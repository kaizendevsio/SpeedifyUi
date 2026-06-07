using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkTelemetryHistoryTests
{
    [Fact]
    public void Add_KeepsOnlyRecentSamplesWithinLimit()
    {
        var history = new StarlinkTelemetryHistory();
        var now = DateTimeOffset.UtcNow;

        history.Add(Sample(now.AddMinutes(-10), 10), TimeSpan.FromMinutes(5), maxSamples: 3, now);
        history.Add(Sample(now.AddMinutes(-2), 20), TimeSpan.FromMinutes(5), maxSamples: 3, now);
        history.Add(Sample(now.AddMinutes(-1), 30), TimeSpan.FromMinutes(5), maxSamples: 3, now);
        history.Add(Sample(now, 40), TimeSpan.FromMinutes(5), maxSamples: 3, now);

        var samples = history.GetSamples();

        Assert.Equal(3, samples.Count);
        Assert.DoesNotContain(samples, sample => sample.PopPingLatencyMs == 10);
        Assert.Equal([20, 30, 40], samples.Select(sample => (int)sample.PopPingLatencyMs!.Value).ToArray());
    }

    [Fact]
    public void Add_IgnoresSnapshotsWithoutTimestamp()
    {
        var history = new StarlinkTelemetryHistory();

        history.Add(StarlinkTelemetrySnapshot.Unavailable("offline"), TimeSpan.FromMinutes(5), maxSamples: 3, DateTimeOffset.UtcNow);

        Assert.Empty(history.GetSamples());
    }

    private static StarlinkTelemetrySnapshot Sample(DateTimeOffset updatedUtc, double latencyMs)
    {
        return new StarlinkTelemetrySnapshot
        {
            IsAvailable = true,
            LastUpdatedUtc = updatedUtc,
            PopPingLatencyMs = latencyMs
        };
    }
}
