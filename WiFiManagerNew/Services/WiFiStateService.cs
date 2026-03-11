// Services/WiFiStateService.cs
// Service quản lý trạng thái ứng dụng Wi-Fi, dùng chung giữa các Blazor component.
// Sử dụng pattern Event-driven để thông báo thay đổi trạng thái tới UI.

using WiFiManager.Models;

namespace WiFiManager.Services;

/// <summary>
/// Service trạng thái trung tâm cho ứng dụng Wi-Fi Manager.
///
/// Pattern: StateService với event OnChange để Blazor component có thể
/// subscribe và re-render khi trạng thái thay đổi.
///
/// Đăng ký là Scoped để mỗi phiên Blazor Server có instance riêng.
/// </summary>
public class WiFiStateService
{
    // Danh sách mạng Wi-Fi từ lần scan gần nhất
    public List<WiFiNetwork> Networks { get; private set; } = new();

    // Trạng thái kết nối hiện tại
    public WiFiStatus CurrentStatus { get; private set; } = new();

    // Đang thực hiện scan không?
    public bool IsScanning { get; private set; }

    // Đang thực hiện kết nối không?
    public bool IsConnecting { get; private set; }

    // Thông báo lỗi hoặc thành công gần nhất
    public string? StatusMessage { get; private set; }
    public bool IsError { get; private set; }

    // Thời gian scan lần cuối
    public DateTime? LastScanTime { get; private set; }

    // Mạng đang được chọn để kết nối
    public WiFiNetwork? SelectedNetwork { get; private set; }

    // Event thông báo trạng thái thay đổi để component re-render
    public event Action? OnChange;

    private readonly WpaSupplicantService? _wpaService;
    private readonly MockWpaSupplicantService? _mockService;
    private readonly DhcpClientService _dhcpService;
    private readonly DnsConfiguratorService _dnsService;
    private readonly ILogger<WiFiStateService> _logger;
    private readonly bool _useMock;

    public WiFiStateService(
        ILogger<WiFiStateService> logger,
        IConfiguration config,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _dhcpService = serviceProvider.GetRequiredService<DhcpClientService>();
        _dnsService = serviceProvider.GetRequiredService<DnsConfiguratorService>();

        // Kiểm tra có dùng mock hay không (từ cấu hình)
        _useMock = config.GetValue<bool>("WpaSupplicant:UseMock", false);

        if (_useMock)
        {
            _mockService = new MockWpaSupplicantService();
            _logger.LogInformation("Đang dùng Mock wpa_supplicant service");
        }
        else
        {
            _wpaService = serviceProvider.GetService<WpaSupplicantService>();
            _logger.LogInformation("Đang dùng wpa_supplicant thực tại socket");
        }
    }

    /// <summary>Scan Wi-Fi và cập nhật danh sách mạng</summary>
    public async Task ScanAsync()
    {
        if (IsScanning) return;

        IsScanning = true;
        _logger.LogInformation("Bắt đầu scan Wi-Fi");
        StatusMessage = "Đang quét Wi-Fi...";
        IsError = false;
        NotifyStateChanged();

        try
        {
            List<WiFiNetwork> networks;

            if (_useMock && _mockService != null)
            {
                _logger.LogInformation("Scan Wi-Fi bằng MOCK service");
                networks = await _mockService.ScanNetworksAsync();
            }
            else if (_wpaService != null)
            {
                _logger.LogInformation("Scan Wi-Fi bằng wpa_supplicant thực");
                networks = await _wpaService.ScanNetworksAsync();
            }
            else
            {
                _logger.LogWarning("Không có service scan Wi-Fi nào được cấu hình");
                networks = new List<WiFiNetwork>();
            }

            Networks = networks;
            LastScanTime = DateTime.Now;
            StatusMessage = $"Tìm thấy {networks.Count} mạng Wi-Fi";
            _logger.LogInformation("Scan hoàn thành: {Count} mạng", networks.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi scan: {ex.Message}";
            IsError = true;
            _logger.LogError(ex, "Lỗi khi scan Wi-Fi");
        }
        finally
        {
            IsScanning = false;
            NotifyStateChanged();
        }
    }

    /// <summary>Cập nhật trạng thái kết nối hiện tại</summary>
    public async Task RefreshStatusAsync()
    {
        try
        {
            WiFiStatus status;

            if (_useMock && _mockService != null)
                status = await _mockService.GetStatusAsync();
            else if (_wpaService != null)
                status = await _wpaService.GetStatusAsync();
            else
                status = new WiFiStatus();

            CurrentStatus = status;
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi lấy trạng thái kết nối");
        }
    }

    /// <summary>Kết nối tới mạng được chọn</summary>
    public async Task ConnectAsync(string ssid, string password, string securityType)
    {
        if (IsConnecting) return;

        IsConnecting = true;
        _logger.LogInformation("Bắt đầu kết nối tới SSID '{SSID}' (Security={Security})", ssid, securityType);
        StatusMessage = $"Đang kết nối tới '{ssid}'...";
        IsError = false;
        NotifyStateChanged();

        try
        {
            (bool success, string message) result;

            if (_useMock && _mockService != null)
            {
                _logger.LogInformation("Kết nối bằng MOCK service");
                result = await _mockService.ConnectToNetworkAsync(ssid, password, securityType);
            }
            else if (_wpaService != null)
            {
                _logger.LogInformation("Kết nối bằng wpa_supplicant thực");
                result = await _wpaService.ConnectToNetworkAsync(ssid, password, securityType);
            }
            else
            {
                _logger.LogWarning("Không có service kết nối nào được cấu hình");
                result = (false, "Không có service nào được cấu hình");
            }

            StatusMessage = result.message;
            IsError = !result.success;

            if (result.success)
            {
                // Chờ một chút rồi lấy trạng thái mới
                _logger.LogInformation("Đã gửi lệnh kết nối, chờ cập nhật trạng thái");
                await Task.Delay(3000);
                await RefreshStatusAsync();

                if (CurrentStatus.IsConnected && string.IsNullOrEmpty(CurrentStatus.IPAddress))
                {
                    _logger.LogWarning("Đã kết nối Wi-Fi nhưng chưa có IP, thử xin DHCP");
                    var dhcpResult = await _dhcpService.AcquireLeaseAsync(CurrentStatus.Interface);
                    if (dhcpResult.Success)
                    {
                        await Task.Delay(1500);
                        await RefreshStatusAsync();
                    }

                    if (string.IsNullOrEmpty(CurrentStatus.IPAddress))
                    {
                        StatusMessage = "Đã kết nối Wi-Fi nhưng chưa nhận IP (DHCP)";
                        IsError = false;
                        if (!dhcpResult.Success)
                            StatusMessage = $"DHCP thất bại: {dhcpResult.Message}";
                    }
                }

                if (CurrentStatus.IsConnected && !string.IsNullOrEmpty(CurrentStatus.IPAddress))
                {
                    _logger.LogInformation("Đã có IP, cấu hình DNS cho interface {Iface}", CurrentStatus.Interface);
                    var dnsResult = await _dnsService.ConfigureAsync(CurrentStatus.Interface);
                    if (!dnsResult.Success)
                        _logger.LogWarning("Cấu hình DNS thất bại: {Message}", dnsResult.Message);
                }
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi kết nối: {ex.Message}";
            IsError = true;
            _logger.LogError(ex, "Lỗi khi kết nối tới {SSID}", ssid);
        }
        finally
        {
            IsConnecting = false;
            SelectedNetwork = null;
            NotifyStateChanged();
        }
    }

    /// <summary>Ngắt kết nối</summary>
    public async Task DisconnectAsync()
    {
        try
        {
            if (_useMock && _mockService != null)
            {
                _logger.LogInformation("Ngắt kết nối bằng MOCK service");
                await _mockService.DisconnectAsync();
            }
            else
            {
                _logger.LogInformation("Ngắt kết nối bằng wpa_supplicant thực");
                await _wpaService!.DisconnectAsync();
            }

            StatusMessage = "Đã ngắt kết nối";
            IsError = false;
            _logger.LogInformation("Ngắt kết nối thành công");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi ngắt kết nối: {ex.Message}";
            IsError = true;
        }
        NotifyStateChanged();
    }

    /// <summary>Chọn một mạng để kết nối</summary>
    public void SelectNetwork(WiFiNetwork network)
    {
        SelectedNetwork = network;
        NotifyStateChanged();
    }

    /// <summary>Bỏ chọn mạng</summary>
    public void ClearSelection()
    {
        SelectedNetwork = null;
        NotifyStateChanged();
    }

    /// <summary>Thông báo cho tất cả subscriber biết trạng thái đã thay đổi</summary>
    private void NotifyStateChanged() => OnChange?.Invoke();
}
