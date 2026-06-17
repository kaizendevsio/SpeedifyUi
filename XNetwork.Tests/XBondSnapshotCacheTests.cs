using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondSnapshotCacheTests
{
    [Fact]
    public async Task GetSnapshotAsync_ReusesSnapshotWithinCacheWindow()
    {
        var provider = new CountingStatsProvider();
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var cache = new XBondSnapshotCache(provider, time, TimeSpan.FromMilliseconds(500));

        var first = await cache.GetSnapshotAsync();
        var second = await cache.GetSnapshotAsync();

        Assert.Same(first, second);
        Assert.Equal(1, provider.Calls);

        time.Advance(TimeSpan.FromMilliseconds(501));
        var third = await cache.GetSnapshotAsync();

        Assert.NotSame(first, third);
        Assert.Equal(2, provider.Calls);
    }

    private sealed class CountingStatsProvider : IXBondStatsProvider
    {
        public int Calls { get; private set; }

        public Task<XBondStatsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new XBondStatsSnapshot
            {
                RawStatus = new XBondStatus
                {
                    Message = $"snapshot {Calls}"
                }
            });
        }
    }

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
