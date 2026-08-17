using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class XBondStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "anchor-duplicate-1";

    [JsonPropertyName("redundancy_policy")]
    public string RedundancyPolicy { get; set; } = "balanced";

    [JsonPropertyName("server_addr")]
    public string ServerAddress { get; set; } = "";

    [JsonPropertyName("tunnel")]
    public XBondTunnelStatus Tunnel { get; set; } = new();

    [JsonPropertyName("anchor_path_id")]
    public int? AnchorPathId { get; set; }

    [JsonPropertyName("schedule")]
    public XBondSchedulePlan Schedule { get; set; } = new();

    [JsonPropertyName("paths")]
    public List<XBondPathStatus> Paths { get; set; } = new();

    [JsonPropertyName("data_packets_sent")]
    public ulong DataPacketsSent { get; set; }

    [JsonPropertyName("duplicate_packets_sent")]
    public ulong DuplicatePacketsSent { get; set; }

    [JsonPropertyName("duplicate_packets_dropped")]
    public ulong DuplicatePacketsDropped { get; set; }

    [JsonPropertyName("data_packets_received")]
    public ulong DataPacketsReceived { get; set; }

    [JsonPropertyName("data_bytes_sent")]
    public ulong DataBytesSent { get; set; }

    [JsonPropertyName("data_bytes_received")]
    public ulong DataBytesReceived { get; set; }

    [JsonPropertyName("outbound_throughput_bps")]
    public ulong OutboundThroughputBps { get; set; }

    [JsonPropertyName("inbound_throughput_bps")]
    public ulong InboundThroughputBps { get; set; }

    [JsonPropertyName("fec_packets_sent")]
    public ulong FecPacketsSent { get; set; }

    [JsonPropertyName("fec_packets_recovered")]
    public ulong FecPacketsRecovered { get; set; }

    [JsonPropertyName("fec_packets_skipped")]
    public ulong FecPacketsSkipped { get; set; }

    [JsonPropertyName("fec")]
    public XBondFecStatus Fec { get; set; } = new();

    [JsonPropertyName("late_packets_dropped")]
    public ulong LatePacketsDropped { get; set; }

    [JsonPropertyName("reorder")]
    public XBondReorderStatus Reorder { get; set; } = new();

    [JsonPropertyName("process")]
    public XBondProcessStatus Process { get; set; } = new();

    [JsonPropertyName("server_recovery")]
    public XBondServerRecoveryStatus ServerRecovery { get; set; } = new();

    [JsonPropertyName("server_health")]
    public XBondServerHealthStatus ServerHealth { get; set; } = new();

    [JsonPropertyName("recovery")]
    public XBondRecoveryStatus Recovery { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = "uLink status is unavailable";

    [JsonIgnore]
    public string? Error { get; set; }

    [JsonIgnore]
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public class XBondSchedulePlan
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "anchor-duplicate-1";

    [JsonPropertyName("anchor_path_id")]
    public int? AnchorPathId { get; set; }

    [JsonPropertyName("data_path_ids")]
    public List<int> DataPathIds { get; set; } = new();

    [JsonPropertyName("duplicate_path_ids")]
    public List<int> DuplicatePathIds { get; set; } = new();

    [JsonPropertyName("fec_path_ids")]
    public List<int> FecPathIds { get; set; } = new();

    /// <summary>Anchor candidate carrying mirrored traffic during a load trial.</summary>
    [JsonPropertyName("trial_path_ids")]
    public List<int> TrialPathIds { get; set; } = new();
}

/// <summary>Progress of the load test a candidate must pass before taking the anchor role.</summary>
public class XBondPathTrialStatus
{
    [JsonPropertyName("path_id")]
    public int PathId { get; set; }

    [JsonPropertyName("ticks")]
    public int Ticks { get; set; }

    [JsonPropertyName("success_ticks")]
    public int SuccessTicks { get; set; }

    [JsonPropertyName("mirrored_bytes")]
    public ulong MirroredBytes { get; set; }

    [JsonPropertyName("required_ticks")]
    public int RequiredTicks { get; set; }

    [JsonPropertyName("required_success_ticks")]
    public int RequiredSuccessTicks { get; set; }
}

public class XBondTunnelStatus
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "disabled";

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("mtu")]
    public int? Mtu { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "uLink tunnel is not reporting runtime status.";

    [JsonPropertyName("rtt_ms")]
    public double? RttMs { get; set; }

    [JsonPropertyName("jitter_ms")]
    public double? JitterMs { get; set; }

    [JsonPropertyName("loss_rate")]
    public double? LossRate { get; set; }

    [JsonPropertyName("success_rate")]
    public double? SuccessRate { get; set; }

    [JsonPropertyName("pending_probes")]
    public int PendingProbes { get; set; }

    [JsonPropertyName("last_success_age_ms")]
    public ulong? LastSuccessAgeMs { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "Tunnel health is unavailable.";
}

public class XBondFecStatus
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("production_ready")]
    public bool ProductionReady { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "FEC is not configured for the current schedule.";
}

public class XBondPathStatus
{
    [JsonPropertyName("path_id")]
    public int PathId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("interface_name")]
    public string? InterfaceName { get; set; }

    [JsonPropertyName("bind_addr")]
    public string? BindAddress { get; set; }

    [JsonPropertyName("bind_device")]
    public string? BindDevice { get; set; }

    [JsonPropertyName("path_isolation")]
    public XBondPathIsolationStatus PathIsolation { get; set; } = new();

    [JsonPropertyName("role")]
    public string Role { get; set; } = "probe";

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("smoothed_score")]
    public double SmoothedScore { get; set; }

    [JsonPropertyName("effective_score")]
    public double EffectiveScore { get; set; }

    [JsonPropertyName("flap_penalty")]
    public double FlapPenalty { get; set; }

    [JsonPropertyName("trial")]
    public XBondPathTrialStatus? Trial { get; set; }

    [JsonPropertyName("rtt_ms")]
    public double? RttMs { get; set; }

    [JsonPropertyName("jitter_ms")]
    public double? JitterMs { get; set; }

    [JsonPropertyName("loss_rate")]
    public double LossRate { get; set; }

    [JsonPropertyName("late_rate")]
    public double LateRate { get; set; }

    [JsonPropertyName("queue_depth")]
    public int QueueDepth { get; set; }

    [JsonPropertyName("throughput_bps")]
    public ulong ThroughputBps { get; set; }

    [JsonPropertyName("outbound_throughput_bps")]
    public ulong OutboundThroughputBps { get; set; }

    [JsonPropertyName("inbound_throughput_bps")]
    public ulong InboundThroughputBps { get; set; }

    [JsonPropertyName("duplicate_inbound_throughput_bps")]
    public ulong DuplicateInboundThroughputBps { get; set; }

    [JsonPropertyName("raw_inbound_throughput_bps")]
    public ulong RawInboundThroughputBps { get; set; }

    [JsonPropertyName("interface_up")]
    public bool InterfaceUp { get; set; }

    [JsonPropertyName("in_cooldown")]
    public bool InCooldown { get; set; }

    [JsonPropertyName("send_failure_streak")]
    public int SendFailureStreak { get; set; }

    [JsonPropertyName("stale_ack_ms")]
    public ulong? StaleAckMs { get; set; }

    [JsonPropertyName("queue_pressure")]
    public double QueuePressure { get; set; }

    [JsonPropertyName("duplicate_usefulness")]
    public double DuplicateUsefulness { get; set; } = 1.0;

    [JsonPropertyName("throughput_collapse_score")]
    public double ThroughputCollapseScore { get; set; }

    [JsonPropertyName("demotion_reason")]
    public string? DemotionReason { get; set; }

    [JsonPropertyName("role_reason")]
    public string? RoleReason { get; set; }

    [JsonPropertyName("socket_generation")]
    public ulong SocketGeneration { get; set; }

    [JsonPropertyName("socket_ifindex")]
    public uint? SocketIfindex { get; set; }

    [JsonPropertyName("socket_bind_addr")]
    public string? SocketBindAddress { get; set; }

    [JsonPropertyName("last_socket_error")]
    public string? LastSocketError { get; set; }

    [JsonPropertyName("last_rebind_reason")]
    public string? LastRebindReason { get; set; }

    [JsonPropertyName("last_rebind_error")]
    public string? LastRebindError { get; set; }

    [JsonPropertyName("last_rebind_at_micros")]
    public ulong? LastRebindAtMicros { get; set; }

    [JsonPropertyName("rebind_count")]
    public ulong RebindCount { get; set; }

    [JsonPropertyName("pending_probes")]
    public int PendingProbes { get; set; }

    [JsonPropertyName("heartbeat_sample_count")]
    public int HeartbeatSampleCount { get; set; }

    [JsonPropertyName("heartbeat_consecutive_misses")]
    public int HeartbeatConsecutiveMisses { get; set; }

    [JsonPropertyName("heartbeat_consecutive_successes")]
    public int HeartbeatConsecutiveSuccesses { get; set; }

    [JsonPropertyName("heartbeat_warming_up")]
    public bool HeartbeatWarmingUp { get; set; }

    [JsonPropertyName("heartbeat_failed")]
    public bool HeartbeatFailed { get; set; }
}

public class XBondPathIsolationStatus
{
    [JsonPropertyName("requested")]
    public bool Requested { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "none";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "No bind-device isolation requested.";
}

public class XBondReorderStatus
{
    [JsonPropertyName("return_path")]
    public XBondReorderCounters ReturnPath { get; set; } = new();
}

public class XBondReorderCounters
{
    [JsonPropertyName("pending_depth")]
    public ulong PendingDepth { get; set; }

    [JsonPropertyName("held_packets")]
    public ulong HeldPackets { get; set; }

    [JsonPropertyName("released_gap_packets")]
    public ulong ReleasedGapPackets { get; set; }

    [JsonPropertyName("late_duplicates")]
    public ulong LateDuplicates { get; set; }

    [JsonPropertyName("timeout_releases")]
    public ulong TimeoutReleases { get; set; }

    [JsonPropertyName("capacity_releases")]
    public ulong CapacityReleases { get; set; }
}

public class XBondProcessStatus
{
    [JsonPropertyName("process_cpu_percent")]
    public double? ProcessCpuPercent { get; set; }

    [JsonPropertyName("rss_bytes")]
    public ulong? RssBytes { get; set; }

    [JsonPropertyName("encode_micros_total")]
    public ulong EncodeMicrosTotal { get; set; }

    [JsonPropertyName("decode_micros_total")]
    public ulong DecodeMicrosTotal { get; set; }

    [JsonPropertyName("encoded_frames")]
    public ulong EncodedFrames { get; set; }

    [JsonPropertyName("decoded_frames")]
    public ulong DecodedFrames { get; set; }

    [JsonPropertyName("udp_receive_batch_size")]
    public int UdpReceiveBatchSize { get; set; }

    [JsonPropertyName("socket_buffers")]
    public List<XBondSocketBufferStatus> SocketBuffers { get; set; } = new();

    [JsonPropertyName("kernel_network")]
    public XBondKernelNetworkStatus KernelNetwork { get; set; } = new();

    [JsonPropertyName("saturation")]
    public XBondSaturationStatus Saturation { get; set; } = new();

    [JsonPropertyName("stage_timings")]
    public XBondStageTimingStatus StageTimings { get; set; } = new();
}

public class XBondSocketBufferStatus
{
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "";

    [JsonPropertyName("requested_receive_bytes")]
    public ulong RequestedReceiveBytes { get; set; }

    [JsonPropertyName("requested_send_bytes")]
    public ulong RequestedSendBytes { get; set; }

    [JsonPropertyName("effective_receive_bytes")]
    public ulong EffectiveReceiveBytes { get; set; }

    [JsonPropertyName("effective_send_bytes")]
    public ulong EffectiveSendBytes { get; set; }

    [JsonPropertyName("below_requested")]
    public bool BelowRequested { get; set; }
}

public class XBondKernelNetworkStatus
{
    [JsonPropertyName("udp_in_errors")]
    public ulong UdpInErrors { get; set; }

    [JsonPropertyName("udp_rcvbuf_errors")]
    public ulong UdpReceiveBufferErrors { get; set; }

    [JsonPropertyName("udp_sndbuf_errors")]
    public ulong UdpSendBufferErrors { get; set; }

    [JsonPropertyName("udp_no_ports")]
    public ulong UdpNoPorts { get; set; }

    [JsonPropertyName("tunnel_rx_drops")]
    public ulong TunnelReceiveDrops { get; set; }

    [JsonPropertyName("tunnel_tx_drops")]
    public ulong TunnelSendDrops { get; set; }
}

public class XBondSaturationStatus
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "normal";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("queue_utilization")]
    public double QueueUtilization { get; set; }

    [JsonPropertyName("oldest_age_ms")]
    public ulong OldestAgeMs { get; set; }

    [JsonPropertyName("recent_drain_packets_per_second")]
    public double RecentDrainPacketsPerSecond { get; set; }

    [JsonPropertyName("pacing_delay_micros")]
    public ulong PacingDelayMicros { get; set; }

    [JsonPropertyName("duplicate_suppressions")]
    public ulong DuplicateSuppressions { get; set; }

    [JsonPropertyName("fec_suppressions")]
    public ulong FecSuppressions { get; set; }

    [JsonPropertyName("saturation_periods")]
    public ulong SaturationPeriods { get; set; }

    [JsonPropertyName("hard_duration_ms")]
    public ulong HardDurationMs { get; set; }
}

public class XBondStageTimingStatus
{
    [JsonPropertyName("receive_micros_total")]
    public ulong ReceiveMicrosTotal { get; set; }

    [JsonPropertyName("receive_batches")]
    public ulong ReceiveBatches { get; set; }

    [JsonPropertyName("receive_datagrams")]
    public ulong ReceiveDatagrams { get; set; }

    [JsonPropertyName("receive_batch_peak")]
    public ulong ReceiveBatchPeak { get; set; }

    [JsonPropertyName("decode_micros_total")]
    public ulong DecodeMicrosTotal { get; set; }

    [JsonPropertyName("schedule_micros_total")]
    public ulong ScheduleMicrosTotal { get; set; }

    [JsonPropertyName("enqueue_micros_total")]
    public ulong EnqueueMicrosTotal { get; set; }

    [JsonPropertyName("tun_micros_total")]
    public ulong TunMicrosTotal { get; set; }
}

public class XBondServerHealthStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "Server egress health has not collected a sample yet.";

    [JsonPropertyName("success_rate")]
    public double SuccessRate { get; set; }

    [JsonPropertyName("avg_connect_ms")]
    public double? AvgConnectMs { get; set; }

    [JsonPropertyName("max_connect_ms")]
    public double? MaxConnectMs { get; set; }

    [JsonPropertyName("last_success_age_ms")]
    public ulong? LastSuccessAgeMs { get; set; }

    [JsonPropertyName("consecutive_failures")]
    public int ConsecutiveFailures { get; set; }

    [JsonPropertyName("updated_at_micros")]
    public ulong UpdatedAtMicros { get; set; }

    [JsonPropertyName("targets")]
    public List<XBondServerHealthTargetStatus> Targets { get; set; } = new();
}

public class XBondServerHealthTargetStatus
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("connect_ms")]
    public double? ConnectMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("updated_at_micros")]
    public ulong UpdatedAtMicros { get; set; }
}

public class XBondServerRecoveryStatus
{
    [JsonPropertyName("reported")]
    public bool Reported { get; set; }

    [JsonPropertyName("recovery_active")]
    public bool RecoveryActive { get; set; }

    [JsonPropertyName("ingress_reorder")]
    public XBondServerIngressReorderStatus IngressReorder { get; set; } = new();

    [JsonPropertyName("repair")]
    public XBondRepairStatus Repair { get; set; } = new();

    [JsonPropertyName("server_health")]
    public XBondServerHealthStatus ServerHealth { get; set; } = new();

    [JsonPropertyName("updated_at_micros")]
    public ulong UpdatedAtMicros { get; set; }
}

public class XBondServerIngressReorderStatus
{
    [JsonPropertyName("current_hold_ms")]
    public int CurrentHoldMs { get; set; }

    [JsonPropertyName("normal_hold_ms")]
    public int NormalHoldMs { get; set; }

    [JsonPropertyName("recovery_min_hold_ms")]
    public int RecoveryMinHoldMs { get; set; }

    [JsonPropertyName("recovery_max_hold_ms")]
    public int RecoveryMaxHoldMs { get; set; }

    [JsonPropertyName("adaptive_recovery_hold_enabled")]
    public bool AdaptiveRecoveryHoldEnabled { get; set; }

    [JsonPropertyName("adaptive_calm_samples")]
    public int AdaptiveCalmSamples { get; set; }

    [JsonPropertyName("adaptive_last_adjustment_reason")]
    public string AdaptiveLastAdjustmentReason { get; set; } = "";

    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("stats")]
    public XBondReorderCounters Stats { get; set; } = new();
}

public class XBondRepairStatus
{
    [JsonPropertyName("requests_sent")]
    public ulong RequestsSent { get; set; }

    [JsonPropertyName("requests_received")]
    public ulong RequestsReceived { get; set; }

    [JsonPropertyName("frames_sent")]
    public ulong FramesSent { get; set; }

    [JsonPropertyName("frames_delivered")]
    public ulong FramesDelivered { get; set; }

    [JsonPropertyName("cache_misses")]
    public ulong CacheMisses { get; set; }

    [JsonPropertyName("late_frames")]
    public ulong LateFrames { get; set; }

    [JsonPropertyName("queue_drops")]
    public ulong QueueDrops { get; set; }

    [JsonPropertyName("cache_entries")]
    public int CacheEntries { get; set; }
}

public class XBondRecoveryStatus
{
    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "Recovery redundancy is inactive.";

    [JsonPropertyName("eligible_path_ids")]
    public List<int> EligiblePathIds { get; set; } = new();

    [JsonPropertyName("degraded_ticks")]
    public int DegradedTicks { get; set; }

    [JsonPropertyName("clean_ticks")]
    public int CleanTicks { get; set; }
}
