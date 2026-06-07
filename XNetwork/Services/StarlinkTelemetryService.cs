using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkTelemetryService : BackgroundService, IStarlinkTelemetryService
{
    private readonly StarlinkTelemetrySettings _settings;
    private readonly StarlinkDeviceClient _client;
    private readonly ILogger<StarlinkTelemetryService> _logger;
    private StarlinkTelemetrySnapshot _snapshot = StarlinkTelemetrySnapshot.Unavailable();

    public StarlinkTelemetryService(
        StarlinkTelemetrySettings settings,
        StarlinkDeviceClient client,
        ILogger<StarlinkTelemetryService> logger)
    {
        _settings = settings;
        _client = client;
        _logger = logger;
    }

    public StarlinkTelemetrySnapshot GetSnapshot()
    {
        var snapshot = _snapshot;
        if (snapshot.IsAvailable && snapshot.IsStale(_settings.StaleAfter))
        {
            return snapshot with
            {
                IsAvailable = false,
                Error = snapshot.Error ?? "Starlink telemetry is stale"
            };
        }

        return snapshot;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("StarlinkTelemetryService is disabled");
            return;
        }

        _logger.LogInformation(
            "StarlinkTelemetryService polling {Host}:{Port}",
            _settings.Host,
            _settings.GrpcPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(_settings.PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            _snapshot = await _client.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Starlink telemetry poll failed");

            var previous = _snapshot;
            if (previous.IsAvailable && !previous.IsStale(_settings.StaleAfter))
            {
                _snapshot = previous with { Error = ex.Message };
                return;
            }

            _snapshot = StarlinkTelemetrySnapshot.Unavailable(ex.Message);
        }
    }
}
