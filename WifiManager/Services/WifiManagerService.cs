using WifiManager.Models;

namespace WifiManager.Services;

/// <summary>
/// Business-logic service that sits between the Blazor UI and WifiDbusService.
/// Handles caching, retry, auto-refresh, and state management.
/// </summary>
public class WifiManagerService : IDisposable
{
    private readonly WifiDbusService _dbus;
    private readonly ILogger<WifiManagerService> _logger;

    // State
    private List<WifiAccessPoint> _accessPoints = [];
    private WifiConnectionInfo?   _currentConnection;
    private bool _isScanning;
    private bool _isConnecting;
    private string? _lastError;
    private Timer?  _refreshTimer;
    private bool    _initialized;

    // Events for UI reactivity
    public event Action? OnStateChanged;

    public WifiManagerService(WifiDbusService dbus, ILogger<WifiManagerService> logger)
    {
        _dbus   = dbus;
        _logger = logger;
    }

    // ──────────────────────────────────────────────────────────
    // Public state (read-only for UI)
    // ──────────────────────────────────────────────────────────

    public IReadOnlyList<WifiAccessPoint> AccessPoints       => _accessPoints;
    public WifiConnectionInfo?            CurrentConnection  => _currentConnection;
    public bool                           IsScanning         => _isScanning;
    public bool                           IsConnecting       => _isConnecting;
    public string?                        LastError          => _lastError;
    public bool                           IsInitialized      => _initialized;

    // ──────────────────────────────────────────────────────────
    // Initialization
    // ──────────────────────────────────────────────────────────

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        try
        {
            await _dbus.InitializeAsync();
            _initialized = true;
            _lastError = null;

            // Load current connection immediately
            await RefreshCurrentConnectionAsync();

            // Start auto-refresh every 3 seconds
            _refreshTimer = new Timer(
                async _ => await RefreshRssiAsync(),
                null,
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _lastError = $"DBus initialisation failed: {ex.Message}";
            _logger.LogError(ex, "Failed to initialize DBus service.");
        }
        NotifyStateChanged();
    }

    // ──────────────────────────────────────────────────────────
    // Scan
    // ──────────────────────────────────────────────────────────

    public async Task ScanAsync()
    {
        if (_isScanning) return;
        _isScanning = true;
        _lastError  = null;
        _logger.LogInformation("ScanAsync: start");
        _refreshTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        NotifyStateChanged();

        try
        {
            try
            {
                await _dbus.RequestScanAsync();
                _logger.LogInformation("ScanAsync: RequestScan completed");
            }
            catch (Exception ex)
            {
                // Don't block the UI if RequestScan hangs/fails: we can still show cached APs from NM.
                _lastError = $"Scan request failed: {ex.Message}";
                _logger.LogWarning(ex, "RequestScan failed; falling back to retrieving AP list.");
            }

            _logger.LogInformation("ScanAsync: fetching access points");
            _accessPoints = await _dbus.GetAccessPointsAsync();
            _logger.LogInformation("ScanAsync: access points fetched: {Count}", _accessPoints.Count);
        }
        catch (Exception ex)
        {
            _lastError    = $"Scan failed: {ex.Message}";
            _accessPoints = [];
            _logger.LogError(ex, "WiFi scan failed.");
        }
        finally
        {
            _isScanning = false;
            _refreshTimer?.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            NotifyStateChanged();
        }
    }

    // ──────────────────────────────────────────────────────────
    // Connect
    // ──────────────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(WifiAccessPoint ap, string password, string securityType)
    {
        if (_isConnecting) return false;
        _isConnecting = true;
        _lastError    = null;
        _refreshTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        NotifyStateChanged();

        try
        {
            await _dbus.ConnectAsync(ap, password, securityType);
            // Wait and poll for connection to establish
            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(1000);
                await RefreshCurrentConnectionAsync();
                NotifyStateChanged();
                if (_currentConnection is not null && _currentConnection.IsConnected)
                    break;
            }
            return true;
        }
        catch (Exception ex)
        {
            _lastError = $"Connection failed: {ex.Message}";
            _logger.LogError(ex, "WiFi connection failed for {SSID}.", ap.SSID);
            return false;
        }
        finally
        {
            _isConnecting = false;
            _refreshTimer?.Change(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
            NotifyStateChanged();
        }
    }

    // ──────────────────────────────────────────────────────────
    // Disconnect
    // ──────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        if (_currentConnection is null) return;
        _lastError = null;

        try
        {
            await _dbus.DisconnectAsync(_currentConnection.ActiveConnectionPath);
            // Refresh state after disconnect
            await Task.Delay(500);
            await RefreshCurrentConnectionAsync();
            if (_currentConnection is not null && _currentConnection.IsConnected)
                _currentConnection = null;
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            _lastError = $"Disconnect failed: {ex.Message}";
            _logger.LogError(ex, "WiFi disconnect failed.");
        }
        NotifyStateChanged();
    }

    // ──────────────────────────────────────────────────────────
    // Refresh helpers
    // ──────────────────────────────────────────────────────────

    private async Task RefreshCurrentConnectionAsync()
    {
        try
        {
            _currentConnection = await _dbus.GetCurrentConnectionInfoAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("RefreshCurrentConnection: {Err}", ex.Message);
            _currentConnection = null;
        }
    }

    /// <summary>Auto-refresh RSSI every 3 seconds.</summary>
    private async Task RefreshRssiAsync()
    {
        if (_isScanning || _isConnecting) return;
        try
        {
            var updated = await _dbus.GetCurrentConnectionInfoAsync();
            if (updated is not null && _currentConnection is not null)
            {
                // Only update RSSI to avoid flicker
                _currentConnection.RSSI = updated.RSSI;
            }
            else
            {
                _currentConnection = updated;
            }
            NotifyStateChanged();
        }
        catch { /* silent */ }
    }

    private void NotifyStateChanged()
        => OnStateChanged?.Invoke();

    // ──────────────────────────────────────────────────────────
    // Dispose
    // ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
        _dbus.Dispose();
    }
}
