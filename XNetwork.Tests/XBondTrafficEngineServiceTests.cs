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

    [Fact]
    public async Task GetStatusAsync_ReusesServiceManagerResultWithinTtl()
    {
        var calls = 0;
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-17T00:00:00Z"));
        var settings = new XBondSettings
        {
            AllowServiceControl = true,
            ClientServiceName = "xbond-client.service"
        };
        var store = new XBondSettingsStore(
            NullLogger<XBondSettingsStore>.Instance,
            Path.Combine(Path.GetTempPath(), $"xbond-settings-{Guid.NewGuid():N}.json"));
        var service = new XBondTrafficEngineService(
            NullLogger<XBondTrafficEngineService>.Instance,
            settings,
            store,
            time,
            (arguments, _) =>
            {
                calls++;
                return Task.FromResult(new XBondTrafficEngineService.ServiceCommandResult(
                    0,
                    arguments[0] == "is-active" ? "active" : "enabled"));
            },
            () => true,
            TimeSpan.FromSeconds(3));

        var first = await service.GetStatusAsync();
        var second = await service.GetStatusAsync();

        Assert.Same(first, second);
        Assert.Equal(2, calls);

        time.Advance(TimeSpan.FromSeconds(4));
        await service.GetStatusAsync();

        Assert.Equal(4, calls);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration)
        {
            _now += duration;
        }
    }
}
