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

    [JsonPropertyName("anchor_path_id")]
    public int? AnchorPathId { get; set; }

    [JsonPropertyName("schedule")]
    public XBondSchedulePlan Schedule { get; set; } = new();

    [JsonPropertyName("paths")]
    public List<XBondPathStatus> Paths { get; set; } = new();

    [JsonPropertyName("duplicate_packets_dropped")]
    public ulong DuplicatePacketsDropped { get; set; }

    [JsonPropertyName("fec_packets_recovered")]
    public ulong FecPacketsRecovered { get; set; }

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

public class XBondPathStatus
{
    [JsonPropertyName("path_id")]
    public int PathId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("interface_name")]
    public string? InterfaceName { get; set; }

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
