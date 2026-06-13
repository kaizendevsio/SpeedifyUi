using System.Text.Json.Serialization;

namespace XNetwork.Models;

public class XBondPublicTestResult
{
    [JsonIgnore]
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public DateTime? CompletedAtUtc { get; set; }

    [JsonPropertyName("server")]
    public string Server { get; set; } = "";

    [JsonPropertyName("bind")]
    public string Bind { get; set; } = "";

    [JsonPropertyName("path_id")]
    public int PathId { get; set; }

    [JsonPropertyName("session_id")]
    public ulong SessionId { get; set; }

    [JsonPropertyName("sent")]
    public int Sent { get; set; }

    [JsonPropertyName("received")]
    public int Received { get; set; }

    [JsonPropertyName("lost")]
    public int Lost { get; set; }

    [JsonPropertyName("loss_rate")]
    public double LossRate { get; set; }

    [JsonPropertyName("min_rtt_ms")]
    public double? MinRttMs { get; set; }

    [JsonPropertyName("avg_rtt_ms")]
    public double? AvgRttMs { get; set; }

    [JsonPropertyName("max_rtt_ms")]
    public double? MaxRttMs { get; set; }

    [JsonPropertyName("replies")]
    public List<XBondPingReply> Replies { get; set; } = new();

    [JsonIgnore]
    public string BypassRule { get; set; } = "";

    [JsonIgnore]
    public bool BypassWasAlreadyPresent { get; set; }

    [JsonIgnore]
    public bool BypassAdded { get; set; }

    [JsonIgnore]
    public bool BypassRemoved { get; set; }

    [JsonIgnore]
    public bool BypassEnabledChanged { get; set; }

    [JsonIgnore]
    public bool BypassEnabledRestored { get; set; }

    [JsonIgnore]
    public string Message { get; set; } = "";

    [JsonIgnore]
    public string? Error { get; set; }

    [JsonIgnore]
    public bool Succeeded => string.IsNullOrWhiteSpace(Error) && Sent > 0 && Received == Sent;

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public class XBondPingReply
{
    [JsonPropertyName("sequence")]
    public ulong Sequence { get; set; }

    [JsonPropertyName("rtt_ms")]
    public double RttMs { get; set; }
}
