using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class XBondStatus
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "anchor-fec";

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
    public string Mode { get; set; } = "anchor-fec";

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
    public string Message { get; set; } = "Canary tunnel is disabled by default.";
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

    [JsonPropertyName("interface_up")]
    public bool InterfaceUp { get; set; }

    [JsonPropertyName("in_cooldown")]
    public bool InCooldown { get; set; }
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
