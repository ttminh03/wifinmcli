using Tmds.DBus.Protocol;
using System.Diagnostics;
using System.Linq;
using WifiManager.DBus;
using WifiManager.Models;

namespace WifiManager.Services;

/// <summary>
/// Low-level DBus communication layer.
/// All NetworkManager DBus calls are encapsulated here.
/// </summary>
public class WifiDbusService : IDisposable
{
    private Connection? _connection;
    private string? _wirelessDevicePath;
    private readonly ILogger<WifiDbusService> _logger;
    private bool _initialized;
    private bool _disposed;

    public WifiDbusService(ILogger<WifiDbusService> logger) => _logger = logger;

    // ──────────────────────────────────────────────────────────
    // Lifecycle
    // ──────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        _logger.LogInformation("Connecting to system DBus…");
        _connection = new Connection(Address.System!);
        await _connection.ConnectAsync();
        _logger.LogInformation("Connected to system DBus.");

        _wirelessDevicePath = await FindWirelessDeviceAsync();
        if (_wirelessDevicePath is null)
            _logger.LogWarning("No WiFi device found via NetworkManager.");
        else
            _logger.LogInformation("WiFi device path: {Path}", _wirelessDevicePath);

        _initialized = true;
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

        var proxy = new WirelessDeviceProxy(Conn, _wirelessDevicePath);
        _logger.LogInformation("Requesting WiFi scan on {Path}", _wirelessDevicePath);
        await proxy.RequestScanAsync();
        _logger.LogInformation("RequestScan sent (no-reply).");
        // Give NetworkManager time to collect results
        await Task.Delay(2500);
    }

    // ──────────────────────────────────────────────────────────
    // Access-point list
    // ──────────────────────────────────────────────────────────

    public async Task<List<WifiAccessPoint>> ScanViaNmcliAsync()
    {
        try
        {
            await RunNmcliAsync(" dev wifi rescan", timeoutSeconds: 10, allowFailure: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "nmcli rescan failed (continuing to list).");
        }

        return await GetAccessPointsViaNmcliAsync(rescan: false);
    }

    public async Task<List<WifiAccessPoint>> GetAccessPointsViaNmcliAsync(bool rescan = false)
    {
        var args = " -t -f SSID,BSSID,SIGNAL,FREQ,SECURITY dev wifi list";
        if (rescan) args += " --rescan yes";

        var output = await RunNmcliAsync(args);
        var result = new List<WifiAccessPoint>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = SplitNmcliLine(line);
            if (fields.Count < 5) continue;

            var ssid = fields[0];
            var bssid = fields[1];
            var signal = ParseInt(fields[2]);
            var freq = ParseInt(fields[3]);
            var securityRaw = fields[4];
            var security = MapSecurity(securityRaw);

            if (string.IsNullOrWhiteSpace(ssid))
                ssid = "(hidden)";

            result.Add(new WifiAccessPoint
            {
                SSID = ssid,
                BSSID = bssid,
                RSSI = signal,
                Frequency = freq,
                Security = security,
                MaxBitrate = 0,
                ObjectPath = ""
            });
        }

        _logger.LogInformation("nmcli returned {Count} access points.", result.Count);
        return result
            .GroupBy(a => a.SSID)
            .Select(g => g.OrderByDescending(a => a.RSSI).First())
            .OrderByDescending(a => a.RSSI)
            .ToList();
    }

    private static async Task<string> RunNmcliAsync(string args)
        => await RunNmcliAsync(args, 10, false);

    private static async Task<string> RunNmcliAsync(string args, int timeoutSeconds, bool allowFailure)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "nmcli",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start nmcli.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        var waitTask = proc.WaitForExitAsync();
        var completed = await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));
        if (completed != waitTask)
        {
            try { proc.Kill(true); } catch { /* ignore */ }
            throw new TimeoutException($"nmcli timed out after {timeoutSeconds} seconds.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0 && !allowFailure)
            throw new InvalidOperationException($"nmcli failed: {stderr.Trim()}");

        return stdout;
    }

    private static List<string> SplitNmcliLine(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var escape = false;

        foreach (var ch in line)
        {
            if (escape)
            {
                current.Append(ch);
                escape = false;
                continue;
            }

            if (ch == '\\')
            {
                escape = true;
                continue;
            }

            if (ch == ':')
            {
                result.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(ch);
        }

        result.Add(current.ToString());
        return result;
    }

    private static int ParseInt(string value)
    {
        var span = value.AsSpan().Trim();
        var end = 0;
        while (end < span.Length && char.IsDigit(span[end])) end++;
        if (end == 0) return 0;
        return int.TryParse(span[..end], out var n) ? n : 0;
    }

    private static string MapSecurity(string securityRaw)
    {
        if (string.IsNullOrWhiteSpace(securityRaw)) return "Open";
        if (securityRaw.Contains("WPA3", StringComparison.OrdinalIgnoreCase)) return "WPA3";
        if (securityRaw.Contains("WPA2", StringComparison.OrdinalIgnoreCase)) return "WPA2";
        if (securityRaw.Contains("WPA", StringComparison.OrdinalIgnoreCase)) return "WPA";
        if (securityRaw.Contains("WEP", StringComparison.OrdinalIgnoreCase)) return "WEP";
        return securityRaw.Trim();
    }

    private async Task<string?> TryGetIpAddressViaNmcliAsync(string? iface)
    {
        if (string.IsNullOrWhiteSpace(iface)) return null;
        try
        {
            var output = await RunNmcliAsync($" -t -f IP4.ADDRESS dev show {iface}", timeoutSeconds: 5, allowFailure: true);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(':', 2);
                if (parts.Length == 2 && parts[0] == "IP4.ADDRESS")
                    return parts[1].Split('/')[0];
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ──────────────────────────────────────────────────────────
    // Connect (nmcli)
    // ──────────────────────────────────────────────────────────

    public async Task<string> ConnectViaNmcliAsync(WifiAccessPoint ap, string password, string securityType)
    {
        var iface = await GetWifiInterfaceViaNmcliAsync();

        var args = " device wifi connect " + QuoteArg(ap.SSID);
        if (!string.IsNullOrWhiteSpace(ap.BSSID))
            args += " bssid " + ap.BSSID;
        if (securityType != "Open" && !string.IsNullOrEmpty(password))
            args += " password " + QuoteArg(password);
        if (!string.IsNullOrWhiteSpace(iface))
            args += " ifname " + iface;

        _logger.LogInformation("Connecting via nmcli: SSID={SSID} BSSID={BSSID} IFACE={IFACE}", ap.SSID, ap.BSSID, iface);

        await RunNmcliAsync(args, timeoutSeconds: 20, allowFailure: false);
        return "nmcli";
    }

    public async Task DisconnectViaNmcliAsync()
    {
        var iface = await GetWifiInterfaceViaNmcliAsync();
        if (string.IsNullOrWhiteSpace(iface))
            throw new InvalidOperationException("No WiFi interface found for disconnect.");

        _logger.LogInformation("Disconnecting via nmcli: IFACE={IFACE}", iface);
        await RunNmcliAsync($" device disconnect {iface}", timeoutSeconds: 10, allowFailure: false);
    }

    public async Task<WifiConnectionInfo?> GetCurrentConnectionInfoViaNmcliAsync()
    {
        string? iface = null;
        string? ssid = null;
        string? bssid = null;
        int rssi = 0;
        int freq = 0;

        try
        {
            var output = await RunNmcliAsync(" -t -f ACTIVE,SSID,BSSID,SIGNAL,FREQ dev wifi list", timeoutSeconds: 5, allowFailure: false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = SplitNmcliLine(line);
                if (fields.Count < 5) continue;
                if (!fields[0].Equals("yes", StringComparison.OrdinalIgnoreCase)) continue;
                ssid  = fields[1];
                bssid = fields[2];
                rssi  = ParseInt(fields[3]);
                freq  = ParseInt(fields[4]);
                break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "nmcli active wifi query failed.");
        }

        if (ssid is null) return null;

        iface = await GetWifiInterfaceViaNmcliAsync();
        var ip = await TryGetIpAddressViaNmcliAsync(iface);

        return new WifiConnectionInfo
        {
            SSID = ssid,
            BSSID = bssid ?? "",
            RSSI = rssi,
            Frequency = freq,
            IPAddress = ip ?? "",
            IsConnected = true,
            ActiveConnectionPath = iface ?? ""
        };
    }

    private async Task<string?> GetWifiInterfaceViaNmcliAsync()
    {
        try
        {
            var output = await RunNmcliAsync(" -t -f DEVICE,TYPE dev status", timeoutSeconds: 5, allowFailure: false);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var fields = SplitNmcliLine(line);
                if (fields.Count >= 2 && fields[1].Equals("wifi", StringComparison.OrdinalIgnoreCase))
                    return fields[0];
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to get wifi interface via nmcli.");
        }
        return null;
    }

    private static string QuoteArg(string value)
    {
        if (value is null) return "\"\"";
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    // ──────────────────────────────────────────────────────────
    // Connect
    // ──────────────────────────────────────────────────────────

    public async Task<string> ConnectAsync(WifiAccessPoint ap, string password, string securityType)
    {
        return await ConnectViaNmcliAsync(ap, password, securityType);
    }

    // ──────────────────────────────────────────────────────────
    // Disconnect
    // ──────────────────────────────────────────────────────────

    public async Task DisconnectAsync(string activeConnectionPath)
    {
        await DisconnectViaNmcliAsync();
    }

    // ──────────────────────────────────────────────────────────
    // Current connection info
    // ──────────────────────────────────────────────────────────

    public async Task<WifiConnectionInfo?> GetCurrentConnectionInfoAsync()
    {
        var info = await GetCurrentConnectionInfoViaNmcliAsync();
        if (info is not null) return info;

        if (_wirelessDevicePath is null) return null;

        try
        {
            var dev   = new DeviceProxy(Conn, _wirelessDevicePath);
            var state = await dev.GetStateAsync();
            if (state != NMConstants.DeviceStateActivated) return null;

            var wireless     = new WirelessDeviceProxy(Conn, _wirelessDevicePath);
            var activeApPath = await wireless.GetActiveAccessPointPathAsync();
            if (string.IsNullOrEmpty(activeApPath) || activeApPath == "/") return null;

            var apProxy   = new AccessPointProxy(Conn, activeApPath);
            var ssidBytes = await apProxy.GetSsidAsync();
            var bssid     = await apProxy.GetHwAddressAsync();
            var strength  = await apProxy.GetStrengthAsync();
            var frequency = await apProxy.GetFrequencyAsync();

            var activeConnPath = await dev.GetActiveConnectionPathAsync();
            var ip4Path        = await dev.GetIp4ConfigPathAsync();

            string ipAddr = "";
            if (!string.IsNullOrEmpty(ip4Path) && ip4Path != "/")
            {
                var ip4 = new IP4ConfigProxy(Conn, ip4Path);
                ipAddr  = await ip4.GetFirstIpAddressAsync();
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

    // ──────────────────────────────────────────────────────────
    // Connection settings builder
    // ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the a{sa{sv}} settings dict for AddAndActivateConnection.
    /// Uses Variant (Tmds.DBus.Protocol 0.20.0 API).
    /// </summary>
    private static Dictionary<string, Dictionary<string, Variant>> BuildConnectionSettings(
        WifiAccessPoint ap,
        string password,
        string securityType)
    {
        var ssidBytes = System.Text.Encoding.UTF8.GetBytes(ap.SSID);

        // ay (byte array) for SSID
        var ssidVariant = new Tmds.DBus.Protocol.Array<byte>(ssidBytes).AsVariant();

        var settings = new Dictionary<string, Dictionary<string, Variant>>
        {
            ["connection"] = new()
            {
                ["id"]   = new Variant($"WM-{ap.SSID}"),
                ["type"] = new Variant("802-11-wireless"),
                ["uuid"] = new Variant(Guid.NewGuid().ToString())
            },
            ["802-11-wireless"] = new()
            {
                ["ssid"] = ssidVariant,
                ["mode"] = new Variant("infrastructure")
            },
            ["ipv4"] = new()
            {
                ["method"] = new Variant("auto")
            },
            ["ipv6"] = new()
            {
                ["method"] = new Variant("ignore")
            }
        };

        if (securityType != "Open" && !string.IsNullOrEmpty(password))
        {
            settings["802-11-wireless"]["security"] =
                new Variant("802-11-wireless-security");

            var secDict = new Dictionary<string, Variant>();

            switch (securityType)
            {
                case "WPA3":
                    secDict["key-mgmt"] = new Variant("sae");
                    secDict["psk"]      = new Variant(password);
                    break;

                case "WPA2":
                case "WPA":
                    secDict["key-mgmt"] = new Variant("wpa-psk");
                    secDict["psk"]      = new Variant(password);
                    break;

                case "WEP":
                    secDict["key-mgmt"]     = new Variant("none");
                    secDict["wep-key0"]     = new Variant(password);
                    secDict["wep-key-type"] = new Variant((uint)1);
                    secDict["auth-alg"]     = new Variant("open");
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
