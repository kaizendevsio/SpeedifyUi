namespace XNetwork.Models;

/// <summary>
/// Point-in-time ping measurement record
/// </summary>
public class PingSnapshot
{
    /// <summary>
    /// Timestamp when the ping was taken
    /// </summary>
    public DateTime Timestamp { get; init; }

    /// <summary>
    /// Latency in milliseconds (or 9999 for timeout)
    /// </summary>
    public double Latency { get; init; }

    /// <summary>
    /// Whether the ping was successful
    /// </summary>
    public bool IsSuccessful { get; init; }

    /// <summary>
    /// Status description ("Success" or "Timeout")
    /// </summary>
    public string Status { get; init; }

    /// <summary>
    /// Creates a new ping snapshot with current timestamp
    /// </summary>
    public PingSnapshot(double latency, bool isSuccessful)
    {
        Timestamp = DateTime.UtcNow;
        Latency = latency;
        IsSuccessful = isSuccessful;
        Status = isSuccessful ? "Success" : "Timeout";
    }

    /// <summary>
    /// Creates a new ping snapshot with specified timestamp
    /// </summary>
    public PingSnapshot(DateTime timestamp, double latency, bool isSuccessful)
    {
        Timestamp = timestamp;
        Latency = latency;
        IsSuccessful = isSuccessful;
        Status = isSuccessful ? "Success" : "Timeout";
    }
}