namespace XNetwork.Models;

/// <summary>
/// Aggregated health metrics for an adapter over a rolling window
/// </summary>
public class HealthMetrics
{
    /// <summary>
    /// Average latency in milliseconds
    /// </summary>
    public double AverageLatency { get; init; }

    /// <summary>
    /// Average packet loss percentage
    /// </summary>
    public double AveragePacketLoss { get; init; }

    /// <summary>
    /// Average speed in Mbps
    /// </summary>
    public double AverageSpeed { get; init; }

    /// <summary>
    /// Minimum latency observed
    /// </summary>
    public double MinLatency { get; init; }

    /// <summary>
    /// Maximum latency observed
    /// </summary>
    public double MaxLatency { get; init; }

    /// <summary>
    /// Standard deviation of latency
    /// </summary>
    public double LatencyStdDev { get; init; }

    /// <summary>
    /// Jitter (latency variation) in milliseconds
    /// Equivalent to LatencyStdDev for ping-based measurements
    /// </summary>
    public double Jitter { get; init; }

    /// <summary>
    /// Success rate as a percentage (0-100)
    /// Percentage of successful pings in the measurement window
    /// </summary>
    public double SuccessRate { get; init; }

    /// <summary>
    /// Stability score (0-1, higher is more stable)
    /// Based on coefficient of variation
    /// </summary>
    public double StabilityScore { get; init; }

    /// <summary>
    /// Number of samples used for these metrics
    /// </summary>
    public int SampleCount { get; init; }

    /// <summary>
    /// Connection status based on these metrics
    /// </summary>
    public ConnectionStatus Status { get; init; }

    /// <summary>
    /// Creates health metrics from aggregated values
    /// </summary>
    public HealthMetrics(
        double averageLatency,
        double averagePacketLoss,
        double averageSpeed,
        double minLatency,
        double maxLatency,
        double latencyStdDev,
        double stabilityScore,
        int sampleCount,
        ConnectionStatus status,
        double jitter = 0,
        double successRate = 100)
    {
        AverageLatency = averageLatency;
        AveragePacketLoss = averagePacketLoss;
        AverageSpeed = averageSpeed;
        MinLatency = minLatency;
        MaxLatency = maxLatency;
        LatencyStdDev = latencyStdDev;
        Jitter = jitter > 0 ? jitter : latencyStdDev; // Use jitter if provided, otherwise use latencyStdDev
        SuccessRate = successRate;
        StabilityScore = stabilityScore;
        SampleCount = sampleCount;
        Status = status;
    }
}