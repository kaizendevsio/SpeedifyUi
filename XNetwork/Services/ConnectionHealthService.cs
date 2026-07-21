using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Services;

public class ConnectionHealthService(
    ILogger<ConnectionHealthService> logger,
    XBondSnapshotCache xbondSnapshotCache) : BackgroundService, IConnectionHealthService
{
    private const string PingTarget = "8.8.8.8";
    private const int PingIntervalMs = 500;
    private const int PingTimeoutMs = 3000;
    private const double FailedPingLatency = 9999.0;
    private const int BufferSize = 30;
    private const int MinSamplesForHealth = 3;

    private readonly ConcurrentDictionary<string, CircularBuffer<HealthSnapshot>> _adapterBuffers = new();
    private readonly ConcurrentDictionary<string, DateTime> _adapterLastSeen = new();
    private readonly ConnectionHealth _overallHealth = new();
    private readonly CircularBuffer<PingSnapshot> _pingBuffer = new(BufferSize);
    private readonly Ping _ping = new();
    private bool _isInitialized;

    public ConnectionHealth GetOverallHealth() => _overallHealth;

    public HealthMetrics? GetAdapterHealth(string adapterId)
    {
        return _adapterBuffers.TryGetValue(adapterId, out var buffer) ? CalculateMetrics(buffer) : null;
    }

    public Dictionary<string, HealthMetrics> GetAllAdapterHealth()
    {
        return _adapterBuffers
            .Select(item => new { item.Key, Metrics = CalculateMetrics(item.Value) })
            .Where(item => item.Metrics is not null)
            .ToDictionary(item => item.Key, item => item.Metrics!);
    }

    public bool IsInitialized() => _isInitialized;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ConnectionHealthService starting with uLink path health");

        var pingTask = RunPingLoopAsync(stoppingToken);
        var xbondTask = RunXBondPathLoopAsync(stoppingToken);
        var cleanupTask = RunCleanupLoopAsync(stoppingToken);

        await Task.WhenAll(pingTask, xbondTask, cleanupTask).ConfigureAwait(false);
    }

    private async Task RunPingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ProcessPingSnapshot(await SendPingAsync(stoppingToken).ConfigureAwait(false));
                _isInitialized = _overallHealth.SampleCount >= MinSamplesForHealth;
                await Task.Delay(PingIntervalMs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error in ping health loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private async Task RunXBondPathLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await xbondSnapshotCache.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                foreach (var path in snapshot.Paths)
                {
                    var key = path.InterfaceName;
                    var buffer = _adapterBuffers.GetOrAdd(key, _ => new CircularBuffer<HealthSnapshot>(BufferSize));
                    buffer.Add(new HealthSnapshot(
                        path.RttMs ?? FailedPingLatency,
                        path.LossPercent,
                        path.ThroughputMbps));
                    _adapterLastSeen[key] = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Error in uLink path health loop");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunCleanupLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                var cutoff = DateTime.UtcNow.AddMinutes(-5);
                foreach (var key in _adapterLastSeen.Where(item => item.Value < cutoff).Select(item => item.Key).ToArray())
                {
                    _adapterBuffers.TryRemove(key, out _);
                    _adapterLastSeen.TryRemove(key, out _);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<PingSnapshot> SendPingAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reply = OperatingSystem.IsLinux()
                ? await _ping.SendPingAsync(PingTarget, PingTimeoutMs).ConfigureAwait(false)
                : await _ping.SendPingAsync(
                    PingTarget,
                    PingTimeoutMs,
                    new byte[32],
                    new PingOptions(ttl: 128, dontFragment: true)).ConfigureAwait(false);

            return reply.Status == IPStatus.Success
                ? new PingSnapshot(reply.RoundtripTime, isSuccessful: true)
                : new PingSnapshot(FailedPingLatency, isSuccessful: false);
        }
        catch (Exception ex) when (ex is PingException or InvalidOperationException)
        {
            logger.LogDebug(ex, "Ping health probe failed");
            return new PingSnapshot(FailedPingLatency, isSuccessful: false);
        }
    }

    private void ProcessPingSnapshot(PingSnapshot snapshot)
    {
        _pingBuffer.Add(snapshot);
        var snapshots = _pingBuffer.GetItems();
        if (snapshots.Length < MinSamplesForHealth)
        {
            return;
        }

        var successful = snapshots.Where(item => item.IsSuccessful).Select(item => item.Latency).ToArray();
        if (successful.Length == 0)
        {
            _overallHealth.UpdateMetrics(ConnectionStatus.Critical, FailedPingLatency, 100, 0, 0, snapshots.Length, successRate: 0);
            return;
        }

        var successRate = successful.Length / (double)snapshots.Length * 100;
        var average = successful.Average();
        var jitter = Math.Sqrt(successful.Average(value => Math.Pow(value - average, 2)));
        var stability = average > 0 ? Math.Clamp(1 - jitter / average, 0, 1) : 0;

        _overallHealth.UpdateMetrics(
            DetermineStatus(average, jitter, successRate),
            average,
            100 - successRate,
            0,
            stability,
            snapshots.Length,
            jitter,
            successRate);
    }

    private static ConnectionStatus DetermineStatus(double latency, double jitter, double successRate)
    {
        if (latency > 300 || jitter > 60 || successRate < 80)
        {
            return ConnectionStatus.Critical;
        }

        if (latency > 150 || jitter > 30 || successRate < 90)
        {
            return ConnectionStatus.Poor;
        }

        if (latency > 80 || jitter > 15 || successRate < 95)
        {
            return ConnectionStatus.Fair;
        }

        if (latency > 50 || jitter > 10 || successRate < 98)
        {
            return ConnectionStatus.Good;
        }

        return ConnectionStatus.Excellent;
    }

    private static HealthMetrics? CalculateMetrics(CircularBuffer<HealthSnapshot> buffer)
    {
        var snapshots = buffer.GetItems();
        if (snapshots.Length < MinSamplesForHealth)
        {
            return null;
        }

        var averageLatency = snapshots.Average(item => item.Latency);
        var averageLoss = snapshots.Average(item => item.PacketLoss);
        var averageSpeed = snapshots.Average(item => item.Speed);
        var minLatency = snapshots.Min(item => item.Latency);
        var maxLatency = snapshots.Max(item => item.Latency);
        var jitter = Math.Sqrt(snapshots.Average(item => Math.Pow(item.Latency - averageLatency, 2)));
        var stability = averageLatency > 0 ? Math.Clamp(1 - jitter / averageLatency, 0, 1) : 0;

        return new HealthMetrics(
            averageLatency,
            averageLoss,
            averageSpeed,
            minLatency,
            maxLatency,
            jitter,
            stability,
            snapshots.Length,
            ConnectionStatus.Unknown,
            jitter,
            Math.Max(0, 100 - averageLoss));
    }

    public override void Dispose()
    {
        _ping.Dispose();
        base.Dispose();
    }
}
