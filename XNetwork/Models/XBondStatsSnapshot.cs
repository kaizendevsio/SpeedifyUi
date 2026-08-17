namespace XNetwork.Models;

public sealed class XBondStatsSnapshot
{
    public XBondStatus RawStatus { get; init; } = new();

    public bool IsRunning => RawStatus.Running;

    public bool HasError => RawStatus.HasError;

    public string? Error => RawStatus.Error;

    public string State => RawStatus.Tunnel.State;

    public string Message => RawStatus.Message;

    public string ServerAddress => RawStatus.ServerAddress;

    public string TunnelDevice => RawStatus.Tunnel.DeviceName ?? "xbond0";

    public string Mode => RawStatus.Mode;

    public string RedundancyPolicy => RawStatus.RedundancyPolicy;

    public DateTime UpdatedAtUtc => RawStatus.UpdatedAtUtc;

    public int? AnchorPathId => RawStatus.AnchorPathId;

    public IReadOnlyList<XBondPathStatsSnapshot> Paths { get; init; } = [];

    public IReadOnlyList<XBondPathStatsSnapshot> DashboardPaths =>
        Paths.Where(path => path.ShowOnDashboard).ToArray();

    public IReadOnlyList<XBondPathStatsSnapshot> ActivePaths =>
        DashboardPaths.Where(path => path.IsActive && !path.IsUnavailable).ToArray();

    public IReadOnlyList<XBondPathStatsSnapshot> StandbyPaths =>
        DashboardPaths.Where(path => !path.IsActive && !path.IsUnavailable).ToArray();

    /// <summary>Configured paths quarantined because their adapter is missing or deliberately disabled.</summary>
    public IReadOnlyList<XBondPathStatsSnapshot> UnavailablePaths =>
        DashboardPaths.Where(path => path.IsUnavailable).ToArray();

    public double DownloadMbps => RawStatus.InboundThroughputBps / 1_000_000d;

    public double UploadMbps => RawStatus.OutboundThroughputBps / 1_000_000d;

    public double AggregateThroughputMbps => DownloadMbps + UploadMbps;

    public double AverageRttMs => Paths
        .Where(path => path.IsActive && path.RttMs.HasValue)
        .Select(path => path.RttMs!.Value)
        .DefaultIfEmpty(0)
        .Average();

    public double MaxLossPercent => Paths
        .Where(path => path.IsActive)
        .Select(path => path.DisplayLossPercent ?? 0)
        .DefaultIfEmpty(0)
        .Max();

    public bool HasTunnelHealth => RawStatus.Tunnel.RttMs.HasValue || RawStatus.Tunnel.LossRate.HasValue;

    public double TunnelRttMs => RawStatus.Tunnel.RttMs.GetValueOrDefault();

    public double TunnelLossPercent => RawStatus.Tunnel.LossRate.HasValue
        ? Math.Clamp(RawStatus.Tunnel.LossRate.Value, 0, 1) * 100
        : 0;

    public double EffectiveRttMs => TunnelRttMs;

    public double EffectiveLossPercent => TunnelLossPercent;

    public string HealthReason => RawStatus.Tunnel.Reason;

    public bool IsProtectingFromLoss =>
        IsRunning &&
        HasTunnelHealth &&
        ActivePaths.Count > 1 &&
        MaxLossPercent >= 2 &&
        EffectiveLossPercent < 2 &&
        EffectiveRttMs < 180;

    public string ProtectionReason => IsProtectingFromLoss
        ? $"Active paths report up to {MaxLossPercent:0.#}% loss while the uLink tunnel is holding at {EffectiveLossPercent:0.#}% loss."
        : string.Empty;

    public string ConnectionTitle
    {
        get
        {
            if (HasError)
            {
                return "Poor Connection";
            }

            if (!IsRunning)
            {
                return "Disconnected";
            }

            if (ActivePaths.Count == 0)
            {
                return "Partial Connection";
            }

            if (!HasTunnelHealth)
            {
                return "Initializing Connection";
            }

            var loss = EffectiveLossPercent;
            var rtt = EffectiveRttMs;

            if (loss >= 25 || rtt >= 300)
            {
                return "Critical Connection";
            }

            if (loss >= 10 || rtt >= 180)
            {
                return "Poor Connection";
            }

            if (loss >= 2 || rtt >= 120)
            {
                return "Fair Connection";
            }

            if (loss >= 0.5 || rtt >= 90)
            {
                return "Good Connection";
            }

            return "Excellent Connection";
        }
    }

    public bool IsStable => IsRunning && HasTunnelHealth && EffectiveLossPercent < 10 && EffectiveRttMs < 180;

    public ulong DataPacketsSent => RawStatus.DataPacketsSent;

    public ulong DuplicatePacketsSent => RawStatus.DuplicatePacketsSent;

    public ulong FecPacketsSent => RawStatus.FecPacketsSent;

    public ulong DataPacketsReceived => RawStatus.DataPacketsReceived;

    public ulong DataBytesSent => RawStatus.DataBytesSent;

    public ulong DataBytesReceived => RawStatus.DataBytesReceived;

    public XBondReorderStatus Reorder => RawStatus.Reorder;

    public XBondProcessStatus Process => RawStatus.Process;

    public double QueueUtilizationPercent => Math.Clamp(Process.Saturation.QueueUtilization, 0, 1) * 100;

    public ulong QueueOldestAgeMs => Process.Saturation.OldestAgeMs;

    public ulong UdpBufferErrors =>
        Process.KernelNetwork.UdpReceiveBufferErrors + Process.KernelNetwork.UdpSendBufferErrors;

    public ulong SaturationPeriods => Process.Saturation.SaturationPeriods;

    public XBondServerRecoveryStatus ServerRecovery => RawStatus.ServerRecovery;

    public bool HasServerRecoveryTelemetry => ServerRecovery.Reported;

    public XBondServerHealthStatus ServerHealth => RawStatus.ServerHealth;

    public string EffectiveServerHealthStatus
    {
        get
        {
            if (IsServerHealthStale)
            {
                return "unknown";
            }

            return string.IsNullOrWhiteSpace(ServerHealth.Status)
                ? "unknown"
                : ServerHealth.Status.ToLowerInvariant();
        }
    }

    public bool HasServerHealthTelemetry => ServerHealth.UpdatedAtMicros > 0 && EffectiveServerHealthStatus != "unknown";

    public bool IsServerHealthStale
    {
        get
        {
            if (ServerHealth.UpdatedAtMicros == 0)
            {
                return false;
            }

            var nowMicros = (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000);
            return ServerHealth.UpdatedAtMicros < nowMicros &&
                   nowMicros - ServerHealth.UpdatedAtMicros > 30_000_000;
        }
    }

    public string ServerHealthLabel => EffectiveServerHealthStatus switch
    {
        "healthy" => "Server OK",
        "degraded" => "Server degraded",
        "down" => "Server down",
        _ => "Server unknown"
    };

    public double ServerEgressConnectMs => ServerHealth.AvgConnectMs.GetValueOrDefault();

    public double ServerHealthFailurePercent =>
        Math.Round((1 - Math.Clamp(ServerHealth.SuccessRate, 0, 1)) * 100, 1);

    public string ServerHealthDetails
    {
        get
        {
            var details = ServerHealth.Targets.Count == 0
                ? "No probe targets reported."
                : string.Join("; ", ServerHealth.Targets.Select(target =>
                {
                    if (target.Success)
                    {
                        return $"{target.Target} OK {target.ConnectMs.GetValueOrDefault():0.#} ms";
                    }

                    return $"{target.Target} failed{(string.IsNullOrWhiteSpace(target.Error) ? string.Empty : $": {target.Error}")}";
                }));

            var lastSuccess = ServerHealth.LastSuccessAgeMs.HasValue
                ? $" Last success {FormatAge(ServerHealth.LastSuccessAgeMs.Value)} ago."
                : " No successful probe yet.";

            return $"{ServerHealth.Reason} {details}.{lastSuccess}";
        }
    }

    public bool IsRecoveryActive => RawStatus.Recovery.Active || ServerRecovery.RecoveryActive;

    public int RecoveryHoldMs => ServerRecovery.IngressReorder.CurrentHoldMs;

    public int RecoveryNormalHoldMs => ServerRecovery.IngressReorder.NormalHoldMs;

    public int RecoveryMinHoldMs => ServerRecovery.IngressReorder.RecoveryMinHoldMs;

    public int RecoveryMaxHoldMs => ServerRecovery.IngressReorder.RecoveryMaxHoldMs;

    public string RecoveryReason => ServerRecovery.RecoveryActive
        ? ServerRecovery.IngressReorder.AdaptiveLastAdjustmentReason
        : RawStatus.Recovery.Reason;

    public string RecoveryBadgeText => HasServerRecoveryTelemetry && RecoveryHoldMs > 0
        ? $"Recovery {RecoveryHoldMs} ms"
        : "Recovery";

    public bool Ipv6Supported => false;

    private static string FormatAge(ulong ageMs)
    {
        if (ageMs < 1_000)
        {
            return $"{ageMs} ms";
        }

        if (ageMs < 60_000)
        {
            return $"{ageMs / 1_000d:0.#} s";
        }

        return $"{ageMs / 60_000d:0.#} min";
    }
}

/// <summary>Why a configured uLink path is unusable, beyond ordinary down/NO-CARRIER states.</summary>
public enum XBondPathAvailability
{
    /// <summary>Usable, or down for an ordinary reason such as lost carrier.</summary>
    Normal,

    /// <summary>The bound interface is not present on the host at all.</summary>
    InterfaceMissing,

    /// <summary>The adapter is deliberately disabled under Settings > Router Wi-Fi.</summary>
    WifiDisabled
}

public sealed class XBondPathStatsSnapshot
{
    public int PathId { get; init; }

    public string Name { get; init; } = "";

    public string InterfaceName { get; init; } = "";

    public string Role { get; init; } = "probe";

    public bool InterfaceUp { get; init; }

    public bool InCooldown { get; init; }

    public string? DemotionReason { get; init; }

    public string? RoleReason { get; init; }

    public int SendFailureStreak { get; init; }

    public ulong SocketGeneration { get; init; }

    public uint? SocketIfindex { get; init; }

    public string? SocketBindAddress { get; init; }

    public string? LastSocketError { get; init; }

    public string? LastRebindReason { get; init; }

    public string? LastRebindError { get; init; }

    public ulong? LastRebindAtMicros { get; init; }

    public ulong RebindCount { get; init; }

    public int PendingProbes { get; init; }

    public int HeartbeatSampleCount { get; init; }

    public int HeartbeatConsecutiveMisses { get; init; }

    public int HeartbeatConsecutiveSuccesses { get; init; }

    public bool HeartbeatWarmingUp { get; init; }

    public bool HeartbeatFailed { get; init; }

    public ulong? StaleAckMs { get; init; }

    public double QueuePressure { get; init; }

    public double DuplicateUsefulness { get; init; } = 1.0;

    public double ThroughputCollapseScore { get; init; }

    public double Score { get; init; }

    public double SmoothedScore { get; init; }

    /// <summary>Smoothed score minus an instability penalty; what the client ranks paths on.</summary>
    public double EffectiveScore { get; init; }

    /// <summary>Decaying distrust after a failed stint or trial as anchor.</summary>
    public double FlapPenalty { get; init; }

    /// <summary>Threshold the client uses to suppress anchor promotion.</summary>
    public const double SuppressionThreshold = 500;

    public bool IsSuppressed => FlapPenalty >= SuppressionThreshold;

    public XBondPathTrialStatus? Trial { get; init; }

    public bool IsTrial => string.Equals(Role, "trial", StringComparison.OrdinalIgnoreCase);

    public double? RttMs { get; init; }

    public double? JitterMs { get; init; }

    public double LossPercent { get; init; }

    public double? DisplayLossPercent => HeartbeatWarmingUp && !HeartbeatFailed ? null : LossPercent;

    public double LatePercent { get; init; }

    public int QueueDepth { get; init; }

    public ulong ThroughputBps { get; init; }

    public ulong OutboundThroughputBps { get; init; }

    public ulong InboundThroughputBps { get; init; }

    public ulong DuplicateInboundThroughputBps { get; init; }

    public ulong RawInboundThroughputBps { get; init; }

    public ulong TotalThroughputBps => ThroughputBps > 0
        ? ThroughputBps
        : OutboundThroughputBps + RawPathInboundThroughputBps;

    public double ThroughputMbps => TotalThroughputBps / 1_000_000d;

    public ulong RawPathInboundThroughputBps => RawInboundThroughputBps > 0
        ? RawInboundThroughputBps
        : InboundThroughputBps + DuplicateInboundThroughputBps;

    public double DownloadMbps => RawPathInboundThroughputBps / 1_000_000d;

    public double UsefulDownloadMbps => InboundThroughputBps / 1_000_000d;

    public double DuplicateDownloadMbps => DuplicateInboundThroughputBps / 1_000_000d;

    public double UploadMbps => OutboundThroughputBps / 1_000_000d;

    public string BindAddress { get; init; } = "";

    public string BindDevice { get; init; } = "";

    public string? Gateway { get; init; }

    public string? CellularGeneration { get; init; }

    public int? CellularSignalBars { get; init; }

    public bool HasCellularTelemetry => !string.IsNullOrWhiteSpace(CellularGeneration) || CellularSignalBars.HasValue;

    public bool IsActive { get; init; }

    public bool IsConfigured { get; init; } = true;

    public XBondPathAvailability Availability { get; init; } = XBondPathAvailability.Normal;

    public bool IsUnavailable => Availability != XBondPathAvailability.Normal;

    /// <summary>Plain-language reason a quarantined path is not carrying traffic.</summary>
    public string UnavailableReason => Availability switch
    {
        XBondPathAvailability.InterfaceMissing => "Adapter not present",
        XBondPathAvailability.WifiDisabled => "Wi-Fi disabled in Settings",
        _ => ""
    };

    // Unavailable paths are shown too: an absent adapter behind a configured path was previously
    // invisible, which gave the operator no signal at all.
    public bool ShowOnDashboard => InterfaceUp || IsActive || InCooldown || !IsConfigured || IsUnavailable;

    public bool IsAnchor => string.Equals(Role, "anchor", StringComparison.OrdinalIgnoreCase);

    public string StateText
    {
        get
        {
            if (!IsConfigured && InterfaceUp)
            {
                return "Connected, not in uLink";
            }

            if (!InterfaceUp)
            {
                return "No carrier";
            }

            if (InCooldown)
            {
                return string.IsNullOrWhiteSpace(DemotionReason) ? "Cooldown" : DemotionReason;
            }

            if (HeartbeatWarmingUp && !HeartbeatFailed)
            {
                return HeartbeatSampleCount > 0
                    ? $"Warming up heartbeat quality ({HeartbeatSampleCount} samples)"
                    : "Warming up heartbeat quality";
            }

            if (IsTrial)
            {
                return Trial is null
                    ? "Testing as anchor"
                    : $"Testing as anchor ({Trial.Ticks}/{Trial.RequiredTicks})";
            }

            return IsActive ? "Active" : "Standby";
        }
    }

    public string DecisionText => !string.IsNullOrWhiteSpace(DemotionReason)
        ? DemotionReason
        : !string.IsNullOrWhiteSpace(RoleReason)
            ? RoleReason
            : StateText;
}
