namespace XNetwork.Models;

/// <summary>
/// Connection health status levels
/// </summary>
public enum ConnectionStatus
{
    Unknown,
    Initializing,
    Excellent,
    Good,
    Fair,
    Poor,
    Critical
}

/// <summary>
/// Overall connection health assessment with thread-safe access
/// </summary>
public class ConnectionHealth
{
    private readonly object _lock = new object();
    private ConnectionStatus _status = ConnectionStatus.Unknown;
    private double _averageLatency;
    private double _averagePacketLoss;
    private double _averageSpeed;
    private double _stabilityScore;
    private int _sampleCount;
    private DateTime _lastUpdated;

    /// <summary>
    /// Overall connection status
    /// </summary>
    public ConnectionStatus Status
    {
        get { lock (_lock) return _status; }
        set { lock (_lock) _status = value; }
    }

    /// <summary>
    /// Average latency in milliseconds
    /// </summary>
    public double AverageLatency
    {
        get { lock (_lock) return _averageLatency; }
        set { lock (_lock) _averageLatency = value; }
    }

    /// <summary>
    /// Average packet loss percentage
    /// </summary>
    public double AveragePacketLoss
    {
        get { lock (_lock) return _averagePacketLoss; }
        set { lock (_lock) _averagePacketLoss = value; }
    }

    /// <summary>
    /// Average speed in Mbps
    /// </summary>
    public double AverageSpeed
    {
        get { lock (_lock) return _averageSpeed; }
        set { lock (_lock) _averageSpeed = value; }
    }

    /// <summary>
    /// Stability score (0-1, higher is more stable)
    /// </summary>
    public double StabilityScore
    {
        get { lock (_lock) return _stabilityScore; }
        set { lock (_lock) _stabilityScore = value; }
    }

    /// <summary>
    /// Number of samples in rolling window
    /// </summary>
    public int SampleCount
    {
        get { lock (_lock) return _sampleCount; }
        set { lock (_lock) _sampleCount = value; }
    }

    /// <summary>
    /// Last update timestamp
    /// </summary>
    public DateTime LastUpdated
    {
        get { lock (_lock) return _lastUpdated; }
        set { lock (_lock) _lastUpdated = value; }
    }

    /// <summary>
    /// Whether the service has enough data to report health
    /// </summary>
    public bool IsInitialized => SampleCount >= 3;

    /// <summary>
    /// Gets a snapshot of all health metrics in a single lock operation
    /// </summary>
    public (ConnectionStatus status, double latency, double packetLoss, double speed, double stability, int samples, DateTime lastUpdated) GetSnapshot()
    {
        lock (_lock)
        {
            return (_status, _averageLatency, _averagePacketLoss, _averageSpeed, _stabilityScore, _sampleCount, _lastUpdated);
        }
    }

    /// <summary>
    /// Updates all metrics in a single lock operation
    /// </summary>
    public void UpdateMetrics(ConnectionStatus status, double latency, double packetLoss, double speed, double stability, int samples)
    {
        lock (_lock)
        {
            _status = status;
            _averageLatency = latency;
            _averagePacketLoss = packetLoss;
            _averageSpeed = speed;
            _stabilityScore = stability;
            _sampleCount = samples;
            _lastUpdated = DateTime.UtcNow;
        }
    }
}