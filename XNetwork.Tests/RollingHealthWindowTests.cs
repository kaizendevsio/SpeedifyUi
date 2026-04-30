using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class RollingHealthWindowTests
{
    [Fact]
    public void Observe_TriggersWhenEnoughBadSamplesOccurInsideWindow()
    {
        var window = new RollingHealthWindow();
        var settings = CreateSettings();
        var now = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

        window.Observe(ConnectionStatus.Good, 40, 2, 100, settings, now);
        window.Observe(ConnectionStatus.Critical, 500, 120, 50, settings, now.AddSeconds(15));
        window.Observe(ConnectionStatus.Good, 40, 2, 100, settings, now.AddSeconds(30));
        window.Observe(ConnectionStatus.Poor, 300, 90, 70, settings, now.AddSeconds(45));
        var snapshot = window.Observe(ConnectionStatus.Good, 40, 2, 80, settings, now.AddSeconds(60));

        Assert.True(snapshot.IsTriggered);
        Assert.Equal(3, snapshot.BadSamples);
        Assert.Equal(5, snapshot.TotalSamples);
    }

    [Fact]
    public void Observe_DoesNotTriggerWhenBadSamplesAreTooSparse()
    {
        var window = new RollingHealthWindow();
        var settings = CreateSettings();
        var now = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

        window.Observe(ConnectionStatus.Critical, 500, 120, 50, settings, now);
        window.Observe(ConnectionStatus.Good, 40, 2, 100, settings, now.AddSeconds(15));
        window.Observe(ConnectionStatus.Good, 40, 2, 100, settings, now.AddSeconds(30));
        var snapshot = window.Observe(ConnectionStatus.Good, 40, 2, 100, settings, now.AddSeconds(45));

        Assert.False(snapshot.IsTriggered);
        Assert.Equal(1, snapshot.BadSamples);
    }

    [Fact]
    public void Observe_ExpiresSamplesOutsideWindow()
    {
        var window = new RollingHealthWindow();
        var settings = CreateSettings();
        var now = new DateTime(2026, 4, 30, 12, 0, 0, DateTimeKind.Utc);

        window.Observe(ConnectionStatus.Critical, 500, 120, 50, settings, now);
        window.Observe(ConnectionStatus.Critical, 500, 120, 50, settings, now.AddSeconds(15));
        var snapshot = window.Observe(ConnectionStatus.Good, 40, 2, 100, settings, now.AddSeconds(140));

        Assert.False(snapshot.IsTriggered);
        Assert.Equal(1, snapshot.TotalSamples);
        Assert.Equal(0, snapshot.BadSamples);
    }

    private static AutoServerSwitchSettings CreateSettings()
    {
        return new AutoServerSwitchSettings
        {
            HealthWindowSeconds = 120,
            MinimumBadSamplesInWindow = 3,
            MinimumBadSampleRatio = 0.35,
            MaxLatencyMs = 250,
            MaxJitterMs = 80,
            MinSuccessRate = 85
        };
    }
}
