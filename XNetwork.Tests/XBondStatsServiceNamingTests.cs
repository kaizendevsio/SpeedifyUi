using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondStatsServiceNamingTests
{
    [Fact]
    public void UsesIspNameOverMetadataNameAndKeepsAliasOnTop()
    {
        var status = new XBondStatus
        {
            Paths =
            [
                new XBondPathStatus { PathId = 1, InterfaceName = "enx0", Role = "anchor", InterfaceUp = true },
                new XBondPathStatus { PathId = 2, InterfaceName = "enx1", Role = "probe", InterfaceUp = true }
            ]
        };
        var interfaces = new[]
        {
            new InterfaceMetadataService.InterfaceMetadata("enx0", "ethernet", "connected", "Smart", "Smart"),
            new InterfaceMetadataService.InterfaceMetadata("enx1", "ethernet", "connected", "Wired connection 2", "")
        };
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["enx0"] = "Upstairs modem" };
        var ispNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["enx0"] = "Smart Communications",
            ["enx1"] = "Starlink"
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            aliases,
            ispNames);

        Assert.Equal("Upstairs modem", snapshot.Paths.Single(path => path.InterfaceName == "enx0").Name);
        Assert.Equal("Starlink", snapshot.Paths.Single(path => path.InterfaceName == "enx1").Name);
    }

    [Fact]
    public void FallsBackToMetadataNameWhenIspLookupHasNoAnswer()
    {
        var status = new XBondStatus
        {
            Paths = [new XBondPathStatus { PathId = 1, InterfaceName = "enx2", Role = "anchor", InterfaceUp = true }]
        };
        var interfaces = new[]
        {
            new InterfaceMetadataService.InterfaceMetadata("enx2", "ethernet", "connected", "Globe", "Globe")
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal("Globe", snapshot.Paths.Single().Name);
    }

    [Fact]
    public void NamesUnconfiguredLocalInterfacesFromIspToo()
    {
        var status = new XBondStatus { Paths = [] };
        var interfaces = new[]
        {
            new InterfaceMetadataService.InterfaceMetadata("wlan0", "wifi", "connected", "Home Wi-Fi", "Home Wi-Fi")
        };
        var ispNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["wlan0"] = "Converge ICT" };

        var snapshot = XBondStatsService.FromStatus(
            status,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ispNames);

        var path = Assert.Single(snapshot.Paths);
        Assert.Equal("Converge ICT", path.Name);
        Assert.False(path.IsConfigured);
    }

    [Fact]
    public void LiveWatchdogAliasEditsReachNameResolution()
    {
        // NetworkMonitorService keeps its own settings copy, so aliases must be read from
        // GetSettings() rather than the startup singleton or edits stay invisible until restart.
        var singleton = new NetworkMonitorSettings();
        var service = new NetworkMonitorService(
            NullLogger<NetworkMonitorService>.Instance,
            singleton,
            new NoopHostApplicationLifetime());

        service.UpdateSettings(new NetworkMonitorSettings
        {
            AdapterAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enx0"] = "Upstairs modem"
            }
        });

        Assert.Empty(XBondStatsService.BuildAdapterAliases(singleton));
        Assert.Equal("Upstairs modem", XBondStatsService.BuildAdapterAliases(service.GetSettings())["enx0"]);
    }

    private sealed class NoopHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
        }
    }

    [Fact]
    public void BuildAdapterAliasesReadsAliasesStraightFromSettings()
    {
        var settings = new NetworkMonitorSettings
        {
            AdapterAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enx0"] = "  Upstairs modem  ",
                ["enx1"] = "   "
            }
        };

        var aliases = XBondStatsService.BuildAdapterAliases(settings);

        Assert.Equal("Upstairs modem", aliases["enx0"]);
        Assert.False(aliases.ContainsKey("enx1"));
    }
}
