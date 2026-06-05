using XNetwork.Models;

namespace XNetwork.Services;

public class PrivateReconnectService(
    ILogger<PrivateReconnectService> logger,
    SpeedifyService speedifyService,
    IConnectionHealthService connectionHealthService,
    PrivateReconnectSettings settings) : BackgroundService
{
    private const int HealthCheckIntervalSeconds = 5;
    private static readonly TimeSpan StaleHealthTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly PrivateReconnectStatus _status = new();
    private DateTime? _nextAttemptUtc;
    private DateTime? _healthDegradedSinceUtc;
    private DateTime? _healthSuppressedUntilUtc;
    private DateTime? _lastHealthTriggerUtc;
    private DateTime? _lastHealthObservationUtc;
    private double? _lastHealthLatencyMs;

    public PrivateReconnectSettings Settings => settings;

    public PrivateReconnectStatus GetStatus()
    {
        lock (_stateLock)
        {
            return CopyStatus(_status);
        }
    }

    public void UpdateSettings(PrivateReconnectSettings updatedSettings)
    {
        ArgumentNullException.ThrowIfNull(updatedSettings);
        var normalized = NormalizeSettings(updatedSettings);

        settings.Enabled = normalized.Enabled;
        settings.IntervalMinutes = normalized.IntervalMinutes;
        settings.DelaySeconds = normalized.DelaySeconds;
        settings.HealthTriggerEnabled = normalized.HealthTriggerEnabled;
        settings.HealthLatencyThresholdMs = normalized.HealthLatencyThresholdMs;
        settings.HealthDegradedSeconds = normalized.HealthDegradedSeconds;
        settings.HealthRecoveryObserveSeconds = normalized.HealthRecoveryObserveSeconds;
        settings.HealthCooldownMinutes = normalized.HealthCooldownMinutes;
        _nextAttemptUtc = settings.Enabled ? DateTime.UtcNow.AddMinutes(settings.IntervalMinutes) : null;

        if (!settings.HealthTriggerEnabled)
        {
            _healthDegradedSinceUtc = null;
            _healthSuppressedUntilUtc = null;
        }

        UpdateStatus(status =>
        {
            status.IsEnabled = settings.Enabled;
            status.IntervalMinutes = settings.IntervalMinutes;
            status.NextAttemptUtc = _nextAttemptUtc;
            status.HealthMessage = settings.HealthTriggerEnabled
                ? "Health trigger is monitoring latency"
                : "Health trigger is disabled";
            status.Message = settings.Enabled
                ? $"Private reconnect scheduled every {settings.IntervalMinutes} minutes"
                : settings.HealthTriggerEnabled
                    ? "Scheduled private reconnect is disabled; health trigger is enabled"
                    : "Private reconnect is disabled";
        });

        logger.LogInformation(
            "Private reconnect settings updated. Enabled: {Enabled}. Interval: {IntervalMinutes} minutes. HealthTriggerEnabled: {HealthTriggerEnabled}. HealthLatencyThresholdMs: {HealthLatencyThresholdMs}",
            settings.Enabled,
            settings.IntervalMinutes,
            settings.HealthTriggerEnabled,
            settings.HealthLatencyThresholdMs);
    }

    public async Task<bool> TriggerNowAsync(string reason = "Manual private reconnect requested", CancellationToken cancellationToken = default)
    {
        return await RunReconnectAsync(reason, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UpdateSettings(settings);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!settings.Enabled && !settings.HealthTriggerEnabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                if (settings.HealthTriggerEnabled)
                {
                    await EvaluateHealthTriggerAsync(stoppingToken).ConfigureAwait(false);
                }

                var now = DateTime.UtcNow;
                if (settings.Enabled && _nextAttemptUtc.HasValue && _nextAttemptUtc.Value <= now)
                {
                    await RunReconnectAsync("Scheduled private reconnect", stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(GetLoopDelay(now), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Private reconnect loop failed");
                UpdateStatus(status =>
                {
                    status.LastError = ex.Message;
                    status.Message = $"Private reconnect loop failed: {ex.Message}";
                    AddEvent(status, status.Message, isError: true);
                });

                if (settings.Enabled && (!_nextAttemptUtc.HasValue || _nextAttemptUtc.Value <= DateTime.UtcNow))
                {
                    _nextAttemptUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
                    UpdateStatus(status => status.NextAttemptUtc = _nextAttemptUtc);
                }
            }
        }
    }

    private async Task EvaluateHealthTriggerAsync(CancellationToken cancellationToken)
    {
        var health = TryGetFreshHealthSnapshot();
        if (health == null)
        {
            _healthDegradedSinceUtc = null;
            UpdateStatus(status =>
            {
                status.HealthDegradedSinceUtc = null;
                status.HealthMessage = connectionHealthService.IsInitialized()
                    ? "Health trigger is waiting for fresh latency samples"
                    : "Health trigger is waiting for latency samples";
            });
            return;
        }

        var now = DateTime.UtcNow;
        _lastHealthLatencyMs = health.Value.latency;
        UpdateStatus(status =>
        {
            status.LastHealthLatencyMs = _lastHealthLatencyMs;
            status.HealthSuppressedUntilUtc = _healthSuppressedUntilUtc;
        });

        if (_healthSuppressedUntilUtc.HasValue && _healthSuppressedUntilUtc.Value > now)
        {
            UpdateStatus(status =>
            {
                status.HealthMessage = $"Health trigger cooling down until {FormatTime(_healthSuppressedUntilUtc)}";
            });
            return;
        }

        if (health.Value.latency <= 0 || health.Value.latency < settings.HealthLatencyThresholdMs)
        {
            var wasDegraded = _healthDegradedSinceUtc.HasValue;
            _healthDegradedSinceUtc = null;
            _healthSuppressedUntilUtc = null;
            UpdateStatus(status =>
            {
                status.HealthDegradedSinceUtc = null;
                status.HealthSuppressedUntilUtc = null;
                status.HealthMessage = $"Latency normal ({health.Value.latency:N0} ms)";
                if (wasDegraded)
                {
                    AddEvent(status, $"Health trigger recovered before reconnect ({health.Value.latency:N0} ms)", isError: false);
                }
            });
            return;
        }

        _healthDegradedSinceUtc ??= now;
        var degradedFor = now - _healthDegradedSinceUtc.Value;
        UpdateStatus(status =>
        {
            status.HealthDegradedSinceUtc = _healthDegradedSinceUtc;
            status.HealthMessage = $"High latency {health.Value.latency:N0} ms for {FormatDuration(degradedFor)}";
        });

        if (degradedFor < TimeSpan.FromSeconds(settings.HealthDegradedSeconds))
        {
            return;
        }

        if (!await IsConnectedToPrivateServerAsync(cancellationToken).ConfigureAwait(false))
        {
            _healthDegradedSinceUtc = null;
            ApplyHealthCooldown("Skipped health reconnect because the current Speedify server is not private or could not be verified", isError: false);
            return;
        }

        _lastHealthTriggerUtc = now;
        var reason = $"Health-triggered private reconnect: latency {health.Value.latency:N0} ms for {FormatDuration(degradedFor)}";
        UpdateStatus(status =>
        {
            status.LastHealthTriggerUtc = _lastHealthTriggerUtc;
            status.HealthMessage = reason;
        });

        var ran = await RunReconnectAsync(reason, cancellationToken).ConfigureAwait(false);
        if (!ran)
        {
            ApplyHealthCooldown("Health-triggered private reconnect did not run; pausing health trigger", isError: false);
            return;
        }

        await ObserveHealthAfterReconnectAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ObserveHealthAfterReconnectAsync(CancellationToken cancellationToken)
    {
        var observeFor = TimeSpan.FromSeconds(settings.HealthRecoveryObserveSeconds);
        var deadline = DateTime.UtcNow.Add(observeFor);

        UpdateStatus(status =>
        {
            status.HealthMessage = $"Observing latency for {FormatDuration(observeFor)} after reconnect";
        });

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var delay = remaining < TimeSpan.FromSeconds(HealthCheckIntervalSeconds)
                ? remaining
                : TimeSpan.FromSeconds(HealthCheckIntervalSeconds);

            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        _lastHealthObservationUtc = DateTime.UtcNow;
        var health = TryGetFreshHealthSnapshot();
        _lastHealthLatencyMs = health?.latency;

        UpdateStatus(status =>
        {
            status.LastHealthObservationUtc = _lastHealthObservationUtc;
            status.LastHealthLatencyMs = _lastHealthLatencyMs;
        });

        if (health.HasValue && health.Value.latency > 0 && health.Value.latency < settings.HealthLatencyThresholdMs)
        {
            _healthDegradedSinceUtc = null;
            _healthSuppressedUntilUtc = null;
            UpdateStatus(status =>
            {
                status.HealthDegradedSinceUtc = null;
                status.HealthSuppressedUntilUtc = null;
                status.HealthMessage = $"Health recovered after reconnect ({health.Value.latency:N0} ms)";
                AddEvent(status, status.HealthMessage, isError: false);
            });
            return;
        }

        var latencyText = health.HasValue ? $"{health.Value.latency:N0} ms" : "no fresh latency sample";
        ApplyHealthCooldown($"Latency still high after health reconnect ({latencyText}); pausing health trigger", isError: false);
    }

    private async Task<bool> IsConnectedToPrivateServerAsync(CancellationToken cancellationToken)
    {
        var state = await speedifyService.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var stateValue = state?.State ?? "unknown";
        UpdateStatus(status => status.LastState = stateValue);

        if (!string.Equals(stateValue, "CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            UpdateStatus(status =>
            {
                status.HealthMessage = $"Health trigger skipped because Speedify state is {stateValue}";
            });
            return false;
        }

        var currentServer = await speedifyService.GetCurrentServerAsync(cancellationToken).ConfigureAwait(false);
        if (currentServer?.IsPrivate == true)
        {
            return true;
        }

        UpdateStatus(status =>
        {
            status.HealthMessage = "Health trigger skipped because the current server is not private";
        });
        return false;
    }

    private (double latency, DateTime lastUpdated)? TryGetFreshHealthSnapshot()
    {
        if (!connectionHealthService.IsInitialized())
        {
            return null;
        }

        var snapshot = connectionHealthService.GetOverallHealth().GetSnapshot();
        if (snapshot.lastUpdated == default ||
            DateTime.UtcNow - snapshot.lastUpdated > StaleHealthTimeout)
        {
            return null;
        }

        return (snapshot.latency, snapshot.lastUpdated);
    }

    private void ApplyHealthCooldown(string message, bool isError)
    {
        _healthDegradedSinceUtc = null;
        _healthSuppressedUntilUtc = DateTime.UtcNow.AddMinutes(settings.HealthCooldownMinutes);

        UpdateStatus(status =>
        {
            status.HealthDegradedSinceUtc = null;
            status.HealthSuppressedUntilUtc = _healthSuppressedUntilUtc;
            status.HealthMessage = $"{message}; cooldown until {FormatTime(_healthSuppressedUntilUtc)}";
            AddEvent(status, status.HealthMessage, isError);
        });
    }

    private async Task<bool> RunReconnectAsync(string reason, CancellationToken cancellationToken)
    {
        if (!await _reconnectLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            UpdateStatus(status =>
            {
                status.Message = "Private reconnect is already running";
                AddEvent(status, status.Message, isError: false);
            });
            return false;
        }

        try
        {
            var now = DateTime.UtcNow;
            UpdateStatus(status =>
            {
                status.IsRunning = true;
                status.LastAttemptUtc = now;
                status.LastError = null;
                status.Message = "Checking Speedify state before private reconnect";
            });

            var state = await speedifyService.GetStateAsync(cancellationToken).ConfigureAwait(false);
            var stateValue = state?.State ?? "unknown";
            UpdateStatus(status => status.LastState = stateValue);

            if (!string.Equals(stateValue, "CONNECTED", StringComparison.OrdinalIgnoreCase))
            {
                var message = $"Skipped private reconnect because Speedify state is {stateValue}";
                logger.LogInformation("{Message}", message);
                UpdateStatus(status =>
                {
                    status.IsRunning = false;
                    status.Message = message;
                    AddEvent(status, message, isError: false);
                });
                return false;
            }

            logger.LogInformation("Starting private reconnect. Reason: {Reason}", reason);
            UpdateStatus(status => status.Message = "Disconnecting before private reconnect");

            await speedifyService.ReconnectPrivateAsync(TimeSpan.FromSeconds(settings.DelaySeconds), cancellationToken).ConfigureAwait(false);

            var completedUtc = DateTime.UtcNow;
            UpdateStatus(status =>
            {
                status.IsRunning = false;
                status.LastSuccessUtc = completedUtc;
                status.LastError = null;
                status.Message = "Private reconnect command completed";
                AddEvent(status, $"{status.Message}: {reason}", isError: false);
            });

            logger.LogInformation("Private reconnect command completed");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Private reconnect failed");
            UpdateStatus(status =>
            {
                status.IsRunning = false;
                status.LastError = ex.Message;
                status.Message = $"Private reconnect failed: {ex.Message}";
                AddEvent(status, status.Message, isError: true);
            });
            return false;
        }
        finally
        {
            _nextAttemptUtc = settings.Enabled
                ? DateTime.UtcNow.AddMinutes(settings.IntervalMinutes)
                : null;
            UpdateStatus(status => status.NextAttemptUtc = _nextAttemptUtc);

            _reconnectLock.Release();
        }
    }

    private void UpdateStatus(Action<PrivateReconnectStatus> update)
    {
        lock (_stateLock)
        {
            update(_status);
            _status.IsEnabled = settings.Enabled;
            _status.IntervalMinutes = settings.IntervalMinutes;
            _status.NextAttemptUtc = _nextAttemptUtc;
            _status.HealthTriggerEnabled = settings.HealthTriggerEnabled;
            _status.HealthLatencyThresholdMs = settings.HealthLatencyThresholdMs;
            _status.HealthDegradedSeconds = settings.HealthDegradedSeconds;
            _status.HealthRecoveryObserveSeconds = settings.HealthRecoveryObserveSeconds;
            _status.HealthCooldownMinutes = settings.HealthCooldownMinutes;
            _status.HealthDegradedSinceUtc = _healthDegradedSinceUtc;
            _status.HealthSuppressedUntilUtc = _healthSuppressedUntilUtc;
            _status.LastHealthTriggerUtc = _lastHealthTriggerUtc;
            _status.LastHealthObservationUtc = _lastHealthObservationUtc;
            _status.LastHealthLatencyMs = _lastHealthLatencyMs;
        }
    }

    private TimeSpan GetLoopDelay(DateTime now)
    {
        TimeSpan delay;

        if (settings.Enabled && _nextAttemptUtc.HasValue)
        {
            delay = _nextAttemptUtc.Value - now;
        }
        else if (settings.Enabled)
        {
            delay = TimeSpan.FromMinutes(settings.IntervalMinutes);
        }
        else
        {
            delay = TimeSpan.FromSeconds(HealthCheckIntervalSeconds);
        }

        if (settings.HealthTriggerEnabled && delay > TimeSpan.FromSeconds(HealthCheckIntervalSeconds))
        {
            delay = TimeSpan.FromSeconds(HealthCheckIntervalSeconds);
        }

        return ClampDelay(delay);
    }

    private static PrivateReconnectSettings NormalizeSettings(PrivateReconnectSettings source)
    {
        return new PrivateReconnectSettings
        {
            Enabled = source.Enabled,
            IntervalMinutes = Math.Clamp(source.IntervalMinutes, 5, 1440),
            DelaySeconds = Math.Clamp(source.DelaySeconds, 1, 30),
            HealthTriggerEnabled = source.HealthTriggerEnabled,
            HealthLatencyThresholdMs = Math.Clamp(source.HealthLatencyThresholdMs, 100, 2000),
            HealthDegradedSeconds = Math.Clamp(source.HealthDegradedSeconds, 30, 1800),
            HealthRecoveryObserveSeconds = Math.Clamp(source.HealthRecoveryObserveSeconds, 10, 300),
            HealthCooldownMinutes = Math.Clamp(source.HealthCooldownMinutes, 5, 240)
        };
    }

    private static TimeSpan ClampDelay(TimeSpan delay)
    {
        if (delay < TimeSpan.FromSeconds(1))
        {
            return TimeSpan.FromSeconds(1);
        }

        return delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay;
    }

    private static void AddEvent(PrivateReconnectStatus status, string message, bool isError)
    {
        status.RecentEvents.Insert(0, new PrivateReconnectEvent
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

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
        {
            return $"{duration.TotalMinutes:N0}m";
        }

        return $"{duration.TotalSeconds:N0}s";
    }

    private static string FormatTime(DateTime? utc)
    {
        return utc.HasValue
            ? utc.Value.ToLocalTime().ToString("g")
            : "not scheduled";
    }

    private static PrivateReconnectStatus CopyStatus(PrivateReconnectStatus source)
    {
        return new PrivateReconnectStatus
        {
            IsEnabled = source.IsEnabled,
            IsRunning = source.IsRunning,
            IntervalMinutes = source.IntervalMinutes,
            HealthTriggerEnabled = source.HealthTriggerEnabled,
            HealthLatencyThresholdMs = source.HealthLatencyThresholdMs,
            HealthDegradedSeconds = source.HealthDegradedSeconds,
            HealthRecoveryObserveSeconds = source.HealthRecoveryObserveSeconds,
            HealthCooldownMinutes = source.HealthCooldownMinutes,
            LastAttemptUtc = source.LastAttemptUtc,
            LastSuccessUtc = source.LastSuccessUtc,
            LastHealthTriggerUtc = source.LastHealthTriggerUtc,
            LastHealthObservationUtc = source.LastHealthObservationUtc,
            NextAttemptUtc = source.NextAttemptUtc,
            HealthDegradedSinceUtc = source.HealthDegradedSinceUtc,
            HealthSuppressedUntilUtc = source.HealthSuppressedUntilUtc,
            LastHealthLatencyMs = source.LastHealthLatencyMs,
            LastState = source.LastState,
            LastError = source.LastError,
            Message = source.Message,
            HealthMessage = source.HealthMessage,
            RecentEvents = source.RecentEvents.Select(item => new PrivateReconnectEvent
            {
                TimestampUtc = item.TimestampUtc,
                Message = item.Message,
                IsError = item.IsError
            }).ToList()
        };
    }

    public override void Dispose()
    {
        _reconnectLock.Dispose();
        base.Dispose();
    }
}
