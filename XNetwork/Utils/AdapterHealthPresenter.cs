using XNetwork.Models;

namespace XNetwork.Utils;

public enum AdapterHealthLevel
{
    Excellent = 0,
    Good = 1,
    Fair = 2,
    Poor = 3,
    Critical = 4,
    Unknown = 5
}

public readonly record struct AdapterHealthSortKey(
    int Rank,
    double PacketLossPercent,
    double JitterMs,
    double LatencyMs,
    double StabilityPenalty);

public static class AdapterHealthPresenter
{
    private const int MinimumRollingSamples = 3;

    public static AdapterHealthLevel Classify(HealthMetrics? rollingHealth, ConnectionItem? currentStats = null)
    {
        var values = GetHealthValues(rollingHealth, currentStats);
        if (values is null)
        {
            return AdapterHealthLevel.Unknown;
        }

        var v = values.Value;

        if (v.PacketLossPercent >= 10 || v.LatencyMs >= 300 || v.JitterMs >= 80)
        {
            return AdapterHealthLevel.Critical;
        }

        if (v.PacketLossPercent >= 5 || v.LatencyMs >= 200 || v.JitterMs >= 50)
        {
            return AdapterHealthLevel.Poor;
        }

        if (v.PacketLossPercent >= 3 || v.LatencyMs >= 100 || v.JitterMs >= 25)
        {
            return AdapterHealthLevel.Fair;
        }

        if (v.PacketLossPercent >= 1 || v.LatencyMs >= 60 || v.JitterMs >= 15)
        {
            return AdapterHealthLevel.Good;
        }

        return AdapterHealthLevel.Excellent;
    }

    public static AdapterHealthSortKey GetSortKey(HealthMetrics? rollingHealth, ConnectionItem? currentStats = null)
    {
        var values = GetHealthValues(rollingHealth, currentStats);
        if (values is null)
        {
            return new AdapterHealthSortKey(
                (int)AdapterHealthLevel.Unknown,
                double.MaxValue,
                double.MaxValue,
                double.MaxValue,
                double.MaxValue);
        }

        var level = Classify(rollingHealth, currentStats);
        return new AdapterHealthSortKey(
            (int)level,
            values.Value.PacketLossPercent,
            values.Value.JitterMs,
            values.Value.LatencyMs,
            1 - values.Value.StabilityScore);
    }

    public static int GetSignalStrength(HealthMetrics? rollingHealth, ConnectionItem? currentStats = null)
    {
        return Classify(rollingHealth, currentStats) switch
        {
            AdapterHealthLevel.Excellent => 4,
            AdapterHealthLevel.Good => 3,
            AdapterHealthLevel.Fair => 2,
            AdapterHealthLevel.Poor => 1,
            _ => 0
        };
    }

    public static string GetBackgroundClass(HealthMetrics? rollingHealth, ConnectionItem? currentStats = null)
    {
        return Classify(rollingHealth, currentStats) switch
        {
            AdapterHealthLevel.Excellent => "bg-green-900/20 border-green-900/30",
            AdapterHealthLevel.Fair => "bg-yellow-900/20 border-yellow-900/30",
            AdapterHealthLevel.Poor or AdapterHealthLevel.Critical => "bg-red-900/20 border-red-900/30",
            _ => "bg-slate-800/50"
        };
    }

    private static AdapterHealthValues? GetHealthValues(HealthMetrics? rollingHealth, ConnectionItem? currentStats)
    {
        if (rollingHealth is { SampleCount: >= MinimumRollingSamples } &&
            rollingHealth.AverageLatency > 0 &&
            !double.IsNaN(rollingHealth.AverageLatency))
        {
            return new AdapterHealthValues(
                rollingHealth.AverageLatency,
                Math.Max(0, rollingHealth.AveragePacketLoss),
                Math.Max(0, rollingHealth.Jitter),
                Math.Clamp(rollingHealth.StabilityScore, 0, 1));
        }

        if (currentStats is not null &&
            currentStats.LatencyMs > 0 &&
            !double.IsNaN(currentStats.LatencyMs))
        {
            return new AdapterHealthValues(
                currentStats.LatencyMs,
                Math.Max(0, currentStats.AverageLossPercent),
                Math.Max(0, currentStats.JitterMs),
                1);
        }

        return null;
    }

    private readonly record struct AdapterHealthValues(
        double LatencyMs,
        double PacketLossPercent,
        double JitterMs,
        double StabilityScore);
}
