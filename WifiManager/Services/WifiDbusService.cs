using System.Diagnostics;
using System.Threading;
using Tmds.DBus.Protocol;
using WifiManager.DBus;
using WifiManager.Models;

namespace WifiManager.Services;

/// <summary>
/// Low-level DBus communication layer.
/// All NetworkManager DBus calls are encapsulated here.
/// </summary>
public class WifiDbusService : IDisposable
{
    private static readonly TimeSpan DefaultDbusTimeout = TimeSpan.FromSeconds(60);

    private Connection? _connection;
    private string? _wirelessDevicePath;
    private readonly ILogger<WifiDbusService> _logger;
    private readonly SemaphoreSlim _dbusGate = new(1, 1);
    private bool _initialized;
    private bool _disposed;

    public WifiDbusService(ILogger<WifiDbusService> logger) => _logger = logger;

    private static async Task WithTimeout(Task task, TimeSpan timeout, string operation)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            throw new TimeoutException($"{operation} timed out after {timeout.TotalSeconds:0.#}s.");
        await task;
    }

    private static async Task WithTimeout(ValueTask task, TimeSpan timeout, string operation)
        => await WithTimeout(task.AsTask(), timeout, operation);

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, string operation)
    {
        var completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
            throw new TimeoutException($"{operation} timed out after {timeout.TotalSeconds:0.#}s.");
        return await task;
    }

    // ──────────────────────────────────────────────────────────
    // Lifecycle
    // ──────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        await AcquireGateAsync("Initialize");
        try
        {
            _logger.LogInformation("Connecting to system DBus…");
            _connection = new Connection(Address.System!);
            await WithTimeout(_connection.ConnectAsync(), DefaultDbusTimeout, "DBus connect");
            _logger.LogInformation("Connected to system DBus.");

            _wirelessDevicePath = await WithTimeout(FindWirelessDeviceAsync(), DefaultDbusTimeout, "Find WiFi device");
            if (_wirelessDevicePath is null)
                _logger.LogWarning("No WiFi device found via NetworkManager.");
            else
                _logger.LogInformation("WiFi device path: {Path}", _wirelessDevicePath);

            _initialized = true;
        }
        finally
        {
            ReleaseGate("Initialize");
        }
    }

    private Connection Conn
        => _connection ?? throw new InvalidOperationException("DBus not initialised – call InitializeAsync first.");

    public string? WirelessDevicePath => _wirelessDevicePath;

    // ──────────────────────────────────────────────────────────
    // Device discovery
    // ──────────────────────────────────────────────────────────

    private async Task<string?> FindWirelessDeviceAsync()
    {
        var nm = new NetworkManagerProxy(Conn);
        string[] devices;

        try   { devices = await nm.GetAllDevicesAsync(); }
        catch
        {
            try   { devices = await nm.GetDevicesAsync(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cannot enumerate NM devices.");
                return null;
            }
        }

        foreach (var path in devices)
        {
            try
            {
                var dev  = new DeviceProxy(Conn, path);
                var type = await dev.GetDeviceTypeAsync();
                if (type == NMConstants.DeviceTypeWifi)
                    return path;
            }
            catch (Exception ex)
            {
                _logger.LogDebug("Skipping device {P}: {E}", path, ex.Message);
            }
        }
        return null;
    }

    // ──────────────────────────────────────────────────────────
    // Scan
    // ──────────────────────────────────────────────────────────

    public async Task RequestScanAsync()
    {
        if (_wirelessDevicePath is null)
            throw new InvalidOperationException("No wireless device available.");

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Requesting WiFi scan on {Path} as user {User}", _wirelessDevicePath, Environment.UserName);
        try
        {
            _logger.LogDebug("RequestScan: starting fresh connection");
            await RequestScanOnFreshConnectionAsync(_wirelessDevicePath);
            _logger.LogInformation("RequestScan completed for {Path} in {Ms} ms", _wirelessDevicePath, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is TimeoutException or DBusException)
        {
            _logger.LogWarning(ex, "RequestScan timed out/failed; continuing without scan");
        }
        // Give NetworkManager time to collect results
        await Task.Delay(2500);
        _logger.LogInformation("RequestScan: delay complete, proceeding to AP list");
    }

    private static async Task RequestScanOnFreshConnectionAsync(string devicePath)
    {
        using var tempConn = new Connection(Address.System!);
        await tempConn.ConnectAsync();
        var proxy = new WirelessDeviceProxy(tempConn, devicePath);
        await WithTimeout(proxy.RequestScanAsync(), TimeSpan.FromSeconds(10), "RequestScan");
    }

    // ──────────────────────────────────────────────────────────
    // Access-point list
    // ──────────────────────────────────────────────────────────

    public async Task<List<WifiAccessPoint>> GetAccessPointsAsync()
    {
        if (_wirelessDevicePath is null) return [];

        return await GetAccessPointsOnFreshConnectionAsync(_wirelessDevicePath);
    }

    private async Task<List<WifiAccessPoint>> GetAccessPointsOnFreshConnectionAsync(string devicePath)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Fetching access points via fresh DBus connection for {Path}", devicePath);

        using var tempConn = new Connection(Address.System!);
        await WithTimeout(tempConn.ConnectAsync(), DefaultDbusTimeout, "DBus connect (AP list)");

        var wireless = new WirelessDeviceProxy(tempConn, devicePath);
        string[] apPaths;
        try
        {
            _logger.LogDebug("GetAccessPoints: reading AccessPoints property");
            apPaths = await WithTimeout(
                wireless.GetAccessPointsPropertyAsync(),
                TimeSpan.FromSeconds(10),
                "Get AccessPoints property");
        }
        catch
        {
            _logger.LogDebug("GetAccessPoints: property failed; trying GetAllAccessPoints");
            try   { apPaths = await WithTimeout(wireless.GetAllAccessPointsAsync(), TimeSpan.FromSeconds(10), "GetAllAccessPoints"); }
            catch
            {
                _logger.LogDebug("GetAccessPoints: GetAllAccessPoints failed; trying GetAccessPoints");
                apPaths = await WithTimeout(wireless.GetAccessPointsAsync(), TimeSpan.FromSeconds(10), "GetAccessPoints");
            }
        }

        _logger.LogInformation("Access point paths retrieved: {Count} items (in {Ms} ms)", apPaths.Length, sw.ElapsedMilliseconds);

        var result = new List<WifiAccessPoint>();

        foreach (var apPath in apPaths)
        {
            try
            {
                _logger.LogDebug("Reading AP properties for {Path}", apPath);
                var ap         = new AccessPointProxy(tempConn, apPath);
                var ssidBytes  = await ap.GetSsidAsync();
                var bssid      = await ap.GetHwAddressAsync();
                var strength   = await ap.GetStrengthAsync();
                var frequency  = await ap.GetFrequencyAsync();
                var maxBitrate = await ap.GetMaxBitrateAsync();
                var flags      = await ap.GetFlagsAsync();
                var wpaFlags   = await ap.GetWpaFlagsAsync();
                var rsnFlags   = await ap.GetRsnFlagsAsync();

                var ssid     = NMHelper.SsidToString(ssidBytes);
                var security = NMHelper.DetectSecurity(flags, wpaFlags, rsnFlags);

                if (string.IsNullOrWhiteSpace(ssid)) continue;

                result.Add(new WifiAccessPoint
                {
                    SSID       = ssid,
                    BSSID      = bssid,
                    RSSI       = (int)strength,
                    Frequency  = (int)frequency,
                    Security   = security,
                    MaxBitrate = (int)(maxBitrate / 1000), // kbps → Mbps
                    ObjectPath = apPath
                });
            }
            catch (Exception ex)
            {
                _logger.LogDebug("AP {P} error: {E}", apPath, ex.Message);
            }
        }

        return result
            .GroupBy(a => a.SSID)
            .Select(g => g.OrderByDescending(a => a.RSSI).First())
            .OrderByDescending(a => a.RSSI)
            .ToList();
    }


    // ──────────────────────────────────────────────────────────
    // Connect
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Calls NetworkManager.AddAndActivateConnection with full settings.
    /// Returns the active connection object path.
    /// </summary>
    public async Task<string> ConnectAsync(WifiAccessPoint ap, string password, string securityType)
    {
        if (_wirelessDevicePath is null)
            throw new InvalidOperationException("No wireless device available.");

        _logger.LogInformation("Connecting to {SSID} ({Sec}) via fresh DBus connection", ap.SSID, securityType);

        using var tempConn = new Connection(Address.System!);
        await WithTimeout(tempConn.ConnectAsync(), DefaultDbusTimeout, "DBus connect (Connect)");

        var settings = BuildConnectionSettings(ap, password, securityType);
        var nm = new NetworkManagerProxy(tempConn);

        return await WithTimeout(
            nm.AddAndActivateConnectionAsync(
                settings,
                _wirelessDevicePath,
                ap.ObjectPath),   // specificObject = access point path
            TimeSpan.FromSeconds(20),
            "AddAndActivateConnection");
    }

    // ──────────────────────────────────────────────────────────
    // Disconnect
    // ──────────────────────────────────────────────────────────

    public async Task DisconnectAsync(string activeConnectionPath)
    {
        _logger.LogInformation("Disconnecting {P} via fresh DBus connection", activeConnectionPath);
        using var tempConn = new Connection(Address.System!);
        await WithTimeout(tempConn.ConnectAsync(), DefaultDbusTimeout, "DBus connect (Disconnect)");
        var nm = new NetworkManagerProxy(tempConn);
        await WithTimeout(nm.DeactivateConnectionAsync(activeConnectionPath), DefaultDbusTimeout, "DeactivateConnection");
        _logger.LogInformation("Disconnected: {P}", activeConnectionPath);
    }

    // ──────────────────────────────────────────────────────────
    // Current connection info
    // ──────────────────────────────────────────────────────────

    public async Task<WifiConnectionInfo?> GetCurrentConnectionInfoAsync()
    {
        if (_wirelessDevicePath is null) return null;

        return await GetCurrentConnectionInfoOnFreshConnectionAsync(_wirelessDevicePath);
    }

    private async Task<WifiConnectionInfo?> GetCurrentConnectionInfoOnFreshConnectionAsync(string devicePath)
    {
        try
        {
            using var tempConn = new Connection(Address.System!);
            await WithTimeout(tempConn.ConnectAsync(), DefaultDbusTimeout, "DBus connect (CurrentConnection)");

            var dev   = new DeviceProxy(tempConn, devicePath);
            var state = await WithTimeout(dev.GetStateAsync(), DefaultDbusTimeout, "Get Device State");
            if (state != NMConstants.DeviceStateActivated) return null;

            var wireless     = new WirelessDeviceProxy(tempConn, devicePath);
            var activeApPath = await WithTimeout(wireless.GetActiveAccessPointPathAsync(), DefaultDbusTimeout, "Get ActiveAccessPoint");
            if (string.IsNullOrEmpty(activeApPath) || activeApPath == "/") return null;

            var apProxy   = new AccessPointProxy(tempConn, activeApPath);
            var ssidBytes = await WithTimeout(apProxy.GetSsidAsync(), DefaultDbusTimeout, "Get SSID");
            var bssid     = await WithTimeout(apProxy.GetHwAddressAsync(), DefaultDbusTimeout, "Get BSSID");
            var strength  = await WithTimeout(apProxy.GetStrengthAsync(), DefaultDbusTimeout, "Get Strength");
            var frequency = await WithTimeout(apProxy.GetFrequencyAsync(), DefaultDbusTimeout, "Get Frequency");

            var activeConnPath = await WithTimeout(dev.GetActiveConnectionPathAsync(), DefaultDbusTimeout, "Get ActiveConnection");
            var ip4Path        = await WithTimeout(dev.GetIp4ConfigPathAsync(), DefaultDbusTimeout, "Get Ip4Config");

            string ipAddr = "";
            if (!string.IsNullOrEmpty(ip4Path) && ip4Path != "/")
            {
                var ip4 = new IP4ConfigProxy(tempConn, ip4Path);
                ipAddr  = await WithTimeout(ip4.GetFirstIpAddressAsync(), DefaultDbusTimeout, "Get IPv4 AddressData");
            }

            return new WifiConnectionInfo
            {
                SSID                 = NMHelper.SsidToString(ssidBytes),
                BSSID                = bssid,
                RSSI                 = (int)strength,
                Frequency            = (int)frequency,
                IPAddress            = ipAddr,
                IsConnected          = true,
                ActiveConnectionPath = activeConnPath
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug("GetCurrentConnectionInfo: {E}", ex.Message);
            return null;
        }
    }

    private async Task AcquireGateAsync(string op)
    {
        _logger.LogDebug("DBus gate: waiting for {Op}", op);
        var acquired = await _dbusGate.WaitAsync(TimeSpan.FromSeconds(5));
        if (!acquired)
            throw new TimeoutException($"DBus gate timeout in {op}.");
        _logger.LogDebug("DBus gate: acquired for {Op}", op);
    }

    private void ReleaseGate(string op)
    {
        _dbusGate.Release();
        _logger.LogDebug("DBus gate: released for {Op}", op);
    }

    // ──────────────────────────────────────────────────────────
    // Connection settings builder
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the a{sa{sv}} settings dict for AddAndActivateConnection.
    /// Uses VariantValue (the non-obsolete API in Tmds.DBus.Protocol 0.20+).
    /// </summary>
    private static Dictionary<string, Dictionary<string, Variant>> BuildConnectionSettings(
        WifiAccessPoint ap,
        string password,
        string securityType)
    {
        var ssidBytes = System.Text.Encoding.UTF8.GetBytes(ap.SSID);

        // SSID is `ay` (array of bytes).
        var ssidArray = new Tmds.DBus.Protocol.Array<byte>();
        foreach (var b in ssidBytes)
            ssidArray.Add(b);
        Variant ssidVariant = Variant.FromArray(ssidArray);

        var settings = new Dictionary<string, Dictionary<string, Variant>>
        {
            ["connection"] = new()
            {
                ["id"]   = $"WM-{ap.SSID}",
                ["type"] = "802-11-wireless",
                ["uuid"] = Guid.NewGuid().ToString()
            },
            ["802-11-wireless"] = new()
            {
                ["ssid"] = ssidVariant,
                ["mode"] = "infrastructure"
            },
            ["ipv4"] = new()
            {
                ["method"] = "auto"
            },
            ["ipv6"] = new()
            {
                ["method"] = "ignore"
            }
        };

        if (securityType != "Open" && !string.IsNullOrEmpty(password))
        {
            settings["802-11-wireless"]["security"] =
                "802-11-wireless-security";

            var secDict = new Dictionary<string, Variant>();

            switch (securityType)
            {
                case "WPA3":
                    secDict["key-mgmt"] = "sae";
                    secDict["psk"]      = password;
                    break;

                case "WPA2":
                case "WPA":
                    secDict["key-mgmt"] = "wpa-psk";
                    secDict["psk"]      = password;
                    break;

                case "WEP":
                    secDict["key-mgmt"]     = "none";
                    secDict["wep-key0"]     = password;
                    secDict["wep-key-type"] = (uint)1;
                    secDict["auth-alg"]     = "open";
                    break;
            }

            settings["802-11-wireless-security"] = secDict;
        }

        return settings;
    }

    // ──────────────────────────────────────────────────────────
    // Dispose
    // ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _connection?.Dispose();
            _connection = null;
            _disposed   = true;
        }
    }
}
