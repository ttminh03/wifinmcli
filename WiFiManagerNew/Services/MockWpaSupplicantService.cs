// Services/MockWpaSupplicantService.cs
// Service giả lập để phát triển và kiểm thử mà không cần wpa_supplicant thực.
// Sử dụng khi chạy trên máy phát triển không có Wi-Fi card hoặc wpa_supplicant.

using WiFiManager.Models;

namespace WiFiManager.Services;

/// <summary>
/// Giả lập wpa_supplicant service với dữ liệu mẫu.
/// Dùng trong môi trường phát triển (Development) hoặc khi không có wpa_supplicant.
/// </summary>
public class MockWpaSupplicantService
{
    private WiFiStatus _currentStatus = new() { WpaState = "DISCONNECTED" };
    private string? _connectedSSID;

    /// <summary>Trả về danh sách mạng Wi-Fi giả lập</summary>
    public async Task<List<WiFiNetwork>> ScanNetworksAsync()
    {
        // Giả lập thời gian quét
        await Task.Delay(2000);

        // Dữ liệu mẫu giống output thực của SCAN_RESULTS
        var sampleOutput = """
            bssid / frequency / signal level / flags / ssid
            aa:bb:cc:dd:ee:01	2412	-45	[WPA2-PSK-CCMP][WPS][ESS]	HomeWiFi_2.4G
            bb:cc:dd:ee:ff:02	5180	-52	[WPA2-PSK-CCMP][ESS]	HomeWiFi_5G
            cc:dd:ee:ff:00:03	2437	-68	[WPA3-SAE-CCMP][ESS]	Neighbor_WPA3
            dd:ee:ff:00:11:04	2462	-75	[WPA-PSK-TKIP][WPA2-PSK-CCMP][ESS]	OfficeLobby
            ee:ff:00:11:22:05	5200	-80	[WEP][ESS]	OldPrinter
            ff:00:11:22:33:06	2412	-85	[ESS]	GuestNetwork_Open
            """;

        return WpaSupplicantService.ParseScanResults(sampleOutput);
    }

    /// <summary>Trả về trạng thái kết nối giả lập</summary>
    public async Task<WiFiStatus> GetStatusAsync()
    {
        await Task.Delay(200);
        return _currentStatus;
    }

    /// <summary>Giả lập kết nối mạng</summary>
    public async Task<(bool Success, string Message)> ConnectToNetworkAsync(
        string ssid, string password, string securityType)
    {
        await Task.Delay(2000); // Giả lập thời gian kết nối

        // Giả lập thành công
        _connectedSSID = ssid;
        _currentStatus = new WiFiStatus
        {
            WpaState = "COMPLETED",
            SSID = ssid,
            BSSID = "aa:bb:cc:dd:ee:ff",
            IPAddress = "192.168.1.100"
        };

        return (true, $"Đã kết nối thành công tới '{ssid}' (MOCK)");
    }

    public async Task<bool> DisconnectAsync()
    {
        await Task.Delay(500);
        _currentStatus = new WiFiStatus { WpaState = "DISCONNECTED" };
        _connectedSSID = null;
        return true;
    }

    public async Task<string?> SendCommandAsync(string command)
    {
        await Task.Delay(100);
        return "OK";
    }
}
