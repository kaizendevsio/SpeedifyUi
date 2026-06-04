using XNetwork.Utils;

namespace XNetwork.Tests;

public class SpeedifyStatsUnitsTests
{
    [Fact]
    public void RateFieldToMbpsTreatsSpeedifyStatsRateAsBitsPerSecond()
    {
        Assert.Equal(9.85, SpeedifyStatsUnits.RateFieldToMbps(9_850_000), precision: 2);
    }
}
