// Services/DhcpClientService.cs
// DHCP client runner (dhclient/udhcpc/others) to obtain IP after Wi-Fi association.

using System.Diagnostics;

namespace WiFiManager.Services;

public class DhcpClientService
{
    private readonly ILogger<DhcpClientService> _logger;
    private readonly bool _enabled;
    private readonly string _command;
    private readonly string _argsTemplate;
    private readonly int _timeoutSeconds;
    private readonly bool _useSudo;

    public DhcpClientService(ILogger<DhcpClientService> logger, IConfiguration config)
    {
        _logger = logger;
        _enabled = config.GetValue<bool>("Dhcp:Enabled", true);
        _command = config["Dhcp:Command"] ?? "dhclient";
        _argsTemplate = config["Dhcp:Args"] ?? "-v {iface}";
        _timeoutSeconds = config.GetValue<int>("Dhcp:TimeoutSeconds", 20);
        _useSudo = config.GetValue<bool>("Dhcp:UseSudo", false);
    }

    public async Task<(bool Success, string Message)> AcquireLeaseAsync(string interfaceName)
    {
        if (!_enabled) return (false, "DHCP bị tắt");
        if (string.IsNullOrWhiteSpace(interfaceName))
            return (false, "Không có tên interface để xin DHCP");

        if (!_useSudo && !IsCommandAvailable(_command))
        {
            var msg = $"Không tìm thấy DHCP client '{_command}'. " +
                      "Hãy cài isc-dhcp-client hoặc cấu hình Dhcp:Command.";
            _logger.LogWarning(msg);
            return (false, msg);
        }

        var args = _argsTemplate.Replace("{iface}", interfaceName);
        var (command, finalArgs) = BuildCommand(args);
        _logger.LogInformation("Chạy DHCP: {Command} {Args}", command, finalArgs);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = finalArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(_timeoutSeconds));
            await process.WaitForExitAsync(cts.Token);

            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();

            if (!string.IsNullOrWhiteSpace(stdout))
                _logger.LogInformation("DHCP stdout: {Output}", stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogWarning("DHCP stderr: {Output}", stderr.Trim());

            if (process.ExitCode == 0)
                return (true, "DHCP OK");

            return (false, $"DHCP exit code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return (false, "DHCP timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi chạy DHCP client");
            return (false, $"Lỗi DHCP: {ex.Message}");
        }
    }

    private (string Command, string Args) BuildCommand(string args)
    {
        if (!_useSudo) return (_command, args);
        return ("sudo", $"-n {_command} {args}");
    }

    private static bool IsCommandAvailable(string command)
    {
        if (Path.IsPathRooted(command))
            return File.Exists(command);

        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);
        return paths.Any(p => File.Exists(Path.Combine(p, command)));
    }
}
