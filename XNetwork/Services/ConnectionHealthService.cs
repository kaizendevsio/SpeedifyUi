using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Services;

/// <summary>
/// Background service that monitors connection health in real-time using ping-based measurements
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

    // Ping-based health check fields
    private readonly CircularBuffer<PingSnapshot> _pingBuffer;
    private readonly Ping _ping;

    // Configuration constants for ping-based health
    private const string PING_TARGET = "1.1.1.1"; // Cloudflare DNS
    private const int PING_INTERVAL_MS = 500; // 2 pings per second
    private const int PING_TIMEOUT_MS = 3000; // 3 second timeout
    private const double FAILED_PING_LATENCY = 9999.0; // Sentinel for failed pings

    // Configuration constants
    private const int BUFFER_SIZE = 10; // 10 samples = 5 seconds at 500ms interval
    private const int MIN_SAMPLES_FOR_HEALTH = 3; // Minimum samples before reporting
    private const int STALE_ADAPTER_TIMEOUT_MINUTES = 5; // Clean up after 5 minutes
    private const int CLEANUP_INTERVAL_SECONDS = 60; // Run cleanup every minute

    // Ping-based health thresholds (from design document)
    private static class PingThresholds
    {
        // Excellent - Gaming/VoIP quality
        public const double EXCELLENT_LATENCY = 30;
        public const double EXCELLENT_JITTER = 5;
        public const double EXCELLENT_SUCCESS_RATE = 98;

        // Good - Normal browsing/streaming
        public const double GOOD_LATENCY = 80;
        public const double GOOD_JITTER = 15;
        public const double GOOD_SUCCESS_RATE = 95;

        // Fair - Acceptable for most uses
        public const double FAIR_LATENCY = 150;
        public const double FAIR_JITTER = 30;
        public const double FAIR_SUCCESS_RATE = 90;

        // Poor - Degraded experience
        public const double POOR_LATENCY = 300;
        public const double POOR_JITTER = 60;
        public const double POOR_SUCCESS_RATE = 80;

        // Critical - Anything above poor thresholds
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
        
        // Initialize ping-based health check
        _pingBuffer = new CircularBuffer<PingSnapshot>(BUFFER_SIZE);
        _ping = new Ping();
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
        _logger.LogInformation("ConnectionHealthService starting with ping-based health checks");

        // Start cleanup task
        var cleanupTask = RunCleanupLoopAsync(stoppingToken);

        // Start ping-based health monitoring task
        var pingTask = RunPingLoopAsync(stoppingToken);

        // Start stats monitoring task (still needed for other metrics)
        var monitoringTask = RunMonitoringLoopAsync(stoppingToken);

        // Wait for all tasks
        await Task.WhenAll(cleanupTask, pingTask, monitoringTask);

        _logger.LogInformation("ConnectionHealthService stopped");
    }

    private async Task RunPingLoopAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting ping-based health monitoring to {Target}", PING_TARGET);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Send ping and get snapshot
                var snapshot = await SendPingAsync(stoppingToken);
                
                // Process the ping snapshot
                ProcessPingSnapshot(snapshot);

                // Mark as initialized after first successful update with sufficient samples
                if (!_isInitialized && _overallHealth.SampleCount >= MIN_SAMPLES_FOR_HEALTH)
                {
                    await _initializationLock.WaitAsync(stoppingToken);
                    try
                    {
                        _isInitialized = true;
                        _logger.LogInformation("ConnectionHealthService initialized with {Count} ping samples", _overallHealth.SampleCount);
                    }
                    finally
                    {
                        _initializationLock.Release();
                    }
                }

                // Wait for next ping interval
                await Task.Delay(PING_INTERVAL_MS, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in ping loop");
                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Ping-based health monitoring stopped");
    }

    private async Task<PingSnapshot> SendPingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = await _ping.SendPingAsync(
                PING_TARGET,
                PING_TIMEOUT_MS,
                buffer: new byte[32], // Standard 32-byte buffer
                options: new PingOptions(ttl: 128, dontFragment: true)
            );

            if (reply.Status == IPStatus.Success)
            {
                return new PingSnapshot(
                    latency: reply.RoundtripTime,
                    isSuccessful: true
                );
            }
            else
            {
                _logger.LogWarning("Ping to {Target} failed: {Status}", PING_TARGET, reply.Status);
                return new PingSnapshot(
                    latency: FAILED_PING_LATENCY,
                    isSuccessful: false
                );
            }
        }
        catch (PingException ex)
        {
            _logger.LogWarning(ex, "Ping exception for {Target}", PING_TARGET);
            return new PingSnapshot(
                latency: FAILED_PING_LATENCY,
                isSuccessful: false
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during ping to {Target}", PING_TARGET);
            return new PingSnapshot(
                latency: FAILED_PING_LATENCY,
                isSuccessful: false
            );
        }
    }

    private void ProcessPingSnapshot(PingSnapshot snapshot)
    {
        // Add snapshot to buffer
        _pingBuffer.Add(snapshot);

        var snapshots = _pingBuffer.GetItems();
        if (snapshots.Length < MIN_SAMPLES_FOR_HEALTH)
            return;

        // Calculate success rate
        var successfulPings = snapshots.Count(s => s.IsSuccessful);
        var failedPings = snapshots.Length - successfulPings;
        var successRate = (double)successfulPings / snapshots.Length * 100.0;

        // Calculate latency stats (only for successful pings)
        var successfulLatencies = snapshots
            .Where(s => s.IsSuccessful)
            .Select(s => s.Latency)
            .ToArray();

        if (successfulLatencies.Length == 0)
        {
            // All pings failed - critical status
            _overallHealth.UpdateMetrics(
                ConnectionStatus.Critical,
                latency: FAILED_PING_LATENCY,
                packetLoss: 0,
                speed: 0,
                stability: 0,
                samples: snapshots.Length,
                jitter: 0,
                successRate: 0
            );
            return;
        }

        var avgLatency = successfulLatencies.Average();
        var minLatency = successfulLatencies.Min();
        var maxLatency = successfulLatencies.Max();

        // Calculate jitter (standard deviation of latency)
        var variance = successfulLatencies.Average(l => Math.Pow(l - avgLatency, 2));
        var jitter = Math.Sqrt(variance);

        // Calculate stability score (inverse of coefficient of variation, clamped to 0-1)
        var coefficientOfVariation = avgLatency > 0 ? jitter / avgLatency : 0;
        var stabilityScore = Math.Max(0, Math.Min(1, 1 - coefficientOfVariation));

        // Determine connection status based on ping metrics
        var status = DetermineConnectionStatusFromPing(avgLatency, jitter, successRate);

        _overallHealth.UpdateMetrics(
            status,
            avgLatency,
            packetLoss: 0, // Not used in ping-based health
            speed: 0, // Not used in ping-based health
            stabilityScore,
            snapshots.Length,
            jitter,
            successRate
        );
    }

    private ConnectionStatus DetermineConnectionStatusFromPing(double avgLatency, double jitter, double successRate)
    {
        // Critical: Multiple severe indicators
        if (avgLatency > PingThresholds.POOR_LATENCY ||
            jitter > PingThresholds.POOR_JITTER ||
            successRate < PingThresholds.POOR_SUCCESS_RATE)
        {
            return ConnectionStatus.Critical;
        }

        // Poor: One or more indicators in poor range
        if (avgLatency > PingThresholds.FAIR_LATENCY ||
            jitter > PingThresholds.FAIR_JITTER ||
            successRate < PingThresholds.FAIR_SUCCESS_RATE)
        {
            return ConnectionStatus.Poor;
        }

        // Fair: Average performance
        if (avgLatency > PingThresholds.GOOD_LATENCY ||
            jitter > PingThresholds.GOOD_JITTER ||
            successRate < PingThresholds.GOOD_SUCCESS_RATE)
        {
            return ConnectionStatus.Fair;
        }

        // Good: Better than average
        if (avgLatency > PingThresholds.EXCELLENT_LATENCY ||
            jitter > PingThresholds.EXCELLENT_JITTER ||
            successRate < PingThresholds.EXCELLENT_SUCCESS_RATE)
        {
            return ConnectionStatus.Good;
        }

        // Excellent: Optimal performance
        return ConnectionStatus.Excellent;
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
                    // Process the connection snapshot for adapter-specific metrics
                    ProcessConnectionSnapshot(connection);
                    
                    // Note: Overall health is now determined by ping loop, not stats
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

    // Note: UpdateOverallHealth is no longer used - overall health is determined by ping loop

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

        // Calculate standard deviation of latency (jitter)
        var latencyVariance = snapshots.Average(s => Math.Pow(s.Latency - avgLatency, 2));
        var latencyStdDev = Math.Sqrt(latencyVariance);
        var jitter = latencyStdDev;

        // Calculate stability score (inverse of coefficient of variation, clamped to 0-1)
        var coefficientOfVariation = avgLatency > 0 ? latencyStdDev / avgLatency : 0;
        var stabilityScore = Math.Max(0, Math.Min(1, 1 - coefficientOfVariation));

        // For adapter-specific metrics, status is not determined here
        // Overall health status is determined by ping-based measurements
        var status = ConnectionStatus.Unknown;

        return new HealthMetrics(
            avgLatency,
            avgPacketLoss,
            avgSpeed,
            minLatency,
            maxLatency,
            latencyStdDev,
            stabilityScore,
            snapshots.Length,
            status,
            jitter,
            successRate: 100 // Adapter metrics don't track success rate
        );
    }

    public override void Dispose()
    {
        _ping?.Dispose();
        _initializationLock.Dispose();
        base.Dispose();
    }
}