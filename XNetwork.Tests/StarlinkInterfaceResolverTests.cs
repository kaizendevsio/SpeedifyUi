using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkInterfaceResolverTests
{
    [Fact]
    public async Task ResolveAsync_PrefersLiveXBondPathMatch()
    {
        var resolver = CreateResolver(
            new XBondStatsSnapshot
            {
                Paths =
                [
                    new XBondPathStatsSnapshot
                    {
                        PathId = 1,
                        Name = "Starlink",
                        InterfaceName = "enx-dynamic-starlink",
                        InterfaceUp = true,
                        IsConfigured = true
                    }
                ]
            });

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal("enx-dynamic-starlink", resolution.InterfaceName);
        Assert.Contains("uLink path", resolution.Reason);
    }

    [Fact]
    public async Task ResolveAsync_UsesBoundProbeFallbackWhenMetadataDoesNotMatch()
    {
        var resolver = CreateResolver(
            new XBondStatsSnapshot(),
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "wlan0",
                    Type: "wifi",
                    State: "connected",
                    ConnectionName: "Wifi",
                    DisplayName: "Wifi"),
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-probed",
                    Type: "ethernet",
                    State: "connected",
                    ConnectionName: "USB WAN",
                    DisplayName: "USB WAN")
            ],
            probe: (iface, _) => Task.FromResult(iface.Equals("enx-probed", StringComparison.OrdinalIgnoreCase)));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal("enx-probed", resolution.InterfaceName);
        Assert.Contains("answered", resolution.Reason);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsExplicitUnavailableWhenNoInterfaceMatches()
    {
        var resolver = CreateResolver(
            new XBondStatsSnapshot(),
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "wlan0",
                    Type: "wifi",
                    State: "connected",
                    ConnectionName: "Wifi",
                    DisplayName: "Wifi")
            ],
            probe: (_, _) => Task.FromResult(false));

        var resolution = await resolver.ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Null(resolution.InterfaceName);
        Assert.Contains("failed", resolution.Reason);
    }

    [Fact]
    public async Task BoundFactory_FailsClosedWhenResolverHasNoInterface()
    {
        var factory = new StarlinkBoundHttpClientFactory(
            new StarlinkTelemetrySettings(),
            new StaticResolver(StarlinkInterfaceResolution.Unavailable("no Starlink path")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.CreateAsync());

        Assert.Contains("Starlink interface unavailable", exception.Message);
        Assert.Contains("no Starlink path", exception.Message);
    }

    [Fact]
    public async Task BoundFactory_ReturnsLeaseForResolvedInterface()
    {
        var factory = new StarlinkBoundHttpClientFactory(
            new StarlinkTelemetrySettings(),
            new StaticResolver(StarlinkInterfaceResolution.Available("enx-starlink", "matched")));

        using var lease = await factory.CreateAsync();

        Assert.Equal("enx-starlink", lease.InterfaceName);
        Assert.Equal("matched", lease.Reason);
    }

    private static StarlinkInterfaceResolver CreateResolver(
        XBondStatsSnapshot snapshot,
        IReadOnlyList<InterfaceMetadataService.InterfaceMetadata>? interfaces = null,
        Func<string, CancellationToken, Task<bool>>? probe = null)
    {
        var cache = new XBondSnapshotCache(new StaticStatsProvider(snapshot));
        var metadataService = new InterfaceMetadataService(NullLogger<InterfaceMetadataService>.Instance);
        return new StarlinkInterfaceResolver(
            new StarlinkTelemetrySettings(),
            cache,
            metadataService,
            NullLogger<StarlinkInterfaceResolver>.Instance,
            _ => Task.FromResult(interfaces ?? []),
            probe);
    }

    private sealed class StaticStatsProvider(XBondStatsSnapshot snapshot) : IXBondStatsProvider
    {
        public Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(snapshot);
        }
    }

    private sealed class StaticResolver(StarlinkInterfaceResolution resolution) : IStarlinkInterfaceResolver
    {
        public Task<StarlinkInterfaceResolution> ResolveAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(resolution);
        }
    }
}
