namespace XNetwork.Tests;

public class ProbeScoreCalculatorTests
{
    [Fact]
    public void Calculate_UsesLegacyWeightsWhenThroughputIsMissing()
    {
        var settings = new global::ProbeSettings
        {
            MaxLatencyMs = 250,
            MaxJitterMs = 80,
            MaxThroughputMbps = 100
        };

        var score = global::ProbeScoreCalculator.Calculate(
            latency: 50,
            jitter: 10,
            successRate: 100,
            throughputMbps: null,
            settings);

        Assert.Equal(93.1, score, precision: 1);
    }

    [Fact]
    public void Calculate_RewardsHigherThroughputWhenThroughputIsMeasured()
    {
        var settings = new global::ProbeSettings
        {
            MaxLatencyMs = 250,
            MaxJitterMs = 80,
            MaxThroughputMbps = 100
        };

        var lowThroughput = global::ProbeScoreCalculator.Calculate(50, 10, 100, 10, settings);
        var highThroughput = global::ProbeScoreCalculator.Calculate(50, 10, 100, 80, settings);

        Assert.True(highThroughput > lowThroughput);
    }

    [Fact]
    public void Calculate_ReducesThroughputPenaltyWhenConfidenceIsLow()
    {
        var settings = new global::ProbeSettings
        {
            MaxLatencyMs = 250,
            MaxJitterMs = 80,
            MaxThroughputMbps = 100
        };

        var lowConfidence = global::ProbeScoreCalculator.Calculate(50, 10, 100, 10, settings, throughputConfidence: 0.1);
        var highConfidence = global::ProbeScoreCalculator.Calculate(50, 10, 100, 10, settings, throughputConfidence: 1);

        Assert.True(lowConfidence > highConfidence);
    }
}
