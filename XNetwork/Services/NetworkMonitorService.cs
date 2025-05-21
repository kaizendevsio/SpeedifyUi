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

    public NetworkMonitorService(
        ILogger<NetworkMonitorService> logger,
        IOptions<NetworkMonitorSettings> settings,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _settings = settings.Value;
        _appLifetime = appLifetime;
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
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            
            foreach (var networkInterface in interfaces)
            {
                // Skip interfaces that are not in the whitelist
                if (!_settings.WhitelistedLinks.Contains(networkInterface.Name))
                {
                    continue;
                }

                bool isUp = networkInterface.OperationalStatus == OperationalStatus.Up;
                
                if (!isUp)
                {
                    // If this is the first time we've seen this interface down, record the time
                    if (!_disconnectionTimes.ContainsKey(networkInterface.Name))
                    {
                        _disconnectionTimes[networkInterface.Name] = DateTime.UtcNow;
                        _logger.LogWarning("Network link {Link} is down. Will attempt restart after {Timeout} seconds",
                            networkInterface.Name, _settings.DownTimeoutSeconds);
                    }
                    else
                    {
                        // Check if it's been down long enough to restart
                        var downTime = DateTime.UtcNow - _disconnectionTimes[networkInterface.Name];
                        if (downTime.TotalSeconds >= _settings.DownTimeoutSeconds)
                        {
                            await RestartLink(networkInterface.Name, stoppingToken);
                            // Reset the timer
                            _disconnectionTimes.Remove(networkInterface.Name);
                        }
                    }
                }
                else
                {
                    // If the interface is up and was previously marked as down, remove it
                    if (_disconnectionTimes.ContainsKey(networkInterface.Name))
                    {
                        _logger.LogInformation("Network link {Link} is now up", networkInterface.Name);
                        _disconnectionTimes.Remove(networkInterface.Name);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking network links");
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
