using XNetwork.Models;

namespace XNetwork.Services;

public class CudyApControlService(
    ILogger<CudyApControlService> logger,
    WifiService wifiService,
    CudyLuciClient cudyClient,
    CudyApAutomationSettings settings) : BackgroundService
{
    private readonly object _statusLock = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly CudyApControlStatus _status = new();
    private DateTime? _homeSeenSinceUtc;
    private DateTime? _homeMissingSinceUtc;
    private bool? _lastCommandedApDisabled;

    public CudyApAutomationSettings Settings => settings;

    public CudyApControlStatus GetStatus()
    {
        lock (_statusLock)
        {
            return CopyStatus(_status);
        }
    }

    public void RefreshStatusFromSettings()
    {
        UpdateStatusFromSettings();
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken = default)
    {
        await EvaluateAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetApEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await ApplyCudyApStateAsync(enabled, enabled ? "Manual Cudy AP enable requested" : "Manual Cudy AP disable requested", cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UpdateStatusFromSettings();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (settings.Enabled)
                {
                    await EvaluateAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    UpdateStatusFromSettings();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cudy AP automation check failed");
                UpdateStatus(status =>
                {
                    status.LastError = ex.Message;
                    status.Message = $"Cudy AP automation check failed: {ex.Message}";
                    AddEvent(status, status.Message, isError: true);
                });
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(settings.CheckIntervalSeconds, 5, 300)), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        var configurationMessage = GetConfigurationMessage(requireHomeNetwork: true);
        UpdateStatusFromSettings();

        if (configurationMessage != null)
        {
            UpdateStatus(status => status.Message = configurationMessage);
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            UpdateStatus(status => status.Message = "Cudy AP automation requires Linux Wi-Fi scanning on the router.");
            return;
        }

        var networks = await wifiService.ScanNetworksAsync(settings.WifiInterface, rescan: true, cancellationToken, collapseBySsid: false).ConfigureAwait(false);
        var homeNetwork = FindHomeNetwork(networks);
        var now = DateTime.UtcNow;
        var disableThreshold = Math.Clamp(settings.DisableWhenSignalAtLeast, 0, 100);
        var enableThreshold = Math.Clamp(settings.EnableWhenSignalBelow, 0, disableThreshold);

        UpdateStatus(status =>
        {
            status.LastCheckUtc = now;
            status.LastError = null;
            status.DetectedHomeNetwork = homeNetwork?.Ssid;
            status.DetectedHomeBssid = homeNetwork?.Bssid;
            status.DetectedSignal = homeNetwork?.Signal;
            if (homeNetwork != null)
            {
                status.LastHomeNetworkSeenUtc = now;
            }
        });

        if (homeNetwork is { Signal: var signal } && signal >= disableThreshold)
        {
            _homeMissingSinceUtc = null;
            _homeSeenSinceUtc ??= now;
            var seenFor = now - _homeSeenSinceUtc.Value;
            var requiredSeen = TimeSpan.FromSeconds(Math.Max(0, settings.DisableAfterSeenSeconds));

            if (_lastCommandedApDisabled == true)
            {
                UpdateStatus(status => status.Message = $"Home Wi-Fi is nearby ({signal}%); Cudy AP is disabled.");
                return;
            }

            if (seenFor >= requiredSeen)
            {
                await ApplyCudyApStateAsync(false, $"Home Wi-Fi {homeNetwork.Ssid} detected at {signal}%", cancellationToken).ConfigureAwait(false);
                return;
            }

            UpdateStatus(status => status.Message = $"Home Wi-Fi is nearby ({signal}%); waiting {FormatRemaining(requiredSeen - seenFor)} before disabling Cudy AP.");
            return;
        }

        if (homeNetwork is { Signal: var weakSignal } && weakSignal > enableThreshold)
        {
            _homeSeenSinceUtc = null;
            _homeMissingSinceUtc = null;
            UpdateStatus(status => status.Message = $"Home Wi-Fi signal is in the hold band ({weakSignal}%); no Cudy AP change.");
            return;
        }

        _homeSeenSinceUtc = null;
        _homeMissingSinceUtc ??= now;

        if (_lastCommandedApDisabled == true && settings.ReEnableWhenHomeMissing)
        {
            var missingFor = now - _homeMissingSinceUtc.Value;
            var requiredMissing = TimeSpan.FromSeconds(Math.Max(0, settings.EnableAfterMissingSeconds));
            if (missingFor >= requiredMissing)
            {
                var reason = homeNetwork == null
                    ? "Home Wi-Fi is no longer visible"
                    : $"Home Wi-Fi signal dropped to {homeNetwork.Signal}%";
                await ApplyCudyApStateAsync(true, reason, cancellationToken).ConfigureAwait(false);
                return;
            }

            UpdateStatus(status => status.Message = $"Home Wi-Fi is missing or weak; waiting {FormatRemaining(requiredMissing - missingFor)} before re-enabling Cudy AP.");
            return;
        }

        UpdateStatus(status => status.Message = homeNetwork == null
            ? "Configured home Wi-Fi is not visible; no Cudy AP change."
            : $"Home Wi-Fi signal is weak ({homeNetwork.Signal}%); no Cudy AP change.");
    }

    private async Task ApplyCudyApStateAsync(bool enabled, string reason, CancellationToken cancellationToken)
    {
        var configurationMessage = GetConfigurationMessage(requireHomeNetwork: false);
        if (configurationMessage != null)
        {
            throw new InvalidOperationException(configurationMessage);
        }

        await _applyLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateStatus(status =>
            {
                status.IsBusy = true;
                status.Message = enabled ? "Enabling Cudy AP..." : "Disabling Cudy AP...";
            });

            await cudyClient.SetWirelessEnabledAsync(settings, enabled, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            _lastCommandedApDisabled = !enabled;

            UpdateStatus(status =>
            {
                status.IsBusy = false;
                status.IsCudyApDisabled = !enabled;
                status.LastActionUtc = now;
                status.LastError = null;
                status.Message = enabled ? "Cudy AP enabled." : "Cudy AP disabled.";
                AddEvent(status, $"{status.Message} {reason}", isError: false);
            });
        }
        catch (Exception ex)
        {
            UpdateStatus(status =>
            {
                status.IsBusy = false;
                status.LastError = ex.Message;
                status.Message = $"Cudy AP change failed: {ex.Message}";
                AddEvent(status, status.Message, isError: true);
            });
            throw;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    private WifiNetwork? FindHomeNetwork(IEnumerable<WifiNetwork> networks)
    {
        var homeBssid = NormalizeBssid(settings.HomeBssid);
        var homeSsid = settings.HomeSsid.Trim();

        if (!string.IsNullOrWhiteSpace(homeBssid))
        {
            return networks
                .Where(network => NormalizeBssid(network.Bssid) == homeBssid)
                .Where(network => string.IsNullOrWhiteSpace(homeSsid) || string.Equals(network.Ssid, homeSsid, StringComparison.Ordinal))
                .OrderByDescending(network => network.Signal)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(homeSsid))
        {
            return null;
        }

        return networks
            .Where(network => string.Equals(network.Ssid, homeSsid, StringComparison.Ordinal))
            .OrderByDescending(network => network.Signal)
            .FirstOrDefault();
    }

    private string? GetConfigurationMessage(bool requireHomeNetwork)
    {
        if (string.IsNullOrWhiteSpace(settings.ManagementBaseUrl))
        {
            return "Cudy management URL is not configured.";
        }

        if (!HasAdminPassword())
        {
            return "Cudy admin password is not configured.";
        }

        if (!settings.Disable2G && !settings.Disable5G)
        {
            return "No Cudy Wi-Fi bands are selected.";
        }

        if (requireHomeNetwork && string.IsNullOrWhiteSpace(settings.HomeSsid) && string.IsNullOrWhiteSpace(settings.HomeBssid))
        {
            return "Home Wi-Fi SSID or BSSID is not configured.";
        }

        return null;
    }

    private bool HasAdminPassword()
    {
        return !string.IsNullOrEmpty(settings.AdminPassword) ||
               !string.IsNullOrWhiteSpace(settings.AdminPasswordEnvironmentVariable) &&
               !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(settings.AdminPasswordEnvironmentVariable.Trim()));
    }

    private void UpdateStatusFromSettings()
    {
        UpdateStatus(status =>
        {
            status.IsEnabled = settings.Enabled;
            status.IsConfigured = GetConfigurationMessage(requireHomeNetwork: true) == null;
            status.ManagementBaseUrl = string.IsNullOrWhiteSpace(settings.ManagementBaseUrl) ? null : settings.ManagementBaseUrl.Trim();
            if (!settings.Enabled)
            {
                status.Message = "Cudy AP automation is disabled";
            }
        });
    }

    private void UpdateStatus(Action<CudyApControlStatus> update)
    {
        lock (_statusLock)
        {
            update(_status);
        }
    }

    private static void AddEvent(CudyApControlStatus status, string message, bool isError)
    {
        status.RecentEvents.Insert(0, new CudyApControlEvent
        {
            TimestampUtc = DateTime.UtcNow,
            Message = message,
            IsError = isError
        });

        if (status.RecentEvents.Count > 20)
        {
            status.RecentEvents.RemoveRange(20, status.RecentEvents.Count - 20);
        }
    }

    private static CudyApControlStatus CopyStatus(CudyApControlStatus source)
    {
        return new CudyApControlStatus
        {
            IsEnabled = source.IsEnabled,
            IsConfigured = source.IsConfigured,
            IsBusy = source.IsBusy,
            IsCudyApDisabled = source.IsCudyApDisabled,
            Message = source.Message,
            ManagementBaseUrl = source.ManagementBaseUrl,
            DetectedHomeNetwork = source.DetectedHomeNetwork,
            DetectedHomeBssid = source.DetectedHomeBssid,
            DetectedSignal = source.DetectedSignal,
            LastHomeNetworkSeenUtc = source.LastHomeNetworkSeenUtc,
            LastCheckUtc = source.LastCheckUtc,
            LastActionUtc = source.LastActionUtc,
            LastError = source.LastError,
            RecentEvents = source.RecentEvents.Select(item => new CudyApControlEvent
            {
                TimestampUtc = item.TimestampUtc,
                Message = item.Message,
                IsError = item.IsError
            }).ToList()
        };
    }

    private static string NormalizeBssid(string value)
    {
        return value.Trim().Replace("-", ":", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        return remaining.TotalSeconds < 60
            ? $"{Math.Ceiling(remaining.TotalSeconds):N0}s"
            : $"{Math.Ceiling(remaining.TotalMinutes):N0}m";
    }
}
