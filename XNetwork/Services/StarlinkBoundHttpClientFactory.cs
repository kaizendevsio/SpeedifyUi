using System.Net.Http;
using XNetwork.Models;

namespace XNetwork.Services;

public sealed class StarlinkHttpClientLease : IDisposable
{
    public StarlinkHttpClientLease(HttpClient client, string interfaceName, string reason)
    {
        Client = client;
        InterfaceName = interfaceName;
        Reason = reason;
    }

    public HttpClient Client { get; }

    public string InterfaceName { get; }

    public string Reason { get; }

    public void Dispose()
    {
        Client.Dispose();
    }
}

public interface IStarlinkHttpClientFactory
{
    Task<StarlinkHttpClientLease> CreateAsync(CancellationToken cancellationToken = default);
}

public sealed class StarlinkBoundHttpClientFactory(
    StarlinkTelemetrySettings settings,
    IStarlinkInterfaceResolver interfaceResolver) : IStarlinkHttpClientFactory
{
    public async Task<StarlinkHttpClientLease> CreateAsync(CancellationToken cancellationToken = default)
    {
        var resolution = await interfaceResolver.ResolveAsync(cancellationToken).ConfigureAwait(false);
        if (!resolution.IsAvailable || string.IsNullOrWhiteSpace(resolution.InterfaceName))
        {
            throw new InvalidOperationException($"Starlink interface unavailable: {resolution.Reason}");
        }

        return new StarlinkHttpClientLease(
            CreateHttpClient(settings, resolution.InterfaceName),
            resolution.InterfaceName,
            resolution.Reason);
    }

    public static HttpClient CreateHttpClient(StarlinkTelemetrySettings settings, string interfaceName)
    {
        return InterfaceBoundHttpClientFactory.CreateClient(interfaceName, settings.RequestTimeout);
    }

    public static SocketsHttpHandler CreateHandler(StarlinkTelemetrySettings settings, string interfaceName)
    {
        return InterfaceBoundHttpClientFactory.CreateHandler(interfaceName, settings.RequestTimeout);
    }
}
