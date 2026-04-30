using XNetwork.Models;

namespace XNetwork.Services;

public class LocalWanStabilityEvaluator
{
    public LocalWanStabilityResult Evaluate(IEnumerable<Adapter> adapters, AutoServerSwitchSettings settings)
    {
        var adapterList = adapters.ToList();
        var connectedCount = adapterList.Count(IsConnected);
        var minimumConnected = Math.Max(0, settings.MinimumConnectedAdaptersBeforeSwitch);

        if (minimumConnected == 0)
        {
            return new LocalWanStabilityResult(true, connectedCount, minimumConnected, "Local WAN guard is disabled");
        }

        if (connectedCount < minimumConnected)
        {
            var disconnected = adapterList
                .Where(adapter => !IsConnected(adapter))
                .Select(adapter => $"{adapter.Name}:{adapter.State}")
                .Take(4);
            var detail = string.Join(", ", disconnected);
            var message = string.IsNullOrWhiteSpace(detail)
                ? $"Only {connectedCount} connected Speedify adapter(s); need {minimumConnected} before server switching"
                : $"Only {connectedCount} connected Speedify adapter(s); need {minimumConnected}. Unstable: {detail}";

            return new LocalWanStabilityResult(false, connectedCount, minimumConnected, message);
        }

        return new LocalWanStabilityResult(true, connectedCount, minimumConnected, $"{connectedCount} Speedify adapter(s) connected");
    }

    private static bool IsConnected(Adapter adapter)
    {
        return string.Equals(adapter.State, "connected", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record LocalWanStabilityResult(
    bool IsStable,
    int ConnectedAdapterCount,
    int MinimumConnectedAdapters,
    string Message);
