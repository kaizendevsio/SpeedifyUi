using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Services;

public sealed class StarlinkInterfaceResolver : IStarlinkInterfaceResolver
{
    private readonly StarlinkTelemetrySettings _settings;
    private readonly XBondSnapshotCache _xbondSnapshotCache;
    private readonly InterfaceMetadataService _interfaceMetadataService;
    private readonly ILogger<StarlinkInterfaceResolver> _logger;
    private readonly Func<CancellationToken, Task<IReadOnlyList<InterfaceMetadataService.InterfaceMetadata>>> _interfaceProvider;
    private readonly Func<string, CancellationToken, Task<bool>> _probeInterfaceAsync;
    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private StarlinkInterfaceResolution? _cachedProbeResolution;
    private DateTimeOffset _cachedProbeExpiresAtUtc = DateTimeOffset.MinValue;

    public StarlinkInterfaceResolver(
        StarlinkTelemetrySettings settings,
        XBondSnapshotCache xbondSnapshotCache,
        InterfaceMetadataService interfaceMetadataService,
        ILogger<StarlinkInterfaceResolver> logger)
        : this(settings, xbondSnapshotCache, interfaceMetadataService, logger, null, null)
    {
    }

    public StarlinkInterfaceResolver(
        StarlinkTelemetrySettings settings,
        XBondSnapshotCache xbondSnapshotCache,
        InterfaceMetadataService interfaceMetadataService,
        ILogger<StarlinkInterfaceResolver> logger,
        Func<CancellationToken, Task<IReadOnlyList<InterfaceMetadataService.InterfaceMetadata>>>? interfaceProvider,
        Func<string, CancellationToken, Task<bool>>? probeInterfaceAsync)
    {
        _settings = settings;
        _xbondSnapshotCache = xbondSnapshotCache;
        _interfaceMetadataService = interfaceMetadataService;
        _logger = logger;
        _interfaceProvider = interfaceProvider ?? interfaceMetadataService.GetInterfacesAsync;
        _probeInterfaceAsync = probeInterfaceAsync ?? ProbeInterfaceAsync;
    }

    public async Task<StarlinkInterfaceResolution> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var xbondMatch = await ResolveFromXBondPathsAsync(cancellationToken).ConfigureAwait(false);
        if (xbondMatch.IsAvailable)
        {
            return xbondMatch;
        }

        if (!_settings.AdapterProbeEnabled)
        {
            return xbondMatch;
        }

        return await ResolveByProbeAsync(xbondMatch.Reason, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StarlinkInterfaceResolution> ResolveFromXBondPathsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _xbondSnapshotCache.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var match = snapshot.DashboardPaths
                .Where(path => path.InterfaceUp && !string.IsNullOrWhiteSpace(path.InterfaceName))
                .FirstOrDefault(path => StarlinkAdapterDetector.HasStarlinkMetadata(path, _settings.AdapterNameHints));

            return match is null
                ? StarlinkInterfaceResolution.Unavailable("No live XBond Starlink path matched the configured adapter hints.")
                : StarlinkInterfaceResolution.Available(match.InterfaceName, $"Matched live XBond path {match.Name}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve Starlink interface from XBond paths");
            return StarlinkInterfaceResolution.Unavailable($"Could not read XBond paths: {ex.Message}");
        }
    }

    private async Task<StarlinkInterfaceResolution> ResolveByProbeAsync(string pathReason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedProbeResolution is not null && now < _cachedProbeExpiresAtUtc)
        {
            return _cachedProbeResolution;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (_cachedProbeResolution is not null && now < _cachedProbeExpiresAtUtc)
            {
                return _cachedProbeResolution;
            }

            var interfaces = await _interfaceProvider(cancellationToken).ConfigureAwait(false);
            var candidates = interfaces
                .Where(item => item.IsDashboardCandidate && !string.IsNullOrWhiteSpace(item.Device))
                .Select(item => item.Device)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in candidates)
            {
                if (await _probeInterfaceAsync(candidate, cancellationToken).ConfigureAwait(false))
                {
                    return CacheProbe(StarlinkInterfaceResolution.Available(
                        candidate,
                        $"Starlink management host {_settings.Host} answered on {candidate}."));
                }
            }

            return CacheProbe(StarlinkInterfaceResolution.Unavailable(
                $"{pathReason} Bound probe to {_settings.Host} failed on {candidates.Length} physical adapter(s)."));
        }
        finally
        {
            _probeLock.Release();
        }
    }

    private StarlinkInterfaceResolution CacheProbe(StarlinkInterfaceResolution resolution)
    {
        _cachedProbeResolution = resolution;
        _cachedProbeExpiresAtUtc = DateTimeOffset.UtcNow + _settings.AdapterProbeInterval;
        return resolution;
    }

    private async Task<bool> ProbeInterfaceAsync(string interfaceName, CancellationToken cancellationToken)
    {
        try
        {
            using var client = StarlinkBoundHttpClientFactory.CreateHttpClient(_settings, interfaceName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://{_settings.Host}/"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Starlink probe failed on interface {InterfaceName}", interfaceName);
            return false;
        }
    }
}
