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

    [JsonPropertyName("recovery")]
    public XBondRecoveryStatus Recovery { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = "XBond status is unavailable";

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
    public string Message { get; set; } = "XBond tunnel is not reporting runtime status.";

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
