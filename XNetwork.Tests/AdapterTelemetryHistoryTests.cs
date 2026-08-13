using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class AdapterTelemetryHistoryTests
{
    [Fact]
    public void KeepsSamplesPerInterface()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 10);

        history.Add("enx0", Sample(1, 30));
        history.Add("enx1", Sample(2, 40));
        history.Add("enx0", Sample(3, 50));

        Assert.Equal(2, history.GetSamples("enx0").Count);
        Assert.Single(history.GetSamples("enx1"));
        Assert.Equal(50, history.GetSamples("enx0")[^1].RttMs);
    }

    [Fact]
    public void PrunesOldestSamplesBeyondCapacity()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 3);

        for (var i = 1; i <= 5; i++)
        {
            history.Add("enx0", Sample(i, i * 10));
        }

        var samples = history.GetSamples("enx0");
        Assert.Equal(3, samples.Count);
        Assert.Equal(30, samples[0].RttMs);
        Assert.Equal(50, samples[^1].RttMs);
    }

    [Fact]
    public void ReturnsEmptyForUnknownInterface()
    {
        Assert.Empty(new AdapterTelemetryHistory(maxSamples: 5).GetSamples("nope"));
    }

    [Fact]
    public void IgnoresBlankInterfaceNames()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 5);

        history.Add("  ", Sample(1, 10));

        Assert.Empty(history.GetSamples("  "));
    }

    [Fact]
    public void EvictsInterfacesThatDisappeared()
    {
        var history = new AdapterTelemetryHistory(maxSamples: 5);
        history.Add("enx0", Sample(1, 10));
        history.Add("enx1", Sample(1, 10));

        history.EvictMissing(["enx1"]);

        Assert.Empty(history.GetSamples("enx0"));
        Assert.Single(history.GetSamples("enx1"));
    }

    private static AdapterTelemetrySample Sample(int second, double rttMs) => new()
    {
        TimestampUtc = new DateTimeOffset(2026, 8, 13, 0, 0, second, TimeSpan.Zero),
        RttMs = rttMs,
        LossPercent = 0,
        JitterMs = 1,
        DownloadMbps = 10,
        UploadMbps = 5
    };
}
