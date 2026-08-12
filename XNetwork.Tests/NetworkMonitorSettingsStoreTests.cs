using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class NetworkMonitorSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_PreservesNetworkMonitorSettings()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"network-monitor-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new NetworkMonitorSettingsStore(NullLogger<NetworkMonitorSettingsStore>.Instance, filePath);
            var settings = new NetworkMonitorSettings
            {
                Enabled = false,
                WhitelistedLinks = new List<string> { "enxc8a3627e629b", "wwan0" },
                AdapterAliases = new Dictionary<string, string> { ["wwan0"] = "Roof modem" },
                DownTimeoutSeconds = 45,
                MaxRestartAttemptsPerHour = 3,
                RestartCooldownMinutes = 20
            };

            await store.SaveAsync(settings);

            var loaded = new NetworkMonitorSettings();
            store.Load(loaded);

            Assert.False(loaded.Enabled);
            Assert.Equal(settings.WhitelistedLinks, loaded.WhitelistedLinks);
            Assert.Equal("Roof modem", loaded.AdapterAliases["wwan0"]);
            Assert.Equal(45, loaded.DownTimeoutSeconds);
            Assert.Equal(3, loaded.MaxRestartAttemptsPerHour);
            Assert.Equal(20, loaded.RestartCooldownMinutes);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Fact]
    public async Task RemoveAndReAddLink_PreservesDisplayAliasByInterfaceIdentity()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"network-monitor-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new NetworkMonitorSettingsStore(NullLogger<NetworkMonitorSettingsStore>.Instance, filePath);
            var settings = new NetworkMonitorSettings
            {
                WhitelistedLinks = ["wlan0"],
                AdapterAliases = new Dictionary<string, string> { ["wlan0"] = "Cabin Wi-Fi" }
            };

            settings.WhitelistedLinks.Clear();
            await store.SaveAsync(settings);
            settings.WhitelistedLinks.Add("wlan0");
            await store.SaveAsync(settings);

            var loaded = new NetworkMonitorSettings();
            store.Load(loaded);
            Assert.Equal(["wlan0"], loaded.WhitelistedLinks);
            Assert.Equal("Cabin Wi-Fi", loaded.AdapterAliases["wlan0"]);
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [Fact]
    public void Validate_RejectsUnsafeInterfaceAndInvalidAlias()
    {
        Assert.Throws<InvalidOperationException>(() => NetworkMonitorSettingsStore.Validate(new NetworkMonitorSettings
        {
            WhitelistedLinks = ["wlan0;reboot"]
        }));
        Assert.Throws<InvalidOperationException>(() => NetworkMonitorSettingsStore.Validate(new NetworkMonitorSettings
        {
            AdapterAliases = new Dictionary<string, string> { ["wlan0"] = new('x', 49) }
        }));
    }

    [Fact]
    public void ApplyAdapterAliases_ChangesDisplayNameWithoutChangingInterfaceIdentity()
    {
        var interfaces = new[]
        {
            new InterfaceMetadataService.InterfaceMetadata("wlan0", "wifi", "connected", "Wi-Fi", "Wi-Fi")
        };
        var settings = new NetworkMonitorSettings
        {
            AdapterAliases = new Dictionary<string, string> { ["wlan0"] = "Cabin Wi-Fi" }
        };

        var renamed = Assert.Single(XBondStatsService.ApplyAdapterAliases(interfaces, settings));
        Assert.Equal("wlan0", renamed.Device);
        Assert.Equal("Cabin Wi-Fi", renamed.DisplayName);
    }

    [Fact]
    public void UpdateSettings_ReplacesRuntimeSettingsAndNormalizesInput()
    {
        var service = new NetworkMonitorService(
            NullLogger<NetworkMonitorService>.Instance,
            new NetworkMonitorSettings
            {
                Enabled = false,
                WhitelistedLinks = new List<string> { "old0" },
                DownTimeoutSeconds = 30
            },
            new TestHostApplicationLifetime());
        var updatedSettings = new NetworkMonitorSettings
        {
            Enabled = true,
            WhitelistedLinks = new List<string> { "new0", " new0 ", "" },
            DownTimeoutSeconds = 4,
            MaxRestartAttemptsPerHour = -1,
            RestartCooldownMinutes = 0
        };

        service.UpdateSettings(updatedSettings);
        updatedSettings.WhitelistedLinks.Add("mutated-after-update");

        var status = service.GetStatus();
        var link = Assert.Single(status.Links);
        Assert.True(status.IsEnabled);
        Assert.Equal(5, status.DownTimeoutSeconds);
        Assert.Equal("new0", link.Name);
    }

    [Fact]
    public async Task SaveAsync_SerializesConcurrentWrites()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"network-monitor-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new NetworkMonitorSettingsStore(NullLogger<NetworkMonitorSettingsStore>.Instance, filePath);
            var saves = Enumerable.Range(0, 20)
                .Select(index => store.SaveAsync(new NetworkMonitorSettings
                {
                    Enabled = index % 2 == 0,
                    WhitelistedLinks = new List<string> { $"link-{index}" },
                    DownTimeoutSeconds = 30 + index,
                    MaxRestartAttemptsPerHour = index,
                    RestartCooldownMinutes = 10 + index
                }));

            await Task.WhenAll(saves);

            var loaded = new NetworkMonitorSettings();
            store.Load(loaded);

            var link = Assert.Single(loaded.WhitelistedLinks);
            Assert.StartsWith("link-", link);
            Assert.InRange(loaded.DownTimeoutSeconds, 30, 49);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }
}
