using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Tests;

public class AdapterHealthPresenterTests
{
    [Fact]
    public void Classify_TreatsZeroLatencyAsUnknown()
    {
        var stats = new ConnectionItem
        {
            LatencyMs = 0,
            LossReceive = 0,
            LossSend = 0
        };

        var level = AdapterHealthPresenter.Classify(null, stats);
        var sortKey = AdapterHealthPresenter.GetSortKey(null, stats);

        Assert.Equal(AdapterHealthLevel.Unknown, level);
        Assert.Equal((int)AdapterHealthLevel.Unknown, sortKey.Rank);
    }

    [Fact]
    public void SortKey_PrefersHealthyRollingMetricsOverPoorMetrics()
    {
        var good = Metrics(latency: 52, packetLoss: 0.2, jitter: 6, stability: 0.95);
        var poor = Metrics(latency: 240, packetLoss: 6, jitter: 55, stability: 0.4);

        var goodKey = AdapterHealthPresenter.GetSortKey(good);
        var poorKey = AdapterHealthPresenter.GetSortKey(poor);

        Assert.True(goodKey.Rank < poorKey.Rank);
    }

    [Fact]
    public void SignalStrength_UsesRollingHealthWhenAvailable()
    {
        var health = Metrics(latency: 45, packetLoss: 0.1, jitter: 5, stability: 0.97);
        var staleInstantStats = new ConnectionItem
        {
            LatencyMs = 280,
            JitterMs = 90,
            LossReceive = 0.1,
            LossSend = 0.1
        };

        var strength = AdapterHealthPresenter.GetSignalStrength(health, staleInstantStats);

        Assert.Equal(4, strength);
    }

    private static HealthMetrics Metrics(double latency, double packetLoss, double jitter, double stability)
    {
        return new HealthMetrics(
            averageLatency: latency,
            averagePacketLoss: packetLoss,
            averageSpeed: 0,
            minLatency: latency - 2,
            maxLatency: latency + 2,
            latencyStdDev: jitter,
            stabilityScore: stability,
            sampleCount: 5,
            status: ConnectionStatus.Unknown,
            jitter: jitter);
    }
}
