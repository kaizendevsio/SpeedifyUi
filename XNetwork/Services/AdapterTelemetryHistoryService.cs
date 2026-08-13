using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Samples uLink path metrics once per second into a bounded per-adapter history so the adapter
/// details sheet can render a populated chart the moment it opens.
/// </summary>
public sealed class AdapterTelemetryHistoryService(
    ILogger<AdapterTelemetryHistoryService> logger,
    XBondSnapshotCache snapshotCache,
    AdapterTelemetryHistory history,
    TimeProvider? timeProvider = null) : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public IReadOnlyList<AdapterTelemetrySample> GetSamples(string interfaceName) => history.GetSamples(interfaceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var snapshot = await snapshotCache.GetSnapshotAsync(stoppingToken).ConfigureAwait(false);
                var now = _timeProvider.GetUtcNow();

                foreach (var path in snapshot.Paths)
                {
                    history.Add(path.InterfaceName, new AdapterTelemetrySample
                    {
                        TimestampUtc = now,
                        RttMs = path.RttMs,
                        LossPercent = path.DisplayLossPercent,
                        JitterMs = path.JitterMs,
                        DownloadMbps = path.DownloadMbps,
                        UploadMbps = path.UploadMbps
                    });
                }

                history.EvictMissing(snapshot.Paths.Select(path => path.InterfaceName));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Adapter telemetry history sampling error");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
