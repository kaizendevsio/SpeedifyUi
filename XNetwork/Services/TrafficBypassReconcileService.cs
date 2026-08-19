using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Applies the bypass rules once at startup, then keeps their policy routes alive.
/// </summary>
/// <remarks>
/// Applying once was not enough. The kernel deletes every route that references a device when
/// that device goes down, and a DHCP lease renewal counts, so an adapter blip silently empties
/// the bypass route tables. The nft marking and the fwmark rules survive, so matched packets are
/// still marked, still sent to their table, find nothing there, fall through to main, and leave
/// through the tunnel. Nothing errors; the only symptom is traffic exiting the wrong country.
/// </remarks>
public sealed class TrafficBypassReconcileService(
    TrafficBypassService trafficBypassService,
    TrafficBypassSettings settings,
    ILogger<TrafficBypassReconcileService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool supported;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            var status = await trafficBypassService.ApplyAsync(stoppingToken).ConfigureAwait(false);
            supported = status.IsSupported;
            if (status.Applied)
            {
                logger.LogInformation("Traffic bypass startup apply succeeded: {Message}", status.Message);
            }
            else if (status.IsSupported)
            {
                logger.LogWarning("Traffic bypass startup apply did not complete: {Message} {Error}", status.Message, status.Error);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Traffic bypass startup apply failed");
            // A failed apply is exactly when reconciliation matters most, so keep going.
            supported = true;
        }

        // Nothing to reconcile off-router; the helper is Linux-only.
        if (!supported)
        {
            return;
        }

        await ReconcileLoopAsync(stoppingToken).ConfigureAwait(false);
    }

    private async Task ReconcileLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(settings.ReconcileInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var status = await trafficBypassService.RepairAsync(stoppingToken).ConfigureAwait(false);
                if (status.Applied)
                {
                    // Only announce actual repairs. A healthy pass every 20s would be noise.
                    if (status.Message.Contains("repaired", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("Traffic bypass {Message}", status.Message);
                    }

                    continue;
                }

                // Repair refuses to run when the nft table itself is missing, because routes
                // with nothing marking traffic achieve nothing. That case needs the full
                // rebuild, which is the one thing repair deliberately will not do.
                logger.LogWarning(
                    "Traffic bypass repair did not complete: {Message} {Error}",
                    status.Message,
                    status.Error);

                var rebuilt = await trafficBypassService.ApplyAsync(stoppingToken).ConfigureAwait(false);
                logger.Log(
                    rebuilt.Applied ? LogLevel.Information : LogLevel.Warning,
                    "Traffic bypass rebuilt after failed repair: {Message} {Error}",
                    rebuilt.Message,
                    rebuilt.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Traffic bypass reconcile failed");
            }
        }
    }
}
