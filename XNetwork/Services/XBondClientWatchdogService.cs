using XNetwork.Models;

namespace XNetwork.Services;

public sealed class XBondClientWatchdogService : BackgroundService
{
    private readonly XBondClientWatchdogSettings settings;
    private readonly XBondClientWatchdogSettingsStore settingsStore;
    private readonly IXBondClientWatchdogStateStore stateStore;
    private readonly XBondSnapshotCache snapshotCache;
    private readonly XBondPhysicalPathProbeService physicalPathProbeService;
    private readonly XBondTrafficEngineService trafficEngineService;
    private readonly XBondSettings xbondSettings;
    private readonly ILogger<XBondClientWatchdogService> logger;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly object _statusLock = new();
    private readonly XBondClientWatchdogState _persistentState;
    private readonly Queue<DateTimeOffset> _restartHistory = new();
    private int _consecutiveMismatchChecks;
    private DateTimeOffset? _suppressedUntilUtc;
    private bool _automaticRestartsBlocked;
    private string? _automaticRestartBlockReason;
    private DateTimeOffset _serviceStartedAtUtc = DateTimeOffset.UtcNow;
    private XBondClientWatchdogStatus _status;

    public XBondClientWatchdogService(
        XBondClientWatchdogSettings settings,
        XBondClientWatchdogSettingsStore settingsStore,
        IXBondClientWatchdogStateStore stateStore,
        XBondSnapshotCache snapshotCache,
        XBondPhysicalPathProbeService physicalPathProbeService,
        XBondTrafficEngineService trafficEngineService,
        XBondSettings xbondSettings,
        ILogger<XBondClientWatchdogService> logger)
    {
        this.settings = settings;
        this.settingsStore = settingsStore;
        this.stateStore = stateStore;
        this.snapshotCache = snapshotCache;
        this.physicalPathProbeService = physicalPathProbeService;
        this.trafficEngineService = trafficEngineService;
        this.xbondSettings = xbondSettings;
        this.logger = logger;
        _status = BuildInitialStatus(settings);
        var loadResult = stateStore.Load(DateTimeOffset.UtcNow);
        _persistentState = loadResult.State;
        foreach (var restart in _persistentState.RestartHistoryUtc)
        {
            _restartHistory.Enqueue(restart);
        }

        _suppressedUntilUtc = _persistentState.SuppressedUntilUtc;
        _automaticRestartsBlocked = !loadResult.CanRestartAutomatically;
        _automaticRestartBlockReason = loadResult.FailureReason;
        UpdateStatus(status =>
        {
            status.RestartsLastHour = _restartHistory.Count;
            status.SuppressedUntilUtc = _suppressedUntilUtc;
            status.AutomaticRestartsBlocked = _automaticRestartsBlocked;
            status.AutomaticRestartBlockReason = _automaticRestartBlockReason;
            if (_automaticRestartsBlocked)
            {
                status.Message = BuildPersistenceBlockMessage();
            }
        });
    }

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
            status.Message = _automaticRestartsBlocked
                ? BuildPersistenceBlockMessage()
                : settings.Enabled
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

    public async Task<XBondClientWatchdogStatus> ReinitializePersistentStateAsync(
        CancellationToken cancellationToken = default)
    {
        await _checkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cleanState = new XBondClientWatchdogState();
            try
            {
                await stateStore.SaveAsync(cleanState, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SetAutomaticRestartBlock($"Watchdog safety state could not be reinitialized: {ex.Message}");
                logger.LogError(ex, "Could not reinitialize XBond client watchdog safety state");
                throw new InvalidOperationException(
                    "Could not reinitialize watchdog safety state; automatic restarts remain disabled.",
                    ex);
            }

            ApplyPersistentState(cleanState, null);
            _consecutiveMismatchChecks = 0;
            UpdateStatus(status =>
            {
                status.ConsecutiveMismatchChecks = 0;
                status.Message = "Watchdog safety state reinitialized; automatic restart protection is available.";
            });
            logger.LogWarning("XBond client watchdog safety state was reinitialized by an operator");
            return GetStatus();
        }
        finally
        {
            _checkLock.Release();
        }
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
            var persistenceBlocked = _automaticRestartsBlocked;
            var shouldRestart = settings.Enabled &&
                                decision.ShouldRestart &&
                                !suppressed &&
                                !rateLimited &&
                                !persistenceBlocked;
            string message;

            if (shouldRestart)
            {
                message = await AttemptRestartAsync(decision, startedAt, cancellationToken).ConfigureAwait(false);
            }
            else if (persistenceBlocked)
            {
                message = $"{BuildPersistenceBlockMessage()} {decision.Reason}";
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
                status.AutomaticRestartsBlocked = _automaticRestartsBlocked;
                status.AutomaticRestartBlockReason = _automaticRestartBlockReason;
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
        if (_automaticRestartsBlocked)
        {
            return BuildPersistenceBlockMessage();
        }

        var previousState = CapturePersistentState();
        var reservedState = CapturePersistentState();
        reservedState.RestartHistoryUtc.Add(startedAt);
        var suppression = TimeSpan.FromSeconds(settings.PostRestartGraceSeconds) >
                          TimeSpan.FromMinutes(settings.RestartCooldownMinutes)
            ? TimeSpan.FromSeconds(settings.PostRestartGraceSeconds)
            : TimeSpan.FromMinutes(settings.RestartCooldownMinutes);
        reservedState.SuppressedUntilUtc = startedAt + suppression;
        reservedState.AutomaticRestartsBlocked = true;

        try
        {
            await stateStore.SaveAsync(reservedState, startedAt, cancellationToken).ConfigureAwait(false);
            ApplyPersistentState(
                reservedState,
                "A watchdog restart reservation is awaiting durable finalization.");
        }
        catch (Exception ex)
        {
            SetAutomaticRestartBlock($"Watchdog restart state could not be reserved: {ex.Message}");
            var blockedMessage =
                "XBond client restart skipped because its safety state could not be persisted; automatic restarts are disabled.";
            logger.LogError(ex, "{Message}", blockedMessage);
            return blockedMessage;
        }

        XBondTrafficEngineStatus serviceStatus;
        try
        {
            serviceStatus = await trafficEngineService.RestartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return await RollBackRestartReservationAsync(
                previousState,
                startedAt,
                $"XBond client restart failed: {ex.Message}",
                ex).ConfigureAwait(false);
        }

        var restartSucceeded = serviceStatus.ClientServiceRunning && string.IsNullOrWhiteSpace(serviceStatus.Error);
        if (!restartSucceeded)
        {
            return await RollBackRestartReservationAsync(
                previousState,
                startedAt,
                $"XBond client restart failed: {serviceStatus.Error ?? serviceStatus.Message}")
                .ConfigureAwait(false);
        }

        _consecutiveMismatchChecks = 0;
        snapshotCache.Invalidate();
        UpdateStatus(status =>
        {
            status.LastRestartAtUtc = startedAt;
            status.LastRestartReason = decision.Reason;
        });

        var finalizedState = CloneState(reservedState);
        finalizedState.AutomaticRestartsBlocked = false;
        var message = $"Restarted XBond client: {decision.Reason}";
        try
        {
            await stateStore.SaveAsync(finalizedState, startedAt, CancellationToken.None).ConfigureAwait(false);
            ApplyPersistentState(finalizedState, null);
        }
        catch (Exception ex)
        {
            SetAutomaticRestartBlock($"Successful restart state could not be finalized: {ex.Message}");
            message =
                $"Restarted XBond client, but its safety state could not be finalized; automatic restarts remain disabled. {decision.Reason}";
            logger.LogError(ex, "{Message}", message);
        }

        logger.LogWarning(
            "{Message}; healthyPhysicalPaths={HealthyPathCount}",
            message,
            decision.HealthyPhysicalPathCount);
        return message;
    }

    private async Task<string> RollBackRestartReservationAsync(
        XBondClientWatchdogState previousState,
        DateTimeOffset attemptedAt,
        string failureMessage,
        Exception? restartException = null)
    {
        try
        {
            await stateStore.SaveAsync(previousState, attemptedAt, CancellationToken.None).ConfigureAwait(false);
            ApplyPersistentState(
                previousState,
                previousState.AutomaticRestartsBlocked
                    ? "Automatic restarts were already blocked before this attempt."
                    : null);
        }
        catch (Exception rollbackException)
        {
            SetAutomaticRestartBlock($"Failed restart reservation could not be rolled back: {rollbackException.Message}");
            failureMessage += " Its safety-state rollback also failed, so automatic restarts remain disabled.";
            logger.LogError(rollbackException, "Could not roll back XBond client watchdog restart state");
        }

        if (restartException is not null)
        {
            logger.LogWarning(restartException, "{Message}", failureMessage);
        }
        else
        {
            logger.LogWarning("{Message}", failureMessage);
        }

        return failureMessage;
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

    private XBondClientWatchdogState CapturePersistentState() => new()
    {
        RestartHistoryUtc = _restartHistory.ToList(),
        SuppressedUntilUtc = _suppressedUntilUtc,
        AutomaticRestartsBlocked = _automaticRestartsBlocked
    };

    private void ApplyPersistentState(
        XBondClientWatchdogState state,
        string? blockReason)
    {
        _persistentState.RestartHistoryUtc = state.RestartHistoryUtc.ToList();
        _persistentState.SuppressedUntilUtc = state.SuppressedUntilUtc;
        _persistentState.AutomaticRestartsBlocked = state.AutomaticRestartsBlocked;
        _restartHistory.Clear();
        foreach (var restart in state.RestartHistoryUtc)
        {
            _restartHistory.Enqueue(restart);
        }

        _suppressedUntilUtc = state.SuppressedUntilUtc;
        _automaticRestartsBlocked = state.AutomaticRestartsBlocked;
        _automaticRestartBlockReason = state.AutomaticRestartsBlocked ? blockReason : null;
        UpdateStatus(status =>
        {
            status.RestartsLastHour = _restartHistory.Count;
            status.SuppressedUntilUtc = _suppressedUntilUtc;
            status.AutomaticRestartsBlocked = _automaticRestartsBlocked;
            status.AutomaticRestartBlockReason = _automaticRestartBlockReason;
        });
    }

    private void SetAutomaticRestartBlock(string reason)
    {
        _persistentState.AutomaticRestartsBlocked = true;
        _automaticRestartsBlocked = true;
        _automaticRestartBlockReason = reason;
        UpdateStatus(status =>
        {
            status.AutomaticRestartsBlocked = true;
            status.AutomaticRestartBlockReason = reason;
            status.Message = BuildPersistenceBlockMessage();
        });
    }

    private string BuildPersistenceBlockMessage() =>
        $"Automatic XBond client restarts are disabled because watchdog safety state is unavailable or uncertain. " +
        $"{_automaticRestartBlockReason ?? "Repair or remove the state file, then restart XNetwork."}";

    private static XBondClientWatchdogState CloneState(XBondClientWatchdogState state) => new()
    {
        RestartHistoryUtc = state.RestartHistoryUtc.ToList(),
        SuppressedUntilUtc = state.SuppressedUntilUtc,
        AutomaticRestartsBlocked = state.AutomaticRestartsBlocked
    };

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
        AutomaticRestartsBlocked = status.AutomaticRestartsBlocked,
        AutomaticRestartBlockReason = status.AutomaticRestartBlockReason,
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
