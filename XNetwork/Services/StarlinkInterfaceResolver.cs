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
        if (xbondMatch.IsAvailable && !_settings.AdapterProbeEnabled)
        {
            return xbondMatch;
        }

        if (xbondMatch.IsAvailable &&
            await VerifyInterfaceAsync(xbondMatch.InterfaceName!, cancellationToken).ConfigureAwait(false))
        {
            return CacheProbe(StarlinkInterfaceResolution.Available(
                xbondMatch.InterfaceName!,
                $"Verified {xbondMatch.InterfaceName} against Starlink management host {_settings.Host}."));
        }

        return await ResolveByProbeAsync(
            xbondMatch.Reason,
            xbondMatch.IsAvailable ? xbondMatch.InterfaceName : null,
            cancellationToken).ConfigureAwait(false);
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
                ? StarlinkInterfaceResolution.Unavailable("No live uLink Starlink path matched the configured adapter hints.")
                : StarlinkInterfaceResolution.Available(match.InterfaceName, $"Matched live uLink path {match.Name}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve Starlink interface from uLink paths");
            return StarlinkInterfaceResolution.Unavailable($"Could not read uLink paths: {ex.Message}");
        }
    }

    private async Task<StarlinkInterfaceResolution> ResolveByProbeAsync(
        string pathReason,
        string? excludedInterface,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (CanUseCachedProbe(now, excludedInterface))
        {
            return _cachedProbeResolution!;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = DateTimeOffset.UtcNow;
            if (CanUseCachedProbe(now, excludedInterface))
            {
                return _cachedProbeResolution!;
            }

            var interfaces = await _interfaceProvider(cancellationToken).ConfigureAwait(false);
            var candidates = interfaces
                .Where(item => item.IsDashboardCandidate && !string.IsNullOrWhiteSpace(item.Device))
                .Select(item => item.Device)
                .Where(item => !string.Equals(item, excludedInterface, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var candidate in candidates)
            {
                try
                {
                    if (await _probeInterfaceAsync(candidate, cancellationToken).ConfigureAwait(false))
                    {
                        return CacheProbe(StarlinkInterfaceResolution.Available(
                            candidate,
                            $"Starlink management host {_settings.Host} answered on {candidate}."));
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(ex, "Starlink probe timed out on interface {InterfaceName}", candidate);
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

    private bool CanUseCachedProbe(DateTimeOffset now, string? excludedInterface) =>
        _cachedProbeResolution is not null &&
        now < _cachedProbeExpiresAtUtc &&
        !string.Equals(
            _cachedProbeResolution.InterfaceName,
            excludedInterface,
            StringComparison.OrdinalIgnoreCase);

    private async Task<bool> VerifyInterfaceAsync(string interfaceName, CancellationToken cancellationToken)
    {
        try
        {
            return await _probeInterfaceAsync(interfaceName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Starlink verification timed out on interface {InterfaceName}", interfaceName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Starlink verification failed on interface {InterfaceName}", interfaceName);
            return false;
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
