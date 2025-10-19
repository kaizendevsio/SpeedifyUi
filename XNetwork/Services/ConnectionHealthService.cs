using System.Collections.Concurrent;
using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Services;

/// <summary>
/// Background service that monitors connection health in real-time using streaming stats
/// </summary>
public class ConnectionHealthService : BackgroundService, IConnectionHealthService
{
    private readonly ILogger<ConnectionHealthService> _logger;
    private readonly SpeedifyService _speedifyService;
    private readonly ConcurrentDictionary<string, CircularBuffer<HealthSnapshot>> _adapterBuffers;
    private readonly ConcurrentDictionary<string, DateTime> _adapterLastSeen;
    private readonly ConnectionHealth _overallHealth;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _isInitialized;

    // Configuration constants
    private const int BUFFER_SIZE = 10; // 10 samples per adapter
    private const int MIN_SAMPLES_FOR_HEALTH = 3; // Minimum samples before reporting
    private const int STALE_ADAPTER_TIMEOUT_MINUTES = 5; // Clean up after 5 minutes
    private const int CLEANUP_INTERVAL_SECONDS = 60; // Run cleanup every minute

    // Health thresholds (more lenient)
    private static class Thresholds
    {
        // Excellent thresholds
        public const double EXCELLENT_LATENCY = 150;
        public const double EXCELLENT_PACKET_LOSS = 3;
        public const double EXCELLENT_SPEED = 40;

        // Good thresholds
        public const double GOOD_LATENCY = 250;
        public const double GOOD_PACKET_LOSS = 7;
        public const double GOOD_SPEED = 15;

        // Fair thresholds
        public const double FAIR_LATENCY = 400;
        public const double FAIR_PACKET_LOSS = 12;
        public const double FAIR_SPEED = 5;

        // Idle detection (good latency but minimal throughput)
        public const double IDLE_LATENCY = 300;      // Latency must be acceptable
        public const double IDLE_PACKET_LOSS = 10;   // Packet loss must be acceptable
        public const double IDLE_SPEED = 0.5;        // Speed below this = idle

        // Poor thresholds
        public const double POOR_LATENCY = 600;
        public const double POOR_PACKET_LOSS = 20;
        public const double POOR_SPEED = 1;
    }

    public ConnectionHealthService(
        ILogger<ConnectionHealthService> logger,
        SpeedifyService speedifyService)
    {
        _logger = logger;
        _speedifyService = speedifyService;
        _adapterBuffers = new ConcurrentDictionary<string, CircularBuffer<HealthSnapshot>>();
        _adapterLastSeen = new ConcurrentDictionary<string, DateTime>();
        _overallHealth = new ConnectionHealth();
    }

    /// <inheritdoc/>
    public ConnectionHealth GetOverallHealth()
    {
        return _overallHealth;
    }

    /// <inheritdoc/>
    public HealthMetrics? GetAdapterHealth(string adapterId)
    {
        if (!_adapterBuffers.TryGetValue(adapterId, out var buffer))
            return null;

        return CalculateMetrics(buffer);
    }

    /// <inheritdoc/>
    public Dictionary<string, HealthMetrics> GetAllAdapterHealth()
    {
        var result = new Dictionary<string, HealthMetrics>();

        foreach (var kvp in _adapterBuffers)
        {
            var metrics = CalculateMetrics(kvp.Value);
            if (metrics != null)
                result[kvp.Key] = metrics;
        }

        return result;
    }

    /// <inheritdoc/>
    public bool IsInitialized()
    {
        return _isInitialized;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ConnectionHealthService starting");

        // Start cleanup task
        var cleanupTask = RunCleanupLoopAsync(stoppingToken);

        // Start stats monitoring task
        var monitoringTask = RunMonitoringLoopAsync(stoppingToken);

        // Wait for both tasks
        await Task.WhenAll(cleanupTask, monitoringTask);

        _logger.LogInformation("ConnectionHealthService stopped");
    }

    private async Task RunMonitoringLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _logger.LogDebug("Starting stats stream monitoring");

                await foreach (var connection in _speedifyService.GetStatsAsync(stoppingToken))
                {
                    // Process the connection snapshot
                    ProcessConnectionSnapshot(connection);

                    // Update overall health periodically after processing connections
                    UpdateOverallHealth();

                    // Mark as initialized after first successful update with sufficient samples
                    if (!_isInitialized && _overallHealth.SampleCount >= MIN_SAMPLES_FOR_HEALTH)
                    {
                        await _initializationLock.WaitAsync(stoppingToken);
                        try
                        {
                            _isInitialized = true;
                            _logger.LogInformation("ConnectionHealthService initialized with {Count} samples", _overallHealth.SampleCount);
                        }
                        finally
                        {
                            _initializationLock.Release();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in stats monitoring loop");
                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CLEANUP_INTERVAL_SECONDS), stoppingToken);
                CleanupStaleAdapters();
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in cleanup loop");
            }
        }
    }

    private void ProcessConnectionSnapshot(ConnectionItem connection)
    {
        // Convert bytes/sec to Mbps
        var speedMbps = (connection.ReceiveBps + connection.SendBps) * 8.0 / 1_000_000.0;

        // Calculate average packet loss from send and receive loss
        var packetLoss = (connection.LossSend + connection.LossReceive) / 2.0;

        // Create snapshot
        var snapshot = new HealthSnapshot(
            latency: connection.LatencyMs,
            packetLoss: packetLoss,
            speed: speedMbps
        );

        // Get or create buffer for this adapter
        var buffer = _adapterBuffers.GetOrAdd(
            connection.AdapterId,
            _ => new CircularBuffer<HealthSnapshot>(BUFFER_SIZE)
        );

        // Add snapshot to buffer
        buffer.Add(snapshot);

        // Update last seen timestamp
        _adapterLastSeen[connection.AdapterId] = DateTime.UtcNow;
    }

    private void UpdateOverallHealth()
    {
        var allMetrics = GetAllAdapterHealth();
        
        if (allMetrics.Count == 0)
        {
            _overallHealth.UpdateMetrics(
                ConnectionStatus.Unknown,
                latency: 0,
                packetLoss: 0,
                speed: 0,
                stability: 0,
                samples: 0
            );
            return;
        }

        // Calculate weighted averages based on sample count
        var totalSamples = allMetrics.Values.Sum(m => m.SampleCount);
        var avgLatency = allMetrics.Values.Sum(m => m.AverageLatency * m.SampleCount) / totalSamples;
        var avgPacketLoss = allMetrics.Values.Sum(m => m.AveragePacketLoss * m.SampleCount) / totalSamples;
        var avgSpeed = allMetrics.Values.Sum(m => m.AverageSpeed * m.SampleCount) / totalSamples;
        var avgStability = allMetrics.Values.Sum(m => m.StabilityScore * m.SampleCount) / totalSamples;

        // Determine overall status (use worst status among adapters)
        var worstStatus = allMetrics.Values
            .Select(m => m.Status)
            .OrderByDescending(s => (int)s)
            .FirstOrDefault(ConnectionStatus.Unknown);

        _overallHealth.UpdateMetrics(
            worstStatus,
            avgLatency,
            avgPacketLoss,
            avgSpeed,
            avgStability,
            totalSamples
        );
    }

    private void CleanupStaleAdapters()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-STALE_ADAPTER_TIMEOUT_MINUTES);
        var staleAdapters = _adapterLastSeen
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var adapterId in staleAdapters)
        {
            _adapterBuffers.TryRemove(adapterId, out _);
            _adapterLastSeen.TryRemove(adapterId, out _);
            _logger.LogDebug("Removed stale adapter: {AdapterId}", adapterId);
        }

        if (staleAdapters.Count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} stale adapters", staleAdapters.Count);
        }
    }

    private HealthMetrics? CalculateMetrics(CircularBuffer<HealthSnapshot> buffer)
    {
        var snapshots = buffer.GetItems();
        
        if (snapshots.Length < MIN_SAMPLES_FOR_HEALTH)
            return null;

        // Calculate averages
        var avgLatency = snapshots.Average(s => s.Latency);
        var avgPacketLoss = snapshots.Average(s => s.PacketLoss);
        var avgSpeed = snapshots.Average(s => s.Speed);

        // Calculate min/max latency
        var minLatency = snapshots.Min(s => s.Latency);
        var maxLatency = snapshots.Max(s => s.Latency);

        // Calculate standard deviation of latency
        var latencyVariance = snapshots.Average(s => Math.Pow(s.Latency - avgLatency, 2));
        var latencyStdDev = Math.Sqrt(latencyVariance);

        // Calculate stability score (inverse of coefficient of variation, clamped to 0-1)
        var coefficientOfVariation = avgLatency > 0 ? latencyStdDev / avgLatency : 0;
        var stabilityScore = Math.Max(0, Math.Min(1, 1 - coefficientOfVariation));

        // Determine connection status
        var status = DetermineConnectionStatus(avgLatency, avgPacketLoss, avgSpeed);

        return new HealthMetrics(
            avgLatency,
            avgPacketLoss,
            avgSpeed,
            minLatency,
            maxLatency,
            latencyStdDev,
            stabilityScore,
            snapshots.Length,
            status
        );
    }

    private ConnectionStatus DetermineConnectionStatus(double latency, double packetLoss, double speed)
    {
        // Idle detection - good latency/packet loss but minimal throughput
        // This indicates a connected but inactive connection (no data transfer)
        if (speed < Thresholds.IDLE_SPEED &&
            latency < Thresholds.IDLE_LATENCY &&
            packetLoss < Thresholds.IDLE_PACKET_LOSS)
        {
            return ConnectionStatus.Idle;
        }

        // Critical conditions (any of these)
        if (latency > Thresholds.POOR_LATENCY ||
            packetLoss > Thresholds.POOR_PACKET_LOSS ||
            speed < Thresholds.POOR_SPEED)
        {
            return ConnectionStatus.Critical;
        }

        // Poor conditions
        if (latency > Thresholds.FAIR_LATENCY ||
            packetLoss > Thresholds.FAIR_PACKET_LOSS ||
            speed < Thresholds.FAIR_SPEED)
        {
            return ConnectionStatus.Poor;
        }

        // Fair conditions
        if (latency > Thresholds.GOOD_LATENCY ||
            packetLoss > Thresholds.GOOD_PACKET_LOSS ||
            speed < Thresholds.GOOD_SPEED)
        {
            return ConnectionStatus.Fair;
        }

        // Good conditions
        if (latency > Thresholds.EXCELLENT_LATENCY ||
            packetLoss > Thresholds.EXCELLENT_PACKET_LOSS ||
            speed < Thresholds.EXCELLENT_SPEED)
        {
            return ConnectionStatus.Good;
        }

        // Excellent - all metrics within excellent thresholds
        return ConnectionStatus.Excellent;
    }

    public override void Dispose()
    {
        _initializationLock.Dispose();
        base.Dispose();
    }
}