using System.Collections.Concurrent;
using System.Net;
using XNetwork.Models;

namespace XNetwork.Services;

/// <summary>
/// Discovers each adapter's upstream ISP by issuing one interface-bound HTTP request per adapter to
/// a keyless lookup endpoint, so the answer describes that WAN rather than the uLink tunnel.
/// Results are cached with a success TTL and a failure backoff.
/// </summary>
public sealed class AdapterIdentityService : BackgroundService
{
    private readonly ILogger<AdapterIdentityService> _logger;
    private readonly AdapterIdentitySettings _settings;
    private readonly InterfaceMetadataService _interfaceMetadataService;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string, CancellationToken, Task<string?>> _fetcher;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _probeLock = new(1, 1);

    public AdapterIdentityService(
        ILogger<AdapterIdentityService> logger,
        AdapterIdentitySettings settings,
        InterfaceMetadataService interfaceMetadataService)
        : this(logger, settings, interfaceMetadataService, null, null)
    {
    }

    public AdapterIdentityService(
        ILogger<AdapterIdentityService> logger,
        AdapterIdentitySettings settings,
        InterfaceMetadataService interfaceMetadataService,
        TimeProvider? timeProvider,
        Func<string, string, CancellationToken, Task<string?>>? fetcher)
    {
        _logger = logger;
        _settings = settings;
        _interfaceMetadataService = interfaceMetadataService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fetcher = fetcher ?? FetchBoundAsync;
    }

    public AdapterIdentity? Get(string interfaceName)
    {
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return null;
        }

        return _cache.TryGetValue(interfaceName, out var entry) ? entry.Identity : null;
    }

    public IReadOnlyDictionary<string, AdapterIdentity> GetAll()
    {
        return _cache.ToDictionary(item => item.Key, item => item.Value.Identity, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Interface to normalized ISP name, for <see cref="AdapterNameResolver"/>.</summary>
    public IReadOnlyDictionary<string, string> GetDisplayNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (device, entry) in _cache)
        {
            if (!string.IsNullOrWhiteSpace(entry.Identity.DisplayName))
            {
                names[device] = entry.Identity.DisplayName!;
            }
        }

        return names;
    }

    public void ClearCache() => _cache.Clear();

    public void Invalidate(string interfaceName)
    {
        if (!string.IsNullOrWhiteSpace(interfaceName))
        {
            _cache.TryRemove(interfaceName, out _);
        }
    }

    public void EvictMissing(IEnumerable<string> presentInterfaces)
    {
        var present = presentInterfaces.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var device in _cache.Keys.Where(device => !present.Contains(device)).ToArray())
        {
            _cache.TryRemove(device, out _);
        }
    }

    /// <summary>
    /// Returns the cached identity when it is still fresh, otherwise probes the endpoints in order.
    /// Returns null when the interface must not be probed or lookups are disabled.
    /// </summary>
    public async Task<AdapterIdentity?> RefreshAsync(
        string interfaceName,
        string? gateway,
        CancellationToken cancellationToken)
    {
        if (!_settings.Enabled || ShouldSkip(interfaceName, gateway))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(interfaceName, out var cached) &&
            cached.ExpiresAtUtc > now &&
            string.Equals(cached.Identity.Gateway, gateway, StringComparison.OrdinalIgnoreCase))
        {
            return cached.Identity;
        }

        await _probeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ProbeAsync(interfaceName, gateway, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _probeLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AdapterIdentityService starting (enabled: {Enabled})", _settings.Enabled);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_settings.Enabled)
                {
                    await RefreshAllAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (!_cache.IsEmpty)
                {
                    ClearCache();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Adapter identity refresh loop error");
            }

            try
            {
                await Task.Delay(_settings.RefreshInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RefreshAllAsync(CancellationToken cancellationToken)
    {
        var interfaces = await _interfaceMetadataService.GetInterfacesAsync(cancellationToken).ConfigureAwait(false);
        var routes = await _interfaceMetadataService.GetDefaultGatewayRoutesAsync(cancellationToken).ConfigureAwait(false);
        var gatewayByDevice = routes
            .Where(route => !string.IsNullOrWhiteSpace(route.Device) && !string.IsNullOrWhiteSpace(route.Gateway))
            .GroupBy(route => route.Device, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Gateway, StringComparer.OrdinalIgnoreCase);

        var candidates = interfaces
            .Where(item => item.IsDashboardCandidate)
            .Select(item => item.Device)
            .ToArray();

        EvictMissing(candidates);

        foreach (var device in candidates)
        {
            gatewayByDevice.TryGetValue(device, out var gateway);
            await RefreshAsync(device, gateway, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AdapterIdentity?> ProbeAsync(
        string interfaceName,
        string? gateway,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var errors = new List<string>();

        foreach (var endpoint in _settings.Endpoints.Where(endpoint => !string.IsNullOrWhiteSpace(endpoint)))
        {
            string? body;
            try
            {
                body = await _fetcher(interfaceName, endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"{endpoint}: timed out");
                continue;
            }
            catch (Exception ex)
            {
                errors.Add($"{endpoint}: {ex.Message}");
                continue;
            }

            if (!AdapterIdentityResponseParser.TryParse(body, out var parsed) || parsed is null)
            {
                errors.Add($"{endpoint}: no usable answer");
                continue;
            }

            var identity = new AdapterIdentity
            {
                InterfaceName = interfaceName,
                PublicIp = parsed.PublicIp,
                Isp = parsed.Isp,
                Organization = parsed.Organization,
                AsLabel = parsed.AsLabel,
                City = parsed.City,
                Region = parsed.Region,
                Country = parsed.Country,
                CountryCode = parsed.CountryCode,
                DisplayName = parsed.DisplayName,
                Source = endpoint,
                Gateway = gateway,
                UpdatedAtUtc = now
            };

            _cache[interfaceName] = new CacheEntry(identity, now + _settings.SuccessTtl);
            return identity;
        }

        var failure = AdapterIdentity.Unavailable(
            interfaceName,
            errors.Count > 0 ? string.Join("; ", errors) : "No ISP lookup endpoints configured",
            now,
            gateway);
        _cache[interfaceName] = new CacheEntry(failure, now + _settings.FailureBackoff);
        return failure;
    }

    private async Task<string?> FetchBoundAsync(string interfaceName, string endpoint, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_settings.RequestTimeout);

        using var client = InterfaceBoundHttpClientFactory.CreateClient(interfaceName, _settings.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", "uLink-adapter-identity/1.0");

        using var response = await client.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private static bool ShouldSkip(string interfaceName, string? gateway)
    {
        if (string.IsNullOrWhiteSpace(interfaceName) ||
            interfaceName.StartsWith("xbond", StringComparison.OrdinalIgnoreCase) ||
            interfaceName.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) ||
            interfaceName.StartsWith("p2p-", StringComparison.OrdinalIgnoreCase) ||
            interfaceName.Equals("lo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(gateway);
    }

    private sealed record CacheEntry(AdapterIdentity Identity, DateTimeOffset ExpiresAtUtc);
}
