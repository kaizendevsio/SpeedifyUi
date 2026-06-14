using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondTrafficEngineServiceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("xbond-active")]
    [InlineData("xbond-primary")]
    [InlineData("unexpected")]
    public void Normalize_ReturnsXBondActive(string? input)
    {
        Assert.Equal(XBondTrafficEngineModes.XBondActive, XBondTrafficEngineModes.Normalize(input));
    }

    [Fact]
    public async Task SetModeAsync_PersistsXBondActive()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"xbond-settings-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new XBondSettings { Enabled = false, TrafficEngineMode = "legacy" };
            var store = new XBondSettingsStore(NullLogger<XBondSettingsStore>.Instance, filePath);
            var service = new XBondTrafficEngineService(
                NullLogger<XBondTrafficEngineService>.Instance,
                settings,
                store);

            var status = await service.SetModeAsync("anything");

            Assert.Equal(XBondTrafficEngineModes.XBondActive, settings.TrafficEngineMode);
            Assert.True(settings.Enabled);
            Assert.Equal(XBondTrafficEngineModes.XBondActive, status.Mode);
            Assert.True(File.Exists(filePath));

            var loaded = new XBondSettings();
            store.Load(loaded);

            Assert.Equal(XBondTrafficEngineModes.XBondActive, loaded.TrafficEngineMode);
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

    [Fact]
    public void TrafficEngineStatus_ExposesServiceAndBootActions()
    {
        var stopped = new XBondTrafficEngineStatus
        {
            ServiceControlAllowed = true,
            ClientServiceRunning = false,
            ClientServiceEnabled = false
        };
        var running = new XBondTrafficEngineStatus
        {
            ServiceControlAllowed = true,
            ClientServiceRunning = true,
            ClientServiceEnabled = true
        };

        Assert.True(stopped.CanStart);
        Assert.False(stopped.CanStop);
        Assert.True(stopped.CanEnableAtBoot);
        Assert.False(running.CanStart);
        Assert.True(running.CanStop);
        Assert.True(running.CanDisableAtBoot);
    }
}
