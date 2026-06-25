namespace XNetwork.Services;

public sealed class TrafficBypassStartupService(
    TrafficBypassService trafficBypassService,
    ILogger<TrafficBypassStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
            var status = await trafficBypassService.ApplyAsync(stoppingToken).ConfigureAwait(false);
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
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Traffic bypass startup apply failed");
        }
    }
}
