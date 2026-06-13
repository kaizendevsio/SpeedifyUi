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

public class XBondProbeResult
{
    [JsonIgnore]
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public DateTime? CompletedAtUtc { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("anchor_path_id")]
    public int? AnchorPathId { get; set; }

    [JsonPropertyName("duplicate_path_ids")]
    public List<int> DuplicatePathIds { get; set; } = new();

    [JsonPropertyName("started_at")]
    public ulong StartedAtMicros { get; set; }

    [JsonPropertyName("completed_at")]
    public ulong CompletedAtMicros { get; set; }

    [JsonPropertyName("paths")]
    public List<XBondProbePathResult> Paths { get; set; } = new();

    [JsonIgnore]
    public string Server { get; set; } = "";

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
    public int TotalSent => Paths.Sum(path => path.Sent);

    [JsonIgnore]
    public int TotalAcks => Paths.Sum(path => path.Acks);

    [JsonIgnore]
    public int TotalFirstArrivals => Paths.Sum(path => path.FirstArrivals);

    [JsonIgnore]
    public int ExpectedSequences => Paths.Count == 0 ? 0 : Paths.Max(path => path.Sent);

    [JsonIgnore]
    public bool HasRouteWarnings => Paths.Any(path => !path.RouteVerified);

    [JsonIgnore]
    public bool HasPathLoss => Paths.Any(path => path.Sent == 0 || path.Acks < path.Sent);

    [JsonIgnore]
    public bool HasAnyPathResponse => Paths.Any(path => path.Acks > 0);

    [JsonIgnore]
    public bool Succeeded =>
        string.IsNullOrWhiteSpace(Error) &&
        ExpectedSequences > 0 &&
        TotalFirstArrivals >= ExpectedSequences &&
        Paths.All(path => path.Sent > 0 && path.Acks == path.Sent);

    [JsonIgnore]
    public bool FullyVerified => Succeeded && !HasRouteWarnings;

    [JsonIgnore]
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public class XBondProbePathResult
{
    [JsonPropertyName("path_id")]
    public int PathId { get; set; }

    [JsonPropertyName("adapter")]
    public string? Adapter { get; set; }

    [JsonPropertyName("interface_name")]
    public string? InterfaceName { get; set; }

    [JsonPropertyName("bind")]
    public string Bind { get; set; } = "";

    [JsonPropertyName("source")]
    public string? Source { get; set; }

    [JsonPropertyName("sent")]
    public int Sent { get; set; }

    [JsonPropertyName("received")]
    public int Received { get; set; }

    [JsonPropertyName("acks")]
    public int Acks { get; set; }

    [JsonPropertyName("first_arrivals")]
    public int FirstArrivals { get; set; }

    [JsonPropertyName("duplicates_dropped")]
    public int? DuplicatesDropped { get; set; }

    [JsonPropertyName("loss_rate")]
    public double LossRate { get; set; }

    [JsonPropertyName("avg_rtt_ms")]
    public double? AvgRttMs { get; set; }

    [JsonPropertyName("route_verified")]
    public bool RouteVerified { get; set; }

    [JsonPropertyName("route_verification")]
    public XBondRouteVerification RouteVerification { get; set; } = new();
}

public class XBondRouteVerification
{
    [JsonPropertyName("verified")]
    public bool Verified { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = "";

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";
}
