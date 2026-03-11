// Services/WpaSupplicantService.cs
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using WiFiManager.Models;

namespace WiFiManager.Services;

public class WpaSupplicantService : IDisposable
{
    private readonly string _controlSocketPath;
    private readonly string _clientSocketPath;
    private readonly ILogger<WpaSupplicantService> _logger;
    private const int ReceiveTimeoutMs = 5000;
    private const int BufferSize = 65536;

    public WpaSupplicantService(ILogger<WpaSupplicantService> logger, IConfiguration config)
    {
        _logger = logger;
        _controlSocketPath = config["WpaSupplicant:ControlSocket"]
            ?? "/var/run/wpa_supplicant/wlp0s20f3";
        _clientSocketPath = $"/tmp/wpa_ctrl_{Guid.NewGuid():N}";
    }

    public async Task<List<WiFiNetwork>> ScanNetworksAsync()
    {
        _logger.LogInformation("Bắt đầu quét Wi-Fi...");
        var scanResult = await SendCommandAsync("SCAN");
        if (scanResult == null || (!scanResult.Contains("OK") && !scanResult.Contains("FAIL-BUSY")))
            _logger.LogWarning("Lệnh SCAN trả về: {Result}", scanResult);
        else
            _logger.LogInformation("Lệnh SCAN trả về: {Result}", scanResult?.Trim());

        await Task.Delay(TimeSpan.FromSeconds(3));

        var scanResults = await SendCommandAsync("SCAN_RESULTS");
        if (string.IsNullOrEmpty(scanResults))
        {
            _logger.LogWarning("SCAN_RESULTS rỗng");
            return new List<WiFiNetwork>();
        }

        _logger.LogInformation("Nhận SCAN_RESULTS ({Length} ký tự)", scanResults.Length);
        return ParseScanResults(scanResults);
    }

    public async Task<WiFiStatus> GetStatusAsync()
    {
        var result = await SendCommandAsync("STATUS");
        var status = ParseStatus(result ?? string.Empty);

        if (string.IsNullOrEmpty(status.Interface))
            status.Interface = GetInterfaceNameFromSocketPath();

        if (string.IsNullOrEmpty(status.IPAddress) && !string.IsNullOrEmpty(status.Interface))
            status.IPAddress = TryGetIPv4Address(status.Interface) ?? string.Empty;

        return status;
    }

    public async Task<(bool Success, string Message)> ConnectToNetworkAsync(
        string ssid, string password, string securityType)
    {
        _logger.LogInformation("Đang kết nối tới: {SSID} (Security={Security})", ssid, securityType);
        try
        {
            var networkId = await SendCommandAsync("ADD_NETWORK");
            if (string.IsNullOrEmpty(networkId) || !int.TryParse(networkId.Trim(), out var id))
                return (false, $"Không thể tạo network entry: {networkId}");

            var setSsid = await SendCommandAsync($"SET_NETWORK {id} ssid \"{ssid}\"");
            if (setSsid?.Trim() != "OK") return (false, $"Lỗi SET ssid: {setSsid}");

            if (string.IsNullOrEmpty(password) || securityType == "Open")
            {
                var r = await SendCommandAsync($"SET_NETWORK {id} key_mgmt NONE");
                if (r?.Trim() != "OK") return (false, $"Lỗi key_mgmt: {r}");
            }
            else if (securityType == "WPA3")
            {
                var r1 = await SendCommandAsync($"SET_NETWORK {id} key_mgmt SAE");
                if (r1?.Trim() != "OK") return (false, $"Lỗi key_mgmt SAE: {r1}");
                var r2 = await SendCommandAsync($"SET_NETWORK {id} sae_password \"{password}\"");
                if (r2?.Trim() != "OK") return (false, $"Lỗi sae_password: {r2}");
            }
            else
            {
                var r = await SendCommandAsync($"SET_NETWORK {id} psk \"{password}\"");
                if (r?.Trim() != "OK") return (false, $"Lỗi PSK: {r}");
            }

            var enable = await SendCommandAsync($"ENABLE_NETWORK {id}");
            if (enable?.Trim() != "OK") return (false, $"Lỗi ENABLE: {enable}");

            var select = await SendCommandAsync($"SELECT_NETWORK {id}");
            if (select?.Trim() != "OK") return (false, $"Lỗi SELECT: {select}");

            return (true, $"Đã gửi lệnh kết nối tới '{ssid}'. Vui lòng chờ...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi kết nối {SSID}", ssid);
            return (false, $"Lỗi: {ex.Message}");
        }
    }

    public async Task<bool> DisconnectAsync()
    {
        var result = await SendCommandAsync("DISCONNECT");
        return result?.Trim() == "OK";
    }

    /// <summary>
    /// Gửi lệnh tới wpa_supplicant qua Unix DGRAM socket.
    ///
    /// QUAN TRỌNG: Dùng SendTo() thay vì Connect() + Send().
    /// - Connect() trên DGRAM Unix socket yêu cầu quyền write vào THƯ MỤC chứa socket
    ///   → lỗi Permission denied (13) dù user đã có group netdev
    /// - SendTo() chỉ cần quyền write vào FILE socket (srwxrwx--- group netdev) → OK
    /// </summary>
    public async Task<string?> SendCommandAsync(string command)
    {
        var clientPath = $"/tmp/wpa_ctrl_{Guid.NewGuid():N}";
        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);

        try
        {
            if (!File.Exists(_controlSocketPath))
            {
                _logger.LogError("Control socket không tồn tại: {Path}", _controlSocketPath);
                return null;
            }

            // Bind client socket để wpa_supplicant biết địa chỉ gửi response về
            socket.Bind(new UnixDomainSocketEndPoint(clientPath));

            // Dùng SendTo() - không cần Connect()
            var serverEndPoint = new UnixDomainSocketEndPoint(_controlSocketPath);
            var commandBytes = Encoding.ASCII.GetBytes(command);
            await socket.SendToAsync(commandBytes, SocketFlags.None, serverEndPoint);

            _logger.LogDebug("Gửi: {Command}", command);

            // Nhận phản hồi
            var buffer = new byte[BufferSize];
            using var cts = new CancellationTokenSource(ReceiveTimeoutMs);
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cts.Token);
            var response = Encoding.ASCII.GetString(buffer, 0, received);

            _logger.LogDebug("Nhận ({Bytes}b): {Response}",
                received, response.Length > 200 ? response[..200] + "..." : response);

            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout lệnh: {Command}", command);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi gửi lệnh '{Command}'", command);
            return null;
        }
        finally
        {
            if (File.Exists(clientPath)) File.Delete(clientPath);
        }
    }

    /// <summary>
    /// Parse output SCAN_RESULTS thành danh sách WiFiNetwork.
    /// Format mỗi dòng (tab-separated):
    ///   BSSID \t Frequency \t SignalLevel \t Flags \t SSID
    ///   aa:bb:cc:dd:ee:ff  2412  -55  [WPA2-PSK-CCMP][ESS]  MyNetwork
    /// </summary>
    public static List<WiFiNetwork> ParseScanResults(string scanOutput)
    {
        var networks = new List<WiFiNetwork>();
        if (string.IsNullOrWhiteSpace(scanOutput)) return networks;

        foreach (var line in scanOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("bssid", StringComparison.OrdinalIgnoreCase)) continue;
            var parts = line.Split('\t');
            if (parts.Length < 5) continue;
            try
            {
                var network = new WiFiNetwork
                {
                    BSSID = parts[0].Trim(),
                    Frequency = int.TryParse(parts[1].Trim(), out var freq) ? freq : 0,
                    SignalLevel = int.TryParse(parts[2].Trim(), out var sig) ? sig : -100,
                    Flags = parts[3].Trim(),
                    SSID = string.Join("\t", parts[4..]).Trim()
                };
                if (!string.IsNullOrEmpty(network.SSID))
                    networks.Add(network);
            }
            catch { continue; }
        }

        return networks.OrderByDescending(n => n.SignalLevel).ToList();
    }

    /// <summary>
    /// Parse output STATUS thành WiFiStatus.
    /// Mỗi dòng dạng key=value.
    /// </summary>
    public static WiFiStatus ParseStatus(string statusOutput)
    {
        var status = new WiFiStatus();
        if (string.IsNullOrWhiteSpace(statusOutput)) return status;

        foreach (var line in statusOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;
            var key = line[..eqIdx].Trim().ToLowerInvariant();
            var value = line[(eqIdx + 1)..].Trim();
            switch (key)
            {
                case "wpa_state": status.WpaState = value; break;
                case "ssid": status.SSID = value; break;
                case "bssid": status.BSSID = value; break;
                case "ip_address": status.IPAddress = value; break;
                case "ifname": status.Interface = value; break;
            }
        }
        return status;
    }

    private string GetInterfaceNameFromSocketPath()
        => Path.GetFileName(_controlSocketPath) ?? string.Empty;

    private static string? TryGetIPv4Address(string interfaceName)
    {
        try
        {
            var ni = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Name == interfaceName);
            if (ni == null) return null;

            var ip = ni.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);

            return ip?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (File.Exists(_clientSocketPath)) File.Delete(_clientSocketPath);
    }
}
