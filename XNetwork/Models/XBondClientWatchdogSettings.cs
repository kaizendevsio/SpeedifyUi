namespace XNetwork.Models;

public sealed class XBondClientWatchdogSettings
{
    public bool Enabled { get; set; } = true;

    public int CheckIntervalSeconds { get; set; } = 10;

    public int ConsecutiveUnhealthyChecks { get; set; } = 3;

    public int TunnelRttThresholdMs { get; set; } = 300;

    public int TunnelLossThresholdPercent { get; set; } = 25;

    public int TunnelStaleAfterSeconds { get; set; } = 10;

    public string PhysicalProbeTarget { get; set; } = "";

    public int PhysicalMaxRttMs { get; set; } = 200;

    public int PhysicalMaxLossPercent { get; set; } = 25;

    public int MinimumHealthyPhysicalPaths { get; set; } = 1;

    public int ProbeCount { get; set; } = 3;

    public int ProbeTimeoutSeconds { get; set; } = 2;

    public int RestartCooldownMinutes { get; set; } = 10;

    public int PostRestartGraceSeconds { get; set; } = 45;

    public int MaxRestartsPerHour { get; set; } = 2;
}

public sealed class XBondClientWatchdogStatus
{
    public bool Enabled { get; set; }

    public bool IsRunning { get; set; }

    public DateTimeOffset? LastStartedAtUtc { get; set; }

    public DateTimeOffset? LastCompletedAtUtc { get; set; }

    public DateTimeOffset? NextRunAtUtc { get; set; }

    public DateTimeOffset? LastRestartAtUtc { get; set; }

    public DateTimeOffset? SuppressedUntilUtc { get; set; }

    public int ConsecutiveMismatchChecks { get; set; }

    public int RestartsLastHour { get; set; }

    public bool AutomaticRestartsBlocked { get; set; }

    public string? AutomaticRestartBlockReason { get; set; }

    public bool TunnelUnhealthy { get; set; }

    public double? TunnelRttMs { get; set; }

    public double? TunnelLossPercent { get; set; }

    public int HealthyPhysicalPathCount { get; set; }

    public string ProbeTarget { get; set; } = "";

    public string Message { get; set; } = "XBond client watchdog has not run yet.";

    public string? LastRestartReason { get; set; }

    public List<XBondPhysicalPathProbeResult> PathProbes { get; set; } = new();
}

public sealed class XBondClientWatchdogState
{
    public List<DateTimeOffset> RestartHistoryUtc { get; set; } = new();

    public DateTimeOffset? SuppressedUntilUtc { get; set; }

    public bool AutomaticRestartsBlocked { get; set; }
}

public sealed record XBondClientWatchdogStateLoadResult(
    XBondClientWatchdogState State,
    bool CanRestartAutomatically,
    string? FailureReason);

public sealed class XBondPhysicalPathProbeResult
{
    public string InterfaceName { get; set; } = "";

    public string Target { get; set; } = "";

    public bool Responded { get; set; }

    public bool Healthy { get; set; }

    public double? AverageRttMs { get; set; }

    public double LossPercent { get; set; } = 100;

    public string? Error { get; set; }
}

public sealed record XBondClientWatchdogDecision(
    bool TunnelUnhealthy,
    int HealthyPhysicalPathCount,
    int ConsecutiveMismatchChecks,
    bool ShouldRestart,
    string Reason);
