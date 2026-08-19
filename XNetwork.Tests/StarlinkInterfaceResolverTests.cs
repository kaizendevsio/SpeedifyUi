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
            },
            probe: (iface, _) => Task.FromResult(iface == "enx-dynamic-starlink"));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal("enx-dynamic-starlink", resolution.InterfaceName);
        Assert.Contains("Verified", resolution.Reason);
    }

    [Fact]
    public async Task ResolveAsync_RejectsStaleXBondMetadataAndFindsVerifiedReplacement()
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
                        InterfaceName = "enx-old-starlink",
                        InterfaceUp = true,
                        IsConfigured = true
                    }
                ]
            },
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-old-starlink", Type: "ethernet", State: "connected",
                    ConnectionName: "Old", DisplayName: "Old"),
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-new-starlink", Type: "ethernet", State: "connected",
                    ConnectionName: "Starlink", DisplayName: "Starlink")
            ],
            probe: (iface, _) => Task.FromResult(iface == "enx-new-starlink"));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal("enx-new-starlink", resolution.InterfaceName);
        Assert.Contains("answered", resolution.Reason);
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
    public async Task ResolveAsync_TreatsBoundProbeTimeoutAsUnavailable()
    {
        var resolver = CreateResolver(
            new XBondStatsSnapshot(),
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-timeout",
                    Type: "ethernet",
                    State: "connected",
                    ConnectionName: "USB WAN",
                    DisplayName: "USB WAN")
            ],
            probe: (_, _) => Task.FromException<bool>(new TaskCanceledException("probe timeout")));

        var resolution = await resolver.ResolveAsync();

        Assert.False(resolution.IsAvailable);
        Assert.Contains("failed", resolution.Reason);
    }

    [Fact]
    public async Task ResolveAsync_KeepsMatchedPathWhenTheDishBrieflyStopsAnswering()
    {
        // An obstructed dish drops its management endpoint intermittently. uLink still reports
        // the path up and matching the Starlink hints, and no other adapter answers, so the
        // interface has not moved -- abandoning it here flapped the LAN access rules.
        var resolver = CreateResolver(
            StarlinkPathSnapshot("enx-starlink"),
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-starlink", Type: "ethernet", State: "connected",
                    ConnectionName: "Starlink", DisplayName: "Starlink"),
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-other", Type: "ethernet", State: "connected",
                    ConnectionName: "USB WAN", DisplayName: "USB WAN")
            ],
            probe: (_, _) => Task.FromResult(false));

        var resolution = await resolver.ResolveAsync();

        Assert.True(resolution.IsAvailable);
        Assert.Equal("enx-starlink", resolution.InterfaceName);
        Assert.Contains("did not answer", resolution.Reason);
    }

    [Fact]
    public async Task ResolveAsync_GivesUpOnTheMatchedPathOnceToleranceIsExhausted()
    {
        var settings = new StarlinkTelemetrySettings { AdapterVerifyFailureTolerance = 2 };
        var resolver = CreateResolver(
            StarlinkPathSnapshot("enx-starlink"),
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-starlink", Type: "ethernet", State: "connected",
                    ConnectionName: "Starlink", DisplayName: "Starlink")
            ],
            probe: (_, _) => Task.FromResult(false),
            settings: settings);

        Assert.True((await resolver.ResolveAsync()).IsAvailable);
        Assert.True((await resolver.ResolveAsync()).IsAvailable);

        // A dish that never comes back is a real fault and must surface as unavailable.
        var exhausted = await resolver.ResolveAsync();

        Assert.False(exhausted.IsAvailable);
        Assert.Null(exhausted.InterfaceName);
    }

    [Fact]
    public async Task ResolveAsync_ForgivesEarlierMissesOnceTheDishAnswersAgain()
    {
        var settings = new StarlinkTelemetrySettings { AdapterVerifyFailureTolerance = 2 };
        var reachable = false;
        var resolver = CreateResolver(
            StarlinkPathSnapshot("enx-starlink"),
            interfaces:
            [
                new InterfaceMetadataService.InterfaceMetadata(
                    Device: "enx-starlink", Type: "ethernet", State: "connected",
                    ConnectionName: "Starlink", DisplayName: "Starlink")
            ],
            // ReSharper disable once AccessToModifiedClosure
            probe: (_, _) => Task.FromResult(reachable),
            settings: settings);

        await resolver.ResolveAsync();
        await resolver.ResolveAsync();
        reachable = true;
        Assert.Contains("Verified", (await resolver.ResolveAsync()).Reason);
        reachable = false;

        // The streak resets, so a later obstruction is tolerated afresh rather than tripping
        // straight to unavailable.
        Assert.True((await resolver.ResolveAsync()).IsAvailable);
    }

    private static XBondStatsSnapshot StarlinkPathSnapshot(string interfaceName) =>
        new()
        {
            Paths =
            [
                new XBondPathStatsSnapshot
                {
                    PathId = 1,
                    Name = "Starlink",
                    InterfaceName = interfaceName,
                    InterfaceUp = true,
                    IsConfigured = true
                }
            ]
        };

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
        Func<string, CancellationToken, Task<bool>>? probe = null,
        StarlinkTelemetrySettings? settings = null)
    {
        var cache = new XBondSnapshotCache(new StaticStatsProvider(snapshot));
        var metadataService = new InterfaceMetadataService(NullLogger<InterfaceMetadataService>.Instance);
        return new StarlinkInterfaceResolver(
            settings ?? new StarlinkTelemetrySettings(),
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
