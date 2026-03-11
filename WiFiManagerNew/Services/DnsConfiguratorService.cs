// Services/DnsConfiguratorService.cs
// Configure DNS for an interface using resolvectl (systemd-resolved).

using System.Diagnostics;

namespace WiFiManager.Services;

public class DnsConfiguratorService
{
    private readonly ILogger<DnsConfiguratorService> _logger;
    private readonly bool _enabled;
    private readonly bool _useSudo;
    private readonly string[] _servers;
    private readonly int _timeoutSeconds;

    public DnsConfiguratorService(ILogger<DnsConfiguratorService> logger, IConfiguration config)
    {
        _logger = logger;
        _enabled = config.GetValue<bool>("Dns:Enabled", true);
        _useSudo = config.GetValue<bool>("Dns:UseSudo", true);
        _servers = (config["Dns:Servers"] ?? "8.8.8.8,1.1.1.1")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _timeoutSeconds = config.GetValue<int>("Dns:TimeoutSeconds", 10);
    }

    public async Task<(bool Success, string Message)> ConfigureAsync(string interfaceName)
    {
        if (!_enabled) return (false, "DNS bị tắt");
        if (string.IsNullOrWhiteSpace(interfaceName))
            return (false, "Không có tên interface để cấu hình DNS");
        if (_servers.Length == 0)
            return (false, "Không có DNS servers cấu hình");

        var serversArg = string.Join(' ', _servers);

        var steps = new[]
        {
            BuildCommand($"resolvectl dns {interfaceName} {serversArg}"),
            BuildCommand($"resolvectl default-route {interfaceName} yes"),
            BuildCommand($"resolvectl domain {interfaceName} ~."),
            BuildCommand("resolvectl flush-caches")
        };

        foreach (var (command, args) in steps)
        {
            var result = await RunAsync(command, args);
            if (!result.Success)
                return result;
        }

        return (true, "DNS đã cấu hình");
    }

    private (string Command, string Args) BuildCommand(string args)
    {
        if (!_useSudo) return ("bash", $"-lc \"{args}\"");
        return ("sudo", $"-n {args}");
    }

    private async Task<(bool Success, string Message)> RunAsync(string command, string args)
    {
        _logger.LogInformation("Chạy DNS: {Command} {Args}", command, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = args,
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
                _logger.LogInformation("DNS stdout: {Output}", stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr))
                _logger.LogWarning("DNS stderr: {Output}", stderr.Trim());

            if (process.ExitCode == 0) return (true, "OK");
            return (false, $"DNS exit code {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            return (false, "DNS timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lỗi khi cấu hình DNS");
            return (false, $"Lỗi DNS: {ex.Message}");
        }
    }
}
