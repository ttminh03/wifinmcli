namespace WifiManager.Models;

public class WifiAccessPoint
{
    public string SSID { get; set; } = string.Empty;
    public string BSSID { get; set; } = string.Empty;
    public int RSSI { get; set; }
    public int Frequency { get; set; }
    public string Security { get; set; } = "Open";
    public int MaxBitrate { get; set; }
    public string ObjectPath { get; set; } = string.Empty;

    public string FrequencyBand => Frequency >= 5000 ? "5 GHz" : "2.4 GHz";

    public string RSSILabel => RSSI switch
    {
        >= 80 => "Excellent",
        >= 60 => "Good",
        >= 40 => "Fair",
        _ => "Weak"
    };

    public string RSSIColor => RSSI switch
    {
        >= 80 => "#22c55e",
        >= 60 => "#84cc16",
        >= 40 => "#f59e0b",
        _ => "#ef4444"
    };
}

public class WifiConnectionInfo
{
    public string SSID { get; set; } = string.Empty;
    public string BSSID { get; set; } = string.Empty;
    public int RSSI { get; set; }
    public int Frequency { get; set; }
    public string IPAddress { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public string ActiveConnectionPath { get; set; } = string.Empty;
}
