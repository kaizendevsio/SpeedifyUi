using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;
using XNetwork.Models;

namespace XNetwork.Services;

public class NetworkMonitorService : BackgroundService
{
    private readonly ILogger<NetworkMonitorService> _logger;
    private readonly NetworkMonitorSettings _settings;
    private readonly Dictionary<string, DateTime> _disconnectionTimes = new();
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly SpeedifyService _speedifyService;

    public NetworkMonitorService(
        ILogger<NetworkMonitorService> logger,
        IOptions<NetworkMonitorSettings> settings,
        IHostApplicationLifetime appLifetime,
        SpeedifyService speedifyService)
    {
        _logger = logger;
        _settings = settings.Value;
        _appLifetime = appLifetime;
        _speedifyService = speedifyService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("Network monitor service is disabled");
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("Network monitor service is only supported on Linux");
            return;
        }

        if (_settings.WhitelistedLinks.Count == 0)
        {
            _logger.LogWarning("No network links are whitelisted for monitoring");
            return;
        }

        _logger.LogInformation("Network monitor service started. Monitoring links: {Links}", 
            string.Join(", ", _settings.WhitelistedLinks));

        using var registration = _appLifetime.ApplicationStopping.Register(() =>
        {
            _logger.LogInformation("Application stopping, stopping network monitor service");
        });

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckNetworkLinks(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in network monitor service");
        }
    }

    private async Task CheckNetworkLinks(CancellationToken stoppingToken)
    {
        try
        {
            var adapters = await _speedifyService.GetAdaptersAsync(stoppingToken);
            foreach (var adapter in adapters)
            {
                if (!_settings.WhitelistedLinks.Contains(adapter.Name))
                    continue;

                bool isDisconnected = adapter.State.ToLowerInvariant() == "disconnected";
                if (isDisconnected)
                {
                    if (!_disconnectionTimes.ContainsKey(adapter.Name))
                    {
                        _disconnectionTimes[adapter.Name] = DateTime.UtcNow;
                        _logger.LogWarning("Speedify adapter {Link} is disconnected. Will attempt restart after {Timeout} seconds",
                            adapter.Name, _settings.DownTimeoutSeconds);
                    }
                    else
                    {
                        var downTime = DateTime.UtcNow - _disconnectionTimes[adapter.Name];
                        if (downTime.TotalSeconds >= _settings.DownTimeoutSeconds)
                        {
                            await RestartLink(adapter.Name, stoppingToken);
                            _disconnectionTimes.Remove(adapter.Name);
                        }
                    }
                }
                else
                {
                    if (_disconnectionTimes.ContainsKey(adapter.Name))
                    {
                        _logger.LogInformation("Speedify adapter {Link} is now up", adapter.Name);
                        _disconnectionTimes.Remove(adapter.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking Speedify adapters");
        }
    }

    private async Task RestartLink(string interfaceName, CancellationToken stoppingToken)
    {
        _logger.LogWarning("Attempting to restart network link {Link}", interfaceName);
        
        try
        {
            // Run ip link set down
            var downResult = await RunCommand($"ip link set {interfaceName} down", stoppingToken);
            if (!downResult)
            {
                _logger.LogError("Failed to bring down network link {Link}", interfaceName);
                return;
            }
            
            // Small delay between commands
            await Task.Delay(1000, stoppingToken);
            
            // Run ip link set up
            var upResult = await RunCommand($"ip link set {interfaceName} up", stoppingToken);
            if (!upResult)
            {
                _logger.LogError("Failed to bring up network link {Link}", interfaceName);
                return;
            }
            
            _logger.LogInformation("Successfully restarted network link {Link}", interfaceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting network link {Link}", interfaceName);
        }
    }

    private async Task<bool> RunCommand(string command, CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogDebug("Running command: {Command}", command);
            
            var processInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();
            
            var output = await process.StandardOutput.ReadToEndAsync(stoppingToken);
            var error = await process.StandardError.ReadToEndAsync(stoppingToken);
            
            await process.WaitForExitAsync(stoppingToken);
            
            if (process.ExitCode != 0)
            {
                _logger.LogError("Command failed with exit code {ExitCode}: {Error}", 
                    process.ExitCode, error);
                return false;
            }
            
            if (!string.IsNullOrEmpty(output))
            {
                _logger.LogDebug("Command output: {Output}", output);
            }
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error running command: {Command}", command);
            return false;
        }
    }
}
