using XNetwork.Models;

namespace XNetwork.Services;

public class XRouterService(CudyLuciClient cudyClient, CudyApAutomationSettings settings, LocalProcessTrafficService localProcessTrafficService)
{
    private static readonly TimeSpan ClientCacheDuration = TimeSpan.FromMilliseconds(750);
    private readonly SemaphoreSlim _clientRefreshLock = new(1, 1);
    private IReadOnlyList<XRouterClient>? _cachedClients;
    private long _cachedClientsAtTicks;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.ManagementBaseUrl) &&
        (!string.IsNullOrWhiteSpace(settings.AdminPassword) ||
         !string.IsNullOrWhiteSpace(settings.AdminPasswordEnvironmentVariable));

    public async Task<IReadOnlyList<XRouterClient>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return [];
        }

        var nowTicks = DateTime.UtcNow.Ticks;
        if (_cachedClients is not null && nowTicks - Volatile.Read(ref _cachedClientsAtTicks) < ClientCacheDuration.Ticks)
        {
            return _cachedClients;
        }

        await _clientRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nowTicks = DateTime.UtcNow.Ticks;
            if (_cachedClients is not null && nowTicks - Volatile.Read(ref _cachedClientsAtTicks) < ClientCacheDuration.Ticks)
            {
                return _cachedClients;
            }

            var clients = await cudyClient.GetXRouterClientsAsync(settings, cancellationToken).ConfigureAwait(false);
            _cachedClients = clients;
            Volatile.Write(ref _cachedClientsAtTicks, nowTicks);
            return clients;
        }
        finally
        {
            _clientRefreshLock.Release();
        }
    }

    public async Task<RouterTrafficSummary> GetTrafficSummaryAsync(double xbondDownloadMbps, double xbondUploadMbps, CancellationToken cancellationToken = default)
    {
        var clients = await GetClientsAsync(cancellationToken).ConfigureAwait(false);
        var localProcessTraffic = await localProcessTrafficService.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new RouterTrafficSummary
        {
            XBondDownloadMbps = xbondDownloadMbps,
            XBondUploadMbps = xbondUploadMbps,
            CudyClientDownloadMbps = clients.Sum(client => client.DownloadMbps),
            CudyClientUploadMbps = clients.Sum(client => client.UploadMbps),
            CudyClientCount = clients.Count,
            LocalProcessTraffic = localProcessTraffic
        };
    }

    public async Task SetInternetAccessAsync(XRouterClient client, bool allowed, CancellationToken cancellationToken = default)
    {
        await cudyClient.SetXRouterClientInternetAccessAsync(settings, client, allowed, cancellationToken).ConfigureAwait(false);
        InvalidateClientCache();
    }

    public async Task SetRateLimitAsync(XRouterClient client, bool enabled, int? downloadMbps, int? uploadMbps, CancellationToken cancellationToken = default)
    {
        await cudyClient.SetXRouterClientRateLimitAsync(settings, client, enabled, downloadMbps, uploadMbps, cancellationToken).ConfigureAwait(false);
        InvalidateClientCache();
    }

    private void InvalidateClientCache()
    {
        _cachedClients = null;
        Volatile.Write(ref _cachedClientsAtTicks, 0);
    }
}
