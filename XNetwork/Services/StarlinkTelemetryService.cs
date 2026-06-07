using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkTelemetryService : BackgroundService, IStarlinkTelemetryService
{
    private readonly StarlinkTelemetrySettings _settings;
    private readonly StarlinkDeviceClient _client;
    private readonly ILogger<StarlinkTelemetryService> _logger;
    private readonly StarlinkTelemetryHistory _history = new();
    private readonly SemaphoreSlim _commandLock = new(1, 1);
    private StarlinkTelemetrySnapshot _snapshot = StarlinkTelemetrySnapshot.Unavailable();
    private StarlinkCapabilitySnapshot _capabilities = StarlinkCapabilitySnapshot.Unavailable();
    private DateTimeOffset _lastCapabilityProbeUtc = DateTimeOffset.MinValue;

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

    public IReadOnlyList<StarlinkTelemetrySnapshot> GetHistory()
    {
        return _history.GetSamples();
    }

    public StarlinkCapabilitySnapshot GetCapabilities()
    {
        return _capabilities;
    }

    public async Task<StarlinkCommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _commandLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _client.ExecuteCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commandLock.Release();
        }
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
            _history.Add(
                _snapshot,
                _settings.HistoryAge,
                _settings.HistorySampleLimit,
                DateTimeOffset.UtcNow);

            await RefreshCapabilitiesIfDueAsync(statusAvailable: true, cancellationToken).ConfigureAwait(false);
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
                await RefreshCapabilitiesIfDueAsync(statusAvailable: true, cancellationToken).ConfigureAwait(false);
                return;
            }

            _snapshot = StarlinkTelemetrySnapshot.Unavailable(ex.Message);
            await RefreshCapabilitiesIfDueAsync(statusAvailable: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshCapabilitiesIfDueAsync(bool statusAvailable, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastCapabilityProbeUtc < _settings.CapabilityProbeInterval)
        {
            return;
        }

        _lastCapabilityProbeUtc = now;
        _capabilities = await _client.GetCapabilitiesAsync(statusAvailable, cancellationToken).ConfigureAwait(false);
    }
}
