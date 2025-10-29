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

    /// <summary>
    /// Set a specific adapter as the primary default route for bypass mode.
    /// This makes the specified adapter the preferred route when Speedify is disconnected.
    /// </summary>
    /// <param name="adapterId">The adapter ID (interface name) to use for routing</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    public async Task<bool> SetPrimaryRouteAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("SetPrimaryRouteAsync is only supported on Linux");
            return false;
        }

        if (string.IsNullOrWhiteSpace(adapterId))
        {
            _logger.LogError("Adapter ID cannot be null or empty");
            return false;
        }

        try
        {
            _logger.LogInformation("Setting primary route for adapter {AdapterId}", adapterId);

            // Get the gateway for the specified interface
            var getGatewayCommand = $"sudo ip route show dev {adapterId} | grep default | awk '{{print $3}}'";
            var processInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{getGatewayCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var getProcess = new Process { StartInfo = processInfo };
            getProcess.Start();
            
            var gateway = (await getProcess.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
            var getError = await getProcess.StandardError.ReadToEndAsync(cancellationToken);
            
            await getProcess.WaitForExitAsync(cancellationToken);

            // If no gateway found for this interface, try getting it from the routing table
            if (string.IsNullOrWhiteSpace(gateway))
            {
                var altCommand = $"sudo ip route | grep 'dev {adapterId}' | grep -v 'linkdown' | head -n1 | awk '{{print $3}}'";
                processInfo.Arguments = $"-c \"{altCommand}\"";
                
                using var altProcess = new Process { StartInfo = processInfo };
                altProcess.Start();
                
                gateway = (await altProcess.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
                await altProcess.WaitForExitAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(gateway))
            {
                _logger.LogError("Could not determine gateway for adapter {AdapterId}", adapterId);
                return false;
            }

            _logger.LogDebug("Gateway for {AdapterId}: {Gateway}", adapterId, gateway);

            // Remove any existing low-metric default routes
            var removeCommand = $"sudo ip route del default metric 50 2>/dev/null || true";
            await RunCommand(removeCommand, cancellationToken);

            // Add new default route with low metric (high priority) for the specified adapter
            var addCommand = $"sudo ip route add default via {gateway} dev {adapterId} metric 50";
            var success = await RunCommand(addCommand, cancellationToken);

            if (success)
            {
                _logger.LogInformation("Successfully set primary route for adapter {AdapterId} via gateway {Gateway}",
                    adapterId, gateway);
            }
            else
            {
                _logger.LogError("Failed to set primary route for adapter {AdapterId}", adapterId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting primary route for adapter {AdapterId}", adapterId);
            return false;
        }
    }

    /// <summary>
    /// Restore default OS routing by removing manual route priorities.
    /// This allows the system's network manager to handle routing automatically.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if successful, false otherwise</returns>
    public async Task<bool> RestoreDefaultRoutingAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("RestoreDefaultRoutingAsync is only supported on Linux");
            return false;
        }

        try
        {
            _logger.LogInformation("Restoring default routing");

            // Remove any manually-added low-metric default routes
            var removeCommand = $"sudo ip route del default metric 50 2>/dev/null || true";
            await RunCommand(removeCommand, cancellationToken);

            // Optionally restart NetworkManager to fully restore automatic routing
            // This is commented out as it's more disruptive, but can be uncommented if needed
            // var restartCommand = "sudo systemctl restart NetworkManager 2>/dev/null || sudo service network-manager restart 2>/dev/null || true";
            // await RunCommand(restartCommand, cancellationToken);

            _logger.LogInformation("Default routing restored");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring default routing");
            return false;
        }
    }
}
