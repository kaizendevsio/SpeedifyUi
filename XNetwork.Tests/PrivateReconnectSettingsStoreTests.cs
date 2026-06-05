using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class PrivateReconnectSettingsStoreTests
{
    [Fact]
    public async Task SaveAndLoad_PreservesHealthTriggerSettings()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"private-reconnect-{Guid.NewGuid():N}.json");
        try
        {
            var store = new PrivateReconnectSettingsStore(NullLogger<PrivateReconnectSettingsStore>.Instance, filePath);
            var settings = new PrivateReconnectSettings
            {
                Enabled = true,
                IntervalMinutes = 45,
                DelaySeconds = 3,
                HealthTriggerEnabled = true,
                HealthLatencyThresholdMs = 325,
                HealthDegradedSeconds = 150,
                HealthRecoveryObserveSeconds = 70,
                HealthCooldownMinutes = 20
            };

            await store.SaveAsync(settings);

            var loaded = new PrivateReconnectSettings();
            store.Load(loaded);

            Assert.True(loaded.Enabled);
            Assert.Equal(45, loaded.IntervalMinutes);
            Assert.Equal(3, loaded.DelaySeconds);
            Assert.True(loaded.HealthTriggerEnabled);
            Assert.Equal(325, loaded.HealthLatencyThresholdMs);
            Assert.Equal(150, loaded.HealthDegradedSeconds);
            Assert.Equal(70, loaded.HealthRecoveryObserveSeconds);
            Assert.Equal(20, loaded.HealthCooldownMinutes);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }
}
