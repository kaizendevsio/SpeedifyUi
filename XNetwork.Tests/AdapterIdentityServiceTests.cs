using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterIdentityServiceTests
{
    private const string IpWhoIsBody = """
    {"ip":"103.235.93.167","success":true,"city":"Cebu City","country":"Philippines",
     "connection":{"asn":10139,"org":"Smart Communications","isp":"Smart Communications Inc."}}
    """;

    private static AdapterIdentitySettings Settings() => new()
    {
        Enabled = true,
        Endpoints = ["https://ipwho.is/", "http://ip-api.com/json/"],
        SuccessTtlMinutes = 15,
        FailureBackoffSeconds = 120
    };

    [Fact]
    public async Task ResolvesAndCachesIdentityPerInterface()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        var first = await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        var second = await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("Smart Communications", first!.DisplayName);
        Assert.Equal("Smart Communications", second!.DisplayName);
        Assert.Equal("https://ipwho.is/", first.Source);
        Assert.Equal("Smart Communications", service.GetDisplayNames()["enx0"]);
    }

    [Fact]
    public async Task RefetchesAfterSuccessTtlExpires()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(16));
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RefetchesWhenGatewayChanges()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        await service.RefreshAsync("enx0", "192.168.9.1", CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task FallsBackToSecondEndpointWhenFirstFails()
    {
        var endpoints = new List<string>();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, endpoint, _) =>
        {
            endpoints.Add(endpoint);
            return Task.FromResult<string?>(endpoint.Contains("ipwho.is")
                ? null
                : """{"status":"success","query":"1.2.3.4","isp":"Dito Telecommunity Corp."}""");
        });

        var identity = await service.RefreshAsync("enx1", "192.168.4.1", CancellationToken.None);

        Assert.Equal(["https://ipwho.is/", "http://ip-api.com/json/"], endpoints);
        Assert.Equal("Dito Telecommunity", identity!.DisplayName);
        Assert.Equal("http://ip-api.com/json/", identity.Source);
    }

    [Fact]
    public async Task CachesFailureForBackoffWindow()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(null);
        });

        var failed = await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.NotNull(failed!.Error);
        Assert.False(failed.IsAvailable);
        Assert.Empty(service.GetDisplayNames());

        time.Advance(TimeSpan.FromSeconds(121));
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task SkipsTunnelAndLoopbackInterfaces()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        Assert.Null(await service.RefreshAsync("xbond0", "10.250.0.1", CancellationToken.None));
        Assert.Null(await service.RefreshAsync("tailscale0", "100.64.0.1", CancellationToken.None));
        Assert.Null(await service.RefreshAsync("lo", null, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PerformsNoLookupWhenDisabled()
    {
        var calls = 0;
        var settings = Settings();
        settings.Enabled = false;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(settings, time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        Assert.Null(await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None));
        Assert.Equal(0, calls);
        Assert.Empty(service.GetDisplayNames());
    }

    [Fact]
    public async Task ClearingCacheDropsDisplayNames()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) => Task.FromResult<string?>(IpWhoIsBody));

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        Assert.Single(service.GetDisplayNames());

        service.ClearCache();
        Assert.Empty(service.GetDisplayNames());
    }

    [Fact]
    public async Task InvalidateForcesFreshLookup()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) =>
        {
            calls++;
            return Task.FromResult<string?>(IpWhoIsBody);
        });

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        service.Invalidate("enx0");
        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EvictsInterfacesThatDisappeared()
    {
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-13T00:00:00Z"));
        var service = CreateService(Settings(), time, (_, _, _) => Task.FromResult<string?>(IpWhoIsBody));

        await service.RefreshAsync("enx0", "192.168.3.1", CancellationToken.None);
        await service.RefreshAsync("enx1", "192.168.4.1", CancellationToken.None);

        service.EvictMissing(["enx1"]);

        Assert.Null(service.Get("enx0"));
        Assert.NotNull(service.Get("enx1"));
    }

    private static AdapterIdentityService CreateService(
        AdapterIdentitySettings settings,
        TimeProvider timeProvider,
        Func<string, string, CancellationToken, Task<string?>> fetcher) =>
        new(
            NullLogger<AdapterIdentityService>.Instance,
            settings,
            new InterfaceMetadataService(NullLogger<InterfaceMetadataService>.Instance),
            timeProvider,
            fetcher);

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
