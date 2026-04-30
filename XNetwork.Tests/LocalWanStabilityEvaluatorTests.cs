using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class LocalWanStabilityEvaluatorTests
{
    private readonly LocalWanStabilityEvaluator _evaluator = new();

    [Fact]
    public void Evaluate_ReturnsStableWhenEnoughAdaptersAreConnected()
    {
        var settings = new AutoServerSwitchSettings { MinimumConnectedAdaptersBeforeSwitch = 2 };
        var adapters = new[]
        {
            Adapter("wan1", "connected"),
            Adapter("wan2", "connected"),
            Adapter("wan3", "disconnected")
        };

        var result = _evaluator.Evaluate(adapters, settings);

        Assert.True(result.IsStable);
        Assert.Equal(2, result.ConnectedAdapterCount);
    }

    [Fact]
    public void Evaluate_ReturnsUnstableWhenConnectedAdaptersAreBelowMinimum()
    {
        var settings = new AutoServerSwitchSettings { MinimumConnectedAdaptersBeforeSwitch = 2 };
        var adapters = new[]
        {
            Adapter("wan1", "connected"),
            Adapter("wan2", "disconnected")
        };

        var result = _evaluator.Evaluate(adapters, settings);

        Assert.False(result.IsStable);
        Assert.Equal(1, result.ConnectedAdapterCount);
        Assert.Contains("need 2", result.Message);
    }

    [Fact]
    public void Evaluate_CanBeDisabledWithZeroMinimumAdapters()
    {
        var settings = new AutoServerSwitchSettings { MinimumConnectedAdaptersBeforeSwitch = 0 };

        var result = _evaluator.Evaluate(Array.Empty<Adapter>(), settings);

        Assert.True(result.IsStable);
        Assert.Equal(0, result.MinimumConnectedAdapters);
    }

    private static Adapter Adapter(string name, string state)
    {
        return new Adapter(name, name, "", state, "automatic", "secondary", "Ethernet");
    }
}
