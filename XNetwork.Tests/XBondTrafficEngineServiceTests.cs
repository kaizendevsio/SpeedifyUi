using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondTrafficEngineServiceTests
{
    [Fact]
    public async Task SetModeAsync_XBondPrimaryWhenLocked_ReturnsErrorAndKeepsSpeedifyPrimary()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"xbond-settings-{Guid.NewGuid():N}.json");
        var settings = new XBondSettings
        {
            TrafficEngineMode = XBondTrafficEngineModes.SpeedifyPrimary,
            AllowPrimaryMode = false
        };
        var store = new XBondSettingsStore(NullLogger<XBondSettingsStore>.Instance, filePath);
        var service = new XBondTrafficEngineService(
            NullLogger<XBondTrafficEngineService>.Instance,
            settings,
            store);

        var status = await service.SetModeAsync(XBondTrafficEngineModes.XBondPrimary);

        Assert.True(status.HasError);
        Assert.Equal(XBondTrafficEngineModes.SpeedifyPrimary, settings.TrafficEngineMode);
        Assert.False(settings.Enabled);
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task SetModeAsync_XBondCanary_PersistsEnabledCanaryMode()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"xbond-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new XBondSettings
            {
                TrafficEngineMode = XBondTrafficEngineModes.SpeedifyPrimary
            };
            var store = new XBondSettingsStore(NullLogger<XBondSettingsStore>.Instance, filePath);
            var service = new XBondTrafficEngineService(
                NullLogger<XBondTrafficEngineService>.Instance,
                settings,
                store);

            var status = await service.SetModeAsync(XBondTrafficEngineModes.XBondCanary);

            Assert.Equal(XBondTrafficEngineModes.XBondCanary, settings.TrafficEngineMode);
            Assert.True(settings.Enabled);
            Assert.Equal(XBondTrafficEngineModes.XBondCanary, status.Mode);
            Assert.True(File.Exists(filePath));

            var loaded = new XBondSettings();
            store.Load(loaded);

            Assert.Equal(XBondTrafficEngineModes.XBondCanary, loaded.TrafficEngineMode);
            Assert.True(loaded.Enabled);
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [Theory]
    [InlineData(null, XBondTrafficEngineModes.SpeedifyPrimary)]
    [InlineData("", XBondTrafficEngineModes.SpeedifyPrimary)]
    [InlineData("XBOND-CANARY", XBondTrafficEngineModes.XBondCanary)]
    [InlineData("xbond-primary", XBondTrafficEngineModes.XBondPrimary)]
    [InlineData("unexpected", XBondTrafficEngineModes.SpeedifyPrimary)]
    public void Normalize_ReturnsSafeKnownMode(string? input, string expected)
    {
        Assert.Equal(expected, XBondTrafficEngineModes.Normalize(input));
    }

    [Fact]
    public void TrafficEngineStatus_AllowsCanaryServiceWhileSpeedifyRemainsPrimary()
    {
        var status = new XBondTrafficEngineStatus
        {
            Mode = XBondTrafficEngineModes.SpeedifyPrimary,
            ServiceControlAllowed = true,
            ClientServiceRunning = false
        };

        Assert.True(status.CanStartCanary);
    }

    [Fact]
    public void TrafficEngineStatus_ExposesBootEnablementActions()
    {
        var disabled = new XBondTrafficEngineStatus
        {
            ServiceControlAllowed = true,
            ClientServiceEnabled = false
        };
        var enabled = new XBondTrafficEngineStatus
        {
            ServiceControlAllowed = true,
            ClientServiceEnabled = true
        };

        Assert.True(disabled.CanEnableAtBoot);
        Assert.False(disabled.CanDisableAtBoot);
        Assert.False(enabled.CanEnableAtBoot);
        Assert.True(enabled.CanDisableAtBoot);
    }
}
