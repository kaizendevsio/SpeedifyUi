using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class WifiControlSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"ulink-wifi-{Guid.NewGuid():N}");

    [Fact]
    public async Task RoundTripsDisabledInterfaces()
    {
        var path = Path.Combine(_directory, "wifi-control-settings.json");
        var store = new WifiControlSettingsStore(NullLogger<WifiControlSettingsStore>.Instance, path);
        var saved = new WifiControlSettings();
        saved.DisabledInterfaces["wlan0"] = true;
        saved.DisabledInterfaces["wlan1"] = false;

        await store.SaveAsync(saved);

        var loaded = new WifiControlSettings();
        store.Load(loaded);

        Assert.True(loaded.IsDisabled("wlan0"));
        Assert.True(loaded.IsDisabled("WLAN0"));
        Assert.False(loaded.IsDisabled("wlan1"));
        Assert.False(loaded.IsDisabled("wlan9"));
    }

    [Fact]
    public async Task PreservesEnforcementInterval()
    {
        var path = Path.Combine(_directory, "wifi-control-settings.json");
        var store = new WifiControlSettingsStore(NullLogger<WifiControlSettingsStore>.Instance, path);

        await store.SaveAsync(new WifiControlSettings { EnforcementIntervalSeconds = 45 });

        var loaded = new WifiControlSettings();
        store.Load(loaded);

        Assert.Equal(45, loaded.EnforcementIntervalSeconds);
        Assert.Equal(TimeSpan.FromSeconds(45), loaded.EnforcementInterval);
    }

    [Fact]
    public void LoadWithoutFileLeavesSettingsUntouched()
    {
        var store = new WifiControlSettingsStore(
            NullLogger<WifiControlSettingsStore>.Instance,
            Path.Combine(_directory, "missing.json"));
        var settings = new WifiControlSettings();

        store.Load(settings);

        Assert.Empty(settings.DisabledInterfaces);
        Assert.Equal(30, settings.EnforcementIntervalSeconds);
    }

    [Fact]
    public void EnforcementIntervalIsClamped()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), new WifiControlSettings { EnforcementIntervalSeconds = 1 }.EnforcementInterval);
        Assert.Equal(TimeSpan.FromSeconds(3600), new WifiControlSettings { EnforcementIntervalSeconds = 99_999 }.EnforcementInterval);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
