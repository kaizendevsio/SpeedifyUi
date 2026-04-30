namespace XNetwork.Models;

public class ServerHealthScore
{
    public string Tag { get; set; } = "";

    public string FriendlyName { get; set; } = "";

    public string Country { get; set; } = "";

    public string City { get; set; } = "";

    public int Num { get; set; }

    public bool IsPremium { get; set; }

    public bool IsPrivate { get; set; }

    public string DataCenter { get; set; } = "";

    public DateTime TestedUtc { get; set; }

    public double LatencyMs { get; set; }

    public double JitterMs { get; set; }

    public double SuccessRate { get; set; }

    public double? ThroughputMbps { get; set; }

    public double ThroughputConfidence { get; set; }

    public int ThroughputSampleCount { get; set; }

    public int ThroughputAttemptCount { get; set; }

    public int ThroughputParallelDownloads { get; set; }

    public int ThroughputSampleBytes { get; set; }

    public double Score { get; set; }

    public bool WasSuccessful { get; set; }

    public string? FailureReason { get; set; }
}
