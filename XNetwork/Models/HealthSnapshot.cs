namespace XNetwork.Models;

/// <summary>
/// Point-in-time measurement record for connection health metrics
/// </summary>
public class HealthSnapshot
{
    /// <summary>
    /// Timestamp when the snapshot was taken
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Latency in milliseconds
    /// </summary>
    public double Latency { get; init; }

    /// <summary>
    /// Packet loss percentage
    /// </summary>
    public double PacketLoss { get; init; }

    /// <summary>
    /// Connection speed in Mbps
    /// </summary>
    public double Speed { get; init; }

    /// <summary>
    /// Creates a new health snapshot with current timestamp
    /// </summary>
    public HealthSnapshot(double latency, double packetLoss, double speed)
    {
        Timestamp = DateTime.UtcNow;
        Latency = latency;
        PacketLoss = packetLoss;
        Speed = speed;
    }

    /// <summary>
    /// Creates a new health snapshot with specified timestamp
    /// </summary>
    public HealthSnapshot(DateTime timestamp, double latency, double packetLoss, double speed)
    {
        Timestamp = timestamp;
        Latency = latency;
        PacketLoss = packetLoss;
        Speed = speed;
    }
}