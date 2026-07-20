using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondClientWatchdogService(
    XBondClientWatchdogSettings settings,
    XBondClientWatchdogSettingsStore settingsStore,
    XBondSnapshotCache snapshotCache,
    XBondPhysicalPathProbeService physicalPathProbeService,
    XBondTrafficEngineService trafficEngineService,
    XBondSettings xbondSettings,
    ILogger<XBondClientWatchdogService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly Queue<DateTimeOffset> _restartHistory = new();
    private int _consecutiveMismatchChecks;
    private DateTimeOffset? _suppressedUntilUtc;
    private DateTimeOffset _serviceStartedAtUtc = DateTimeOffset.UtcNow;
    private XBondClientWatchdogStatus _status = BuildInitialStatus(settings);

    public XBondClientWatchdogSettings Settings => settings;

    public XBondClientWatchdogStatus GetStatus()
    {
        lock (_statusLock)
        {
            return CloneStatus(_status);
        }
    }

    public async Task SaveSettingsAsync(
        XBondClientWatchdogSettings updated,
        CancellationToken cancellationToken = default)
    {
        XBondClientWatchdogSettingsStore.Apply(updated, settings);
        await settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
        UpdateStatus(status =>
        {
            status.Enabled = settings.Enabled;
            status.Message = settings.Enabled
                ? "XBond client watchdog settings saved."
                : "XBond client watchdog is disabled.";
        });
    }

    public async Task<XBondClientWatchdogStatus> RunCheckNowAsync(
        CancellationToken cancellationToken = default)
    {
        await RunCheckAsync(manual: true, cancellationToken).ConfigureAwait(false);
        return GetStatus();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _serviceStartedAtUtc = DateTimeOffset.UtcNow;
        logger.LogInformation(
            "XBond client watchdog started; enabled={Enabled}, interval={IntervalSeconds}s",
            settings.Enabled,
            settings.CheckIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(Math.Clamp(settings.CheckIntervalSeconds, 5, 300));
            UpdateStatus(status => status.NextRunAtUtc = DateTimeOffset.UtcNow + delay);
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                if (settings.Enabled)
                {
                    await RunCheckAsync(manual: false, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "XBond client watchdog loop failed");
                UpdateStatus(status =>
                {
                    status.IsRunning = false;
                    status.LastCompletedAtUtc = DateTimeOffset.UtcNow;
                    status.Message = $"Watchdog check failed: {ex.Message}";
                });
            }
        }
    }

    private async Task RunCheckAsync(bool manual, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            UpdateStatus(status => status.Message = "XBond client watchdog runs only on Linux.");
            return;
        }

        if (!settings.Enabled && !manual)
        {
            return;
        }

        if (!await _checkLock.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            UpdateStatus(status => status.Message = "A watchdog check is already running.");
            return;
        }

        var startedAt = DateTimeOffset.UtcNow;
        try
        {
            UpdateStatus(status =>
            {
                status.Enabled = settings.Enabled;
                status.IsRunning = true;
                status.LastStartedAtUtc = startedAt;
                status.Message = manual ? "Running manual watchdog check." : "Checking XBond tunnel health.";
            });

            if (!manual && IsWithinStartupGrace(_serviceStartedAtUtc, startedAt, settings.PostRestartGraceSeconds))
            {
                var graceEndsAt = _serviceStartedAtUtc.AddSeconds(settings.PostRestartGraceSeconds);
                _consecutiveMismatchChecks = 0;
                UpdateStatus(status =>
                {
                    status.IsRunning = false;
                    status.LastCompletedAtUtc = DateTimeOffset.UtcNow;
                    status.ConsecutiveMismatchChecks = 0;
                    status.Message = $"Watchdog startup grace is active until {graceEndsAt:O}.";
                });
                return;
            }

            var snapshot = await snapshotCache.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var target = ResolveProbeTarget(settings.PhysicalProbeTarget, snapshot.ServerAddress, xbondSettings.PublicTestServerAddress);
            var candidateInterfaces = snapshot.Paths
                .Where(path => path.IsConfigured && path.InterfaceUp)
                .Select(path => path.InterfaceName)
                .Where(XBondPhysicalPathProbeService.IsSafePhysicalInterface)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var probes = await physicalPathProbeService.ProbeAsync(
                candidateInterfaces,
                target,
                settings,
                cancellationToken).ConfigureAwait(false);
            var decision = Evaluate(snapshot, probes, settings, _consecutiveMismatchChecks);
            _consecutiveMismatchChecks = decision.ConsecutiveMismatchChecks;
            PruneRestartHistory(startedAt);

            var suppressed = _suppressedUntilUtc.HasValue && startedAt < _suppressedUntilUtc.Value;
            var rateLimited = _restartHistory.Count >= settings.MaxRestartsPerHour;
            var shouldRestart = settings.Enabled && decision.ShouldRestart && !suppressed && !rateLimited;
            string message;

            if (shouldRestart)
            {
                message = await AttemptRestartAsync(decision, startedAt, cancellationToken).ConfigureAwait(false);
            }
            else if (suppressed)
            {
                message = $"Watchdog restart suppressed until {_suppressedUntilUtc:O} after the previous restart.";
            }
            else if (rateLimited && decision.ShouldRestart)
            {
                message = $"Watchdog restart blocked by the {settings.MaxRestartsPerHour}/hour safety limit.";
            }
            else
            {
                message = decision.Reason;
            }

            UpdateStatus(status =>
            {
                status.Enabled = settings.Enabled;
                status.IsRunning = false;
                status.LastCompletedAtUtc = DateTimeOffset.UtcNow;
                status.ConsecutiveMismatchChecks = _consecutiveMismatchChecks;
                status.RestartsLastHour = _restartHistory.Count;
                status.SuppressedUntilUtc = _suppressedUntilUtc;
                status.TunnelUnhealthy = decision.TunnelUnhealthy;
                status.TunnelRttMs = snapshot.HasTunnelHealth ? snapshot.TunnelRttMs : null;
                status.TunnelLossPercent = snapshot.HasTunnelHealth ? snapshot.TunnelLossPercent : null;
                status.HealthyPhysicalPathCount = decision.HealthyPhysicalPathCount;
                status.ProbeTarget = target;
                status.Message = message;
                status.PathProbes = probes.Select(CloneProbe).ToList();
            });
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task<string> AttemptRestartAsync(
        XBondClientWatchdogDecision decision,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var serviceStatus = await trafficEngineService.RestartAsync(cancellationToken).ConfigureAwait(false);
        var restartSucceeded = serviceStatus.ClientServiceRunning && string.IsNullOrWhiteSpace(serviceStatus.Error);
        var message = restartSucceeded
            ? $"Restarted XBond client: {decision.Reason}"
            : $"XBond client restart failed: {serviceStatus.Error ?? serviceStatus.Message}";

        if (restartSucceeded)
        {
            _restartHistory.Enqueue(startedAt);
            var suppression = TimeSpan.FromSeconds(settings.PostRestartGraceSeconds) >
                              TimeSpan.FromMinutes(settings.RestartCooldownMinutes)
                ? TimeSpan.FromSeconds(settings.PostRestartGraceSeconds)
                : TimeSpan.FromMinutes(settings.RestartCooldownMinutes);
            _suppressedUntilUtc = startedAt + suppression;
            _consecutiveMismatchChecks = 0;
            snapshotCache.Invalidate();
            UpdateStatus(status =>
            {
                status.LastRestartAtUtc = startedAt;
                status.LastRestartReason = decision.Reason;
            });
        }

        logger.LogWarning(
            "{Message}; healthyPhysicalPaths={HealthyPathCount}",
            message,
            decision.HealthyPhysicalPathCount);
        return message;
    }

    public static XBondClientWatchdogDecision Evaluate(
        XBondStatsSnapshot snapshot,
        IReadOnlyCollection<XBondPhysicalPathProbeResult> probes,
        XBondClientWatchdogSettings settings,
        int previousConsecutiveMismatchChecks)
    {
        var tunnelReason = GetTunnelFailureReason(snapshot, settings);
        var tunnelUnhealthy = tunnelReason is not null;
        var healthyPhysicalPaths = probes.Count(probe => probe.Healthy);
        var mismatch = tunnelUnhealthy && healthyPhysicalPaths >= settings.MinimumHealthyPhysicalPaths;
        var consecutive = mismatch ? previousConsecutiveMismatchChecks + 1 : 0;
        var shouldRestart = mismatch && consecutive >= settings.ConsecutiveUnhealthyChecks;

        if (!tunnelUnhealthy)
        {
            return new(false, healthyPhysicalPaths, 0, false, "XBond tunnel health is within configured limits.");
        }

        if (healthyPhysicalPaths < settings.MinimumHealthyPhysicalPaths)
        {
            return new(
                true,
                healthyPhysicalPaths,
                0,
                false,
                $"Tunnel is unhealthy ({tunnelReason}), but no independently healthy physical path was proven; restart skipped.");
        }

        return new(
            true,
            healthyPhysicalPaths,
            consecutive,
            shouldRestart,
            shouldRestart
                ? $"Tunnel remained unhealthy ({tunnelReason}) while {healthyPhysicalPaths} physical path(s) reached the server directly."
                : $"Tunnel/physical mismatch {consecutive}/{settings.ConsecutiveUnhealthyChecks}: {tunnelReason}; {healthyPhysicalPaths} physical path(s) healthy.");
    }

    private static string? GetTunnelFailureReason(
        XBondStatsSnapshot snapshot,
        XBondClientWatchdogSettings settings)
    {
        var statusAge = DateTimeOffset.UtcNow - new DateTimeOffset(
            DateTime.SpecifyKind(snapshot.UpdatedAtUtc, DateTimeKind.Utc));
        if (statusAge > TimeSpan.FromSeconds(settings.TunnelStaleAfterSeconds))
        {
            return $"runtime status file is {Math.Max(0, statusAge.TotalSeconds):0.#} seconds old";
        }

        if (!snapshot.IsRunning)
        {
            return "client runtime is not running";
        }

        if (!snapshot.HasTunnelHealth)
        {
            return "tunnel health is unavailable";
        }

        if (snapshot.RawStatus.Tunnel.LastSuccessAgeMs is { } age &&
            age >= (ulong)settings.TunnelStaleAfterSeconds * 1_000)
        {
            return $"last successful tunnel heartbeat is {age} ms old";
        }

        if (snapshot.TunnelLossPercent >= settings.TunnelLossThresholdPercent)
        {
            return $"tunnel loss is {snapshot.TunnelLossPercent:0.#}%";
        }

        if (snapshot.TunnelRttMs >= settings.TunnelRttThresholdMs)
        {
            return $"tunnel RTT is {snapshot.TunnelRttMs:0.#} ms";
        }

        return null;
    }

    public static bool IsWithinStartupGrace(
        DateTimeOffset serviceStartedAtUtc,
        DateTimeOffset nowUtc,
        int graceSeconds) =>
        nowUtc < serviceStartedAtUtc.AddSeconds(Math.Max(0, graceSeconds));

    public static string ResolveProbeTarget(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var normalized = XBondClientWatchdogSettingsStore.NormalizeHost(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return "45.77.241.247";
    }

    private void PruneRestartHistory(DateTimeOffset now)
    {
        while (_restartHistory.TryPeek(out var restart) && now - restart >= TimeSpan.FromHours(1))
        {
            _restartHistory.Dequeue();
        }
    }

    private void UpdateStatus(Action<XBondClientWatchdogStatus> update)
    {
        lock (_statusLock)
        {
            var clone = CloneStatus(_status);
            update(clone);
            _status = clone;
        }
    }

    private static XBondClientWatchdogStatus BuildInitialStatus(XBondClientWatchdogSettings settings)
    {
        XBondClientWatchdogSettingsStore.Normalize(settings);
        return new XBondClientWatchdogStatus
        {
            Enabled = settings.Enabled,
            Message = settings.Enabled
                ? "XBond client watchdog is waiting for its first check."
                : "XBond client watchdog is disabled."
        };
    }

    private static XBondClientWatchdogStatus CloneStatus(XBondClientWatchdogStatus status) => new()
    {
        Enabled = status.Enabled,
        IsRunning = status.IsRunning,
        LastStartedAtUtc = status.LastStartedAtUtc,
        LastCompletedAtUtc = status.LastCompletedAtUtc,
        NextRunAtUtc = status.NextRunAtUtc,
        LastRestartAtUtc = status.LastRestartAtUtc,
        SuppressedUntilUtc = status.SuppressedUntilUtc,
        ConsecutiveMismatchChecks = status.ConsecutiveMismatchChecks,
        RestartsLastHour = status.RestartsLastHour,
        TunnelUnhealthy = status.TunnelUnhealthy,
        TunnelRttMs = status.TunnelRttMs,
        TunnelLossPercent = status.TunnelLossPercent,
        HealthyPhysicalPathCount = status.HealthyPhysicalPathCount,
        ProbeTarget = status.ProbeTarget,
        Message = status.Message,
        LastRestartReason = status.LastRestartReason,
        PathProbes = status.PathProbes.Select(CloneProbe).ToList()
    };

    private static XBondPhysicalPathProbeResult CloneProbe(XBondPhysicalPathProbeResult probe) => new()
    {
        InterfaceName = probe.InterfaceName,
        Target = probe.Target,
        Responded = probe.Responded,
        Healthy = probe.Healthy,
        AverageRttMs = probe.AverageRttMs,
        LossPercent = probe.LossPercent,
        Error = probe.Error
    };
}
