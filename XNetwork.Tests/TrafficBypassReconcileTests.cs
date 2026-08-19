using XNetwork.Models;

namespace XNetwork.Tests;

public class TrafficBypassReconcileTests
{
    [Fact]
    public void ReconcileIntervalDefaultsToATightEnoughWindow()
    {
        // Between an adapter blip and the next reconcile, matched traffic silently leaves
        // through the tunnel, so this window is the exposure to wrong-country routing.
        Assert.Equal(TimeSpan.FromSeconds(20), new TrafficBypassSettings().ReconcileInterval);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(1)]
    public void AnAbsurdlySmallIntervalIsClampedAwayFromAHotLoop(int configured)
    {
        // Reconciling forks `ip` several times per pass; a zero interval would spin the
        // router's CPU permanently.
        var settings = new TrafficBypassSettings { ReconcileIntervalSeconds = configured };

        Assert.Equal(TimeSpan.FromSeconds(5), settings.ReconcileInterval);
    }

    [Fact]
    public void AnAbsurdlyLargeIntervalIsCapped()
    {
        var settings = new TrafficBypassSettings { ReconcileIntervalSeconds = 100_000 };

        Assert.Equal(TimeSpan.FromMinutes(15), settings.ReconcileInterval);
    }

    [Fact]
    public void AnOperatorChosenIntervalIsHonoured()
    {
        var settings = new TrafficBypassSettings { ReconcileIntervalSeconds = 45 };

        Assert.Equal(TimeSpan.FromSeconds(45), settings.ReconcileInterval);
    }
}
