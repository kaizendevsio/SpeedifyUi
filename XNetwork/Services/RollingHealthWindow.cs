using XNetwork.Models;

namespace XNetwork.Services;

public sealed class RollingHealthWindow
{
    private readonly Queue<HealthWindowSample> _samples = new();

    public HealthWindowSnapshot Observe(
        ConnectionStatus status,
        double latencyMs,
        double jitterMs,
        double successRate,
        AutoServerSwitchSettings settings,
        DateTime utcNow)
    {
        var isBad = IsBadSample(status, latencyMs, jitterMs, successRate, settings);
        _samples.Enqueue(new HealthWindowSample(utcNow, isBad));
        return TrimAndBuildSnapshot(settings, utcNow);
    }

    public HealthWindowSnapshot GetSnapshot(AutoServerSwitchSettings settings, DateTime utcNow)
    {
        return TrimAndBuildSnapshot(settings, utcNow);
    }

    public void Clear()
    {
        _samples.Clear();
    }

    private HealthWindowSnapshot TrimAndBuildSnapshot(AutoServerSwitchSettings settings, DateTime utcNow)
    {
        var window = TimeSpan.FromSeconds(Math.Max(15, settings.HealthWindowSeconds));
        var cutoff = utcNow - window;
        while (_samples.Count > 0 && _samples.Peek().TimestampUtc < cutoff)
        {
            _samples.Dequeue();
        }

        var totalSamples = _samples.Count;
        var badSamples = _samples.Count(sample => sample.IsBad);
        var ratio = totalSamples == 0 ? 0 : badSamples / (double)totalSamples;
        var minimumBadSamples = Math.Max(1, settings.MinimumBadSamplesInWindow);
        var minimumBadRatio = Math.Clamp(settings.MinimumBadSampleRatio, 0, 1);
        var isTriggered = totalSamples > 0 && badSamples >= minimumBadSamples && ratio >= minimumBadRatio;
        var firstBadSampleUtc = _samples.FirstOrDefault(sample => sample.IsBad)?.TimestampUtc;
        var message = $"{badSamples}/{totalSamples} bad samples in {window.TotalSeconds:N0}s ({ratio:P0}); trigger requires {minimumBadSamples} and {minimumBadRatio:P0}";

        return new HealthWindowSnapshot(
            window,
            totalSamples,
            badSamples,
            ratio,
            isTriggered,
            firstBadSampleUtc,
            message);
    }

    private static bool IsBadSample(
        ConnectionStatus status,
        double latencyMs,
        double jitterMs,
        double successRate,
        AutoServerSwitchSettings settings)
    {
        return status is ConnectionStatus.Poor or ConnectionStatus.Critical ||
               latencyMs > settings.MaxLatencyMs ||
               jitterMs > settings.MaxJitterMs ||
               successRate < settings.MinSuccessRate;
    }

    private sealed record HealthWindowSample(DateTime TimestampUtc, bool IsBad);
}

public sealed record HealthWindowSnapshot(
    TimeSpan Window,
    int TotalSamples,
    int BadSamples,
    double BadSampleRatio,
    bool IsTriggered,
    DateTime? FirstBadSampleUtc,
    string Message);
