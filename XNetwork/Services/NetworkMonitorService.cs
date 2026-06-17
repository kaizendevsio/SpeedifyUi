using System.Diagnostics;
using System.Net.NetworkInformation;
using XNetwork.Models;

namespace XNetwork.Services;

public class NetworkMonitorService : BackgroundService
{
    private readonly ILogger<NetworkMonitorService> _logger;
    private readonly NetworkMonitorSettings _settings;
    private readonly Dictionary<string, DateTime> _disconnectionTimes = new();
    private readonly Dictionary<string, Queue<DateTime>> _restartAttempts = new();
    private readonly Dictionary<string, DateTime> _restartSuppressedUntil = new();
    private readonly Dictionary<string, DateTime> _lastSuppressionLog = new();
    private readonly Dictionary<string, string> _lastObservedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastRestartAttemptUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _lastRestartErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly IHostApplicationLifetime _appLifetime;

    public NetworkMonitorService(
        ILogger<NetworkMonitorService> logger,
        NetworkMonitorSettings settings,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _settings = CopySettings(settings);
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("Network monitor service is only supported on Linux");
            return;
        }

        _logger.LogInformation("Network monitor service started");

        using var registration = _appLifetime.ApplicationStopping.Register(() =>
        {
            _logger.LogInformation("Application stopping, stopping network monitor service");
        });

        try
        {
            var loggedDisabled = false;
            var loggedNoLinks = false;

            while (!stoppingToken.IsCancellationRequested)
            {
                var settings = GetSettings();
                if (!settings.Enabled)
                {
                    if (!loggedDisabled)
                    {
                        _logger.LogInformation("Network monitor service is disabled");
                        loggedDisabled = true;
                    }

                    loggedNoLinks = false;
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                loggedDisabled = false;
                if (settings.WhitelistedLinks.Count == 0)
                {
                    if (!loggedNoLinks)
                    {
                        _logger.LogWarning("No network links are whitelisted for monitoring");
                        loggedNoLinks = true;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                loggedNoLinks = false;
                await CheckNetworkLinks(settings, stoppingToken);
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

    private async Task CheckNetworkLinks(NetworkMonitorSettings settings, CancellationToken stoppingToken)
    {
        try
        {
            foreach (var link in settings.WhitelistedLinks)
            {
                var state = GetInterfaceState(link);
                var isDisconnected = IsInterfaceDown(state);
                var shouldRestart = false;
                if (isDisconnected)
                {
                    lock (_stateLock)
                    {
                        _lastObservedStates[link] = state;
                        if (!_disconnectionTimes.ContainsKey(link))
                        {
                            _disconnectionTimes[link] = DateTime.UtcNow;
                            _logger.LogWarning(
                                "Network link {Link} is down ({State}). Will attempt restart after {Timeout} seconds",
                                link,
                                state,
                                settings.DownTimeoutSeconds);
                        }
                        else
                        {
                            var downTime = DateTime.UtcNow - _disconnectionTimes[link];
                            shouldRestart = downTime.TotalSeconds >= settings.DownTimeoutSeconds && CanAttemptRestartLocked(link, settings);
                        }
                    }

                    if (shouldRestart)
                    {
                        await RestartLink(link, stoppingToken);
                        lock (_stateLock)
                        {
                            _disconnectionTimes.Remove(link);
                        }
                    }
                }
                else
                {
                    lock (_stateLock)
                    {
                        _lastObservedStates[link] = state;
                        if (_disconnectionTimes.ContainsKey(link))
                        {
                            _logger.LogInformation("Network link {Link} is now up", link);
                            _disconnectionTimes.Remove(link);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking network links");
        }
    }

    private static string GetInterfaceState(string interfaceName)
    {
        var interfacePath = Path.Combine("/sys/class/net", interfaceName);
        var operStatePath = Path.Combine(interfacePath, "operstate");
        var carrierPath = Path.Combine(interfacePath, "carrier");

        if (!Directory.Exists(interfacePath))
        {
            return "missing";
        }

        var operState = ReadSysfsValueOrUnknown(operStatePath);
        var carrier = ReadSysfsValueOrUnknown(carrierPath);

        return carrier == "0" && !string.Equals(operState, "up", StringComparison.OrdinalIgnoreCase)
            ? "no-carrier"
            : operState;
    }

    private static string ReadSysfsValueOrUnknown(string path)
    {
        if (!File.Exists(path))
        {
            return "unknown";
        }

        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (IOException)
        {
            return "unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "unknown";
        }
    }

    private static bool IsInterfaceDown(string state)
    {
        return state.Equals("missing", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("down", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("dormant", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("lowerlayerdown", StringComparison.OrdinalIgnoreCase) ||
               state.Equals("no-carrier", StringComparison.OrdinalIgnoreCase);
    }

    private bool CanAttemptRestartLocked(string interfaceName, NetworkMonitorSettings settings)
    {
        if (settings.MaxRestartAttemptsPerHour <= 0)
        {
            _lastRestartAttemptUtc[interfaceName] = DateTime.UtcNow;
            return true;
        }

        var now = DateTime.UtcNow;
        if (_restartSuppressedUntil.TryGetValue(interfaceName, out var suppressedUntil))
        {
            if (suppressedUntil > now)
            {
                if (!_lastSuppressionLog.TryGetValue(interfaceName, out var lastLog) || now - lastLog > TimeSpan.FromMinutes(1))
                {
                    _logger.LogWarning(
                        "Suppressing restart of {Link} until {SuppressedUntil:u}; restart attempts are not restoring carrier",
                        interfaceName,
                        suppressedUntil);
                    _lastSuppressionLog[interfaceName] = now;
                }

                return false;
            }

            _restartSuppressedUntil.Remove(interfaceName);
            _lastSuppressionLog.Remove(interfaceName);
        }

        var attempts = GetRestartAttemptsLocked(interfaceName);
        while (attempts.Count > 0 && now - attempts.Peek() > TimeSpan.FromHours(1))
        {
            attempts.Dequeue();
        }

        var maxAttempts = settings.MaxRestartAttemptsPerHour;
        if (attempts.Count >= maxAttempts)
        {
            var cooldown = TimeSpan.FromMinutes(Math.Max(1, settings.RestartCooldownMinutes));
            _restartSuppressedUntil[interfaceName] = now.Add(cooldown);
            _lastSuppressionLog[interfaceName] = now;
            _logger.LogWarning(
                "Suppressing restart of {Link} for {CooldownMinutes} minutes after {AttemptCount} restart attempts in the last hour",
                interfaceName,
                Math.Round(cooldown.TotalMinutes),
                attempts.Count);
            return false;
        }

        attempts.Enqueue(now);
        _lastRestartAttemptUtc[interfaceName] = now;
        return true;
    }

    private Queue<DateTime> GetRestartAttemptsLocked(string interfaceName)
    {
        if (!_restartAttempts.TryGetValue(interfaceName, out var attempts))
        {
            attempts = new Queue<DateTime>();
            _restartAttempts[interfaceName] = attempts;
        }

        return attempts;
    }

    public NetworkMonitorStatus GetStatus()
    {
        lock (_stateLock)
        {
            var settings = CopySettings(_settings);
            var now = DateTime.UtcNow;
            var links = settings.WhitelistedLinks.Select(link =>
            {
                var attempts = _restartAttempts.TryGetValue(link, out var restartAttempts)
                    ? restartAttempts.Count(attempt => now - attempt <= TimeSpan.FromHours(1))
                    : 0;

                return new NetworkLinkMonitorStatus
                {
                    Name = link,
                    State = _lastObservedStates.GetValueOrDefault(link),
                    DisconnectedSinceUtc = _disconnectionTimes.TryGetValue(link, out var disconnectedSince) ? disconnectedSince : null,
                    RestartSuppressedUntilUtc = _restartSuppressedUntil.TryGetValue(link, out var suppressedUntil) ? suppressedUntil : null,
                    LastRestartAttemptUtc = _lastRestartAttemptUtc.TryGetValue(link, out var lastRestartAttempt) ? lastRestartAttempt : null,
                    LastRestartError = _lastRestartErrors.GetValueOrDefault(link),
                    RestartAttemptsInLastHour = attempts
                };
            }).ToList();

            return new NetworkMonitorStatus
            {
                IsEnabled = settings.Enabled,
                DownTimeoutSeconds = settings.DownTimeoutSeconds,
                Links = links
            };
        }
    }

    public NetworkMonitorSettings GetSettings()
    {
        lock (_stateLock)
        {
            return CopySettings(_settings);
        }
    }

    public void UpdateSettings(NetworkMonitorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var updatedSettings = CopySettings(settings);
        lock (_stateLock)
        {
            var activeLinks = new HashSet<string>(updatedSettings.WhitelistedLinks, StringComparer.OrdinalIgnoreCase);
            foreach (var link in _settings.WhitelistedLinks.Where(link => !activeLinks.Contains(link)).ToList())
            {
                ClearLinkStateLocked(link);
            }

            _settings.Enabled = updatedSettings.Enabled;
            _settings.WhitelistedLinks = updatedSettings.WhitelistedLinks;
            _settings.DownTimeoutSeconds = updatedSettings.DownTimeoutSeconds;
            _settings.MaxRestartAttemptsPerHour = updatedSettings.MaxRestartAttemptsPerHour;
            _settings.RestartCooldownMinutes = updatedSettings.RestartCooldownMinutes;
        }

        _logger.LogInformation(
            "Network monitor settings updated. Enabled: {Enabled}. Monitoring links: {Links}",
            updatedSettings.Enabled,
            string.Join(", ", updatedSettings.WhitelistedLinks));
    }

    private void ClearLinkStateLocked(string link)
    {
        _disconnectionTimes.Remove(link);
        _restartAttempts.Remove(link);
        _restartSuppressedUntil.Remove(link);
        _lastSuppressionLog.Remove(link);
        _lastObservedStates.Remove(link);
        _lastRestartAttemptUtc.Remove(link);
        _lastRestartErrors.Remove(link);
    }

    private static NetworkMonitorSettings CopySettings(NetworkMonitorSettings settings)
    {
        return new NetworkMonitorSettings
        {
            Enabled = settings.Enabled,
            WhitelistedLinks = NormalizeLinks(settings.WhitelistedLinks),
            DownTimeoutSeconds = Math.Clamp(settings.DownTimeoutSeconds, 5, 300),
            MaxRestartAttemptsPerHour = Math.Max(0, settings.MaxRestartAttemptsPerHour),
            RestartCooldownMinutes = Math.Max(1, settings.RestartCooldownMinutes)
        };
    }

    private static List<string> NormalizeLinks(IEnumerable<string>? links)
    {
        return links?
            .Select(link => link.Trim())
            .Where(link => !string.IsNullOrWhiteSpace(link))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? new List<string>();
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
                lock (_stateLock)
                {
                    _lastRestartErrors[interfaceName] = "Failed to bring link down.";
                }
                return;
            }
            
            // Small delay between commands
            await Task.Delay(1000, stoppingToken);
            
            // Run ip link set up
            var upResult = await RunCommand($"ip link set {interfaceName} up", stoppingToken);
            if (!upResult)
            {
                _logger.LogError("Failed to bring up network link {Link}", interfaceName);
                lock (_stateLock)
                {
                    _lastRestartErrors[interfaceName] = "Failed to bring link up.";
                }
                return;
            }
            
            lock (_stateLock)
            {
                _lastRestartErrors[interfaceName] = null;
            }
            _logger.LogInformation("Successfully restarted network link {Link}", interfaceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restarting network link {Link}", interfaceName);
            lock (_stateLock)
            {
                _lastRestartErrors[interfaceName] = ex.Message;
            }
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
    /// Get the gateway IP address for a specific network adapter.
    /// </summary>
    /// <param name="adapterId">The adapter ID (interface name)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The gateway IP address, or null if not found</returns>
    public async Task<string?> GetGatewayAsync(string adapterId, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            _logger.LogWarning("GetGatewayAsync is only supported on Linux");
            return null;
        }

        if (string.IsNullOrWhiteSpace(adapterId))
        {
            _logger.LogError("Adapter ID cannot be null or empty");
            return null;
        }

        try
        {
            _logger.LogDebug("Getting gateway for adapter {AdapterId}", adapterId);
            
            // Get the gateway for the specified interface - try without sudo first
            var getGatewayCommand = $"ip route show dev {adapterId} | grep default | awk '{{print $3}}'";
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
            var stderr = await getProcess.StandardError.ReadToEndAsync(cancellationToken);
            await getProcess.WaitForExitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                _logger.LogDebug("Gateway lookup stderr for {AdapterId}: {Error}", adapterId, stderr);
            }

            // If no gateway found for this interface, try getting it from the routing table
            if (string.IsNullOrWhiteSpace(gateway))
            {
                _logger.LogDebug("No default gateway found, trying alternative route lookup for {AdapterId}", adapterId);
                var altCommand = $"ip route | grep 'dev {adapterId}' | grep -v 'linkdown' | head -n1 | awk '{{print $3}}'";
                processInfo.Arguments = $"-c \"{altCommand}\"";
                
                using var altProcess = new Process { StartInfo = processInfo };
                altProcess.Start();
                
                gateway = (await altProcess.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
                await altProcess.WaitForExitAsync(cancellationToken);
            }

            if (string.IsNullOrWhiteSpace(gateway))
            {
                _logger.LogDebug("No gateway found for adapter {AdapterId}", adapterId);
                return null;
            }

            _logger.LogInformation("Found gateway {Gateway} for adapter {AdapterId}", gateway, adapterId);
            return gateway;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting gateway for adapter {AdapterId}", adapterId);
            return null;
        }
    }

    /// <summary>
    /// Checks whether an HTTP host is reachable when traffic is explicitly bound to a Linux interface.
    /// </summary>
    public async Task<bool> CanReachHttpHostViaInterfaceAsync(
        string interfaceName,
        string host,
        int port = 80,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() ||
            string.IsNullOrWhiteSpace(interfaceName) ||
            string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var boundedTimeout = timeout ?? TimeSpan.FromSeconds(2);
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling(boundedTimeout.TotalSeconds));
        var uri = $"http://{host}:{port}/";

        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "curl";
            process.StartInfo.ArgumentList.Add("--interface");
            process.StartInfo.ArgumentList.Add(interfaceName);
            process.StartInfo.ArgumentList.Add("--connect-timeout");
            process.StartInfo.ArgumentList.Add(timeoutSeconds.ToString());
            process.StartInfo.ArgumentList.Add("--max-time");
            process.StartInfo.ArgumentList.Add(timeoutSeconds.ToString());
            process.StartInfo.ArgumentList.Add("--silent");
            process.StartInfo.ArgumentList.Add("--output");
            process.StartInfo.ArgumentList.Add("/dev/null");
            process.StartInfo.ArgumentList.Add("--write-out");
            process.StartInfo.ArgumentList.Add("%{http_code}");
            process.StartInfo.ArgumentList.Add(uri);
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.UseShellExecute = false;

            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var statusCodeText = (await outputTask).Trim();
            _ = await errorTask;

            return process.ExitCode == 0 &&
                   int.TryParse(statusCodeText, out var statusCode) &&
                   statusCode is >= 200 and < 400;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "HTTP probe to {Host}:{Port} via {InterfaceName} failed",
                host,
                port,
                interfaceName);
            return false;
        }
    }

    /// <summary>
    /// Set a specific adapter as the primary default route for diagnostics.
    /// This makes the specified adapter the preferred route until OS routing changes it again.
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
