// Models/WiFiNetwork.cs
// Định nghĩa model cho một mạng Wi-Fi được phát hiện từ wpa_supplicant

namespace WiFiManager.Models;

/// <summary>
/// Đại diện cho một mạng Wi-Fi được quét thấy từ wpa_supplicant.
/// Các trường tương ứng với output của lệnh SCAN_RESULTS.
///
/// Ví dụ output SCAN_RESULTS từ wpa_supplicant:
/// bssid / frequency / signal level / flags / ssid
/// aa:bb:cc:dd:ee:ff	2412	-55	[WPA2-PSK-CCMP][ESS]	MyNetwork
/// 11:22:33:44:55:66	5180	-70	[WPA3-SAE-CCMP][ESS]	HomeWifi
/// </summary>
public class WiFiNetwork
{
    /// <summary>Địa chỉ MAC của điểm truy cập (Access Point)</summary>
    public string BSSID { get; set; } = string.Empty;

    /// <summary>Tần số (MHz): 2412 = 2.4GHz kênh 1, 5180 = 5GHz kênh 36</summary>
    public int Frequency { get; set; }

    /// <summary>Cường độ tín hiệu tính bằng dBm (âm, càng gần 0 càng mạnh)</summary>
    public int SignalLevel { get; set; }

    /// <summary>Tên mạng Wi-Fi (Service Set Identifier)</summary>
    public string SSID { get; set; } = string.Empty;

    /// <summary>Các cờ bảo mật, ví dụ: [WPA2-PSK-CCMP][ESS]</summary>
    public string Flags { get; set; } = string.Empty;

    /// <summary>
    /// Loại bảo mật được xác định từ Flags:
    /// WEP, WPA, WPA2, WPA3, hoặc Open
    /// </summary>
    public string SecurityType => DetermineSecurityType(Flags);

    /// <summary>Băng tần: "2.4 GHz" hoặc "5 GHz"</summary>
    public string Band => Frequency < 3000 ? "2.4 GHz" : "5 GHz";

    /// <summary>
    /// Chất lượng tín hiệu từ 0-100 dựa trên dBm.
    /// Ánh xạ: -30 dBm = 100%, -90 dBm = 0%
    /// </summary>
    public int SignalQuality
    {
        get
        {
            // Công thức: chuyển dBm sang phần trăm
            if (SignalLevel >= -30) return 100;
            if (SignalLevel <= -90) return 0;
            return (int)((SignalLevel + 90) * 100.0 / 60.0);
        }
    }

    /// <summary>Icon biểu thị cường độ tín hiệu</summary>
    public string SignalIcon => SignalQuality switch
    {
        >= 75 => "📶",  // Mạnh
        >= 50 => "📶",  // Trung bình
        >= 25 => "📶",  // Yếu
        _ => "📵"       // Rất yếu
    };

    /// <summary>Màu hiển thị theo cường độ tín hiệu</summary>
    public string SignalColor => SignalQuality switch
    {
        >= 75 => "success",
        >= 50 => "warning",
        >= 25 => "danger",
        _ => "secondary"
    };

    /// <summary>
    /// Xác định loại bảo mật từ chuỗi flags của wpa_supplicant.
    /// Flags ví dụ: [WPA2-PSK-CCMP][WPS][ESS]
    /// </summary>
    private static string DetermineSecurityType(string flags)
    {
        if (string.IsNullOrEmpty(flags)) return "Open";

        // Kiểm tra theo thứ tự ưu tiên từ cao đến thấp
        if (flags.Contains("WPA3", StringComparison.OrdinalIgnoreCase)) return "WPA3";
        if (flags.Contains("WPA2", StringComparison.OrdinalIgnoreCase)) return "WPA2";
        if (flags.Contains("WPA", StringComparison.OrdinalIgnoreCase)) return "WPA";
        if (flags.Contains("WEP", StringComparison.OrdinalIgnoreCase)) return "WEP";

        return "Open"; // Không có mã hóa
    }

    /// <summary>Mạng có yêu cầu mật khẩu không?</summary>
    public bool RequiresPassword => SecurityType != "Open";
}

/// <summary>
/// Trạng thái kết nối Wi-Fi hiện tại lấy từ lệnh STATUS của wpa_supplicant.
/// </summary>
public class WiFiStatus
{
    public string WpaState { get; set; } = "DISCONNECTED";
    public string SSID { get; set; } = string.Empty;
    public string BSSID { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public string Interface { get; set; } = "wlp0s20f3";

    /// <summary>
    /// Trạng thái thân thiện với người dùng.
    /// WPA_STATE từ wpa_supplicant: DISCONNECTED, SCANNING, ASSOCIATING, ASSOCIATED, 4WAY_HANDSHAKE, COMPLETED
    /// </summary>
    public string FriendlyState => WpaState switch
    {
        "COMPLETED" => "Đã kết nối",
        "ASSOCIATING" => "Đang kết nối...",
        "ASSOCIATED" => "Đã liên kết (đang xác thực)...",
        "4WAY_HANDSHAKE" => "Đang xác thực...",
        "SCANNING" => "Đang quét...",
        "DISCONNECTED" => "Chưa kết nối",
        _ => WpaState
    };

    public bool IsConnected => WpaState == "COMPLETED";
    public string StatusColor => IsConnected ? "success" : (WpaState == "DISCONNECTED" ? "secondary" : "warning");
}
