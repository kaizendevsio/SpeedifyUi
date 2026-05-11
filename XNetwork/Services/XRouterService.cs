using XNetwork.Models;

namespace XNetwork.Services;

public class XRouterService(CudyLuciClient cudyClient, CudyApAutomationSettings settings)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(settings.ManagementBaseUrl) &&
        (!string.IsNullOrWhiteSpace(settings.AdminPassword) ||
         !string.IsNullOrWhiteSpace(settings.AdminPasswordEnvironmentVariable));

    public Task<IReadOnlyList<XRouterClient>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        return cudyClient.GetXRouterClientsAsync(settings, cancellationToken);
    }

    public Task SetInternetAccessAsync(XRouterClient client, bool allowed, CancellationToken cancellationToken = default)
    {
        return cudyClient.SetXRouterClientInternetAccessAsync(settings, client, allowed, cancellationToken);
    }

    public Task SetRateLimitAsync(XRouterClient client, bool enabled, int? downloadMbps, int? uploadMbps, CancellationToken cancellationToken = default)
    {
        return cudyClient.SetXRouterClientRateLimitAsync(settings, client, enabled, downloadMbps, uploadMbps, cancellationToken);
    }
}
