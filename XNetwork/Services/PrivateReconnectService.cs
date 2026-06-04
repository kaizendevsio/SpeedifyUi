using XNetwork.Models;

namespace XNetwork.Services;

public class PrivateReconnectService(
    ILogger<PrivateReconnectService> logger,
    SpeedifyService speedifyService,
    PrivateReconnectSettings settings) : BackgroundService
{
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly PrivateReconnectStatus _status = new();
    private DateTime? _nextAttemptUtc;

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
        _nextAttemptUtc = settings.Enabled ? DateTime.UtcNow.AddMinutes(settings.IntervalMinutes) : null;

        UpdateStatus(status =>
        {
            status.IsEnabled = settings.Enabled;
            status.IntervalMinutes = settings.IntervalMinutes;
            status.NextAttemptUtc = _nextAttemptUtc;
            status.Message = settings.Enabled
                ? $"Private reconnect scheduled every {settings.IntervalMinutes} minutes"
                : "Private reconnect is disabled";
        });

        logger.LogInformation(
            "Private reconnect settings updated. Enabled: {Enabled}. Interval: {IntervalMinutes} minutes",
            settings.Enabled,
            settings.IntervalMinutes);
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
                if (!settings.Enabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                var now = DateTime.UtcNow;
                if (!_nextAttemptUtc.HasValue || _nextAttemptUtc.Value > now)
                {
                    var delay = _nextAttemptUtc.HasValue
                        ? _nextAttemptUtc.Value - now
                        : TimeSpan.FromMinutes(settings.IntervalMinutes);
                    await Task.Delay(ClampDelay(delay), stoppingToken).ConfigureAwait(false);
                    continue;
                }

                await RunReconnectAsync("Scheduled private reconnect", stoppingToken).ConfigureAwait(false);
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
            if (settings.Enabled)
            {
                _nextAttemptUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
                UpdateStatus(status => status.NextAttemptUtc = _nextAttemptUtc);
            }

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
        }
    }

    private static PrivateReconnectSettings NormalizeSettings(PrivateReconnectSettings source)
    {
        return new PrivateReconnectSettings
        {
            Enabled = source.Enabled,
            IntervalMinutes = Math.Clamp(source.IntervalMinutes, 5, 1440),
            DelaySeconds = Math.Clamp(source.DelaySeconds, 1, 30)
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

    private static PrivateReconnectStatus CopyStatus(PrivateReconnectStatus source)
    {
        return new PrivateReconnectStatus
        {
            IsEnabled = source.IsEnabled,
            IsRunning = source.IsRunning,
            IntervalMinutes = source.IntervalMinutes,
            LastAttemptUtc = source.LastAttemptUtc,
            LastSuccessUtc = source.LastSuccessUtc,
            NextAttemptUtc = source.NextAttemptUtc,
            LastState = source.LastState,
            LastError = source.LastError,
            Message = source.Message,
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
