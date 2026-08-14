using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class XBondPathAvailabilityTests
{
    private static readonly string[] Present = ["enxb8d4bcbcb0f0", "enx103c59f1039c", "wlan0", "enxc8a3627ddf6a"];

    [Fact]
    public void MissingInterfaceIsClassified()
    {
        Assert.Equal(
            XBondPathAvailability.InterfaceMissing,
            XBondPathAvailabilityResolver.Resolve("enxb8d4bcc3bf30", Present, []));
    }

    [Fact]
    public void PresentButDownInterfaceStaysNormal()
    {
        // enxc8a3627ddf6a is present and NO-CARRIER: a real outage, not a dead path.
        Assert.Equal(
            XBondPathAvailability.Normal,
            XBondPathAvailabilityResolver.Resolve("enxc8a3627ddf6a", Present, []));
    }

    [Fact]
    public void WifiDisabledIsClassified()
    {
        Assert.Equal(
            XBondPathAvailability.WifiDisabled,
            XBondPathAvailabilityResolver.Resolve("wlan0", Present, ["wlan0"]));
    }

    [Fact]
    public void WifiDisabledWinsOverMissingInterface()
    {
        // A disabled adapter released by NetworkManager can also look absent; the operator's choice
        // is the more useful explanation.
        Assert.Equal(
            XBondPathAvailability.WifiDisabled,
            XBondPathAvailabilityResolver.Resolve("wlan9", Present, ["wlan9"]));
    }

    [Fact]
    public void EmptyInterfaceListNeverReportsMissing()
    {
        // The interface list is empty on non-Linux hosts and when nmcli fails. Without this guard
        // every configured path would be mislabelled as missing hardware.
        Assert.Equal(
            XBondPathAvailability.Normal,
            XBondPathAvailabilityResolver.Resolve("enxb8d4bcc3bf30", [], []));
    }

    [Theory]
    [InlineData("WLAN0")]
    [InlineData("EnXb8D4BcBcB0F0")]
    public void MatchingIsCaseInsensitive(string interfaceName)
    {
        Assert.Equal(
            XBondPathAvailability.Normal,
            XBondPathAvailabilityResolver.Resolve(interfaceName, Present, []));
    }

    [Fact]
    public void BlankInterfaceIsNormal()
    {
        Assert.Equal(XBondPathAvailability.Normal, XBondPathAvailabilityResolver.Resolve("  ", Present, []));
    }

    [Fact]
    public void ReasonTextMatchesClassification()
    {
        Assert.Equal("Adapter not present", new XBondPathStatsSnapshot { Availability = XBondPathAvailability.InterfaceMissing }.UnavailableReason);
        Assert.Equal("Wi-Fi disabled in Settings", new XBondPathStatsSnapshot { Availability = XBondPathAvailability.WifiDisabled }.UnavailableReason);
        Assert.Equal("", new XBondPathStatsSnapshot().UnavailableReason);
    }

    [Fact]
    public void ConfiguredPathWithMissingAdapterIsShownOnDashboard()
    {
        var path = new XBondPathStatsSnapshot
        {
            InterfaceName = "enxb8d4bcc3bf30",
            IsConfigured = true,
            InterfaceUp = false,
            Availability = XBondPathAvailability.InterfaceMissing
        };

        Assert.True(path.ShowOnDashboard);
        Assert.True(path.IsUnavailable);
    }

    [Fact]
    public void ReproducesLiveRouterClassification()
    {
        // Ground truth captured from xeon-network on 2026-08-15: only the absent adapter and the
        // deliberately disabled Wi-Fi adapter are quarantined; the two NO-CARRIER paths are not.
        var status = new XBondStatus
        {
            Paths =
            [
                new XBondPathStatus { PathId = 3, InterfaceName = "enxb8d4bcbcb0f0", Role = "anchor", InterfaceUp = true },
                new XBondPathStatus { PathId = 6, InterfaceName = "enx103c59f1039c", Role = "probe", InterfaceUp = true },
                new XBondPathStatus { PathId = 1, InterfaceName = "enxc8a3627ddf6a", Role = "probe", InterfaceUp = false },
                new XBondPathStatus { PathId = 4, InterfaceName = "enxb8d4bcc3bf30", Role = "probe", InterfaceUp = false },
                new XBondPathStatus { PathId = 5, InterfaceName = "wlan0", Role = "probe", InterfaceUp = false },
                new XBondPathStatus { PathId = 7, InterfaceName = "enxc8a3627e60c1", Role = "probe", InterfaceUp = false }
            ]
        };
        var interfaces = new[]
        {
            Metadata("enxb8d4bcbcb0f0", "connected"),
            Metadata("enx103c59f1039c", "connected"),
            Metadata("wlan0", "disconnected"),
            Metadata("enxc8a3627ddf6a", "unavailable"),
            Metadata("enxc8a3627e60c1", "unavailable")
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            interfaces,
            new Dictionary<string, F50ModemTelemetry>(StringComparer.OrdinalIgnoreCase),
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ["wlan0"],
            // Kernel-visible interfaces: enxb8d4bcc3bf30 is genuinely absent.
            ["enxb8d4bcbcb0f0", "enx103c59f1039c", "wlan0", "enxc8a3627ddf6a", "enxc8a3627e60c1"]);

        Assert.Equal(
            XBondPathAvailability.InterfaceMissing,
            snapshot.Paths.Single(path => path.PathId == 4).Availability);
        Assert.Equal(
            XBondPathAvailability.WifiDisabled,
            snapshot.Paths.Single(path => path.PathId == 5).Availability);
        Assert.Equal(
            XBondPathAvailability.Normal,
            snapshot.Paths.Single(path => path.PathId == 1).Availability);
        Assert.Equal(
            XBondPathAvailability.Normal,
            snapshot.Paths.Single(path => path.PathId == 7).Availability);

        Assert.Equal([4, 5], snapshot.UnavailablePaths.Select(path => path.PathId).OrderBy(id => id));
        Assert.DoesNotContain(snapshot.ActivePaths, path => path.IsUnavailable);
        Assert.DoesNotContain(snapshot.StandbyPaths, path => path.IsUnavailable);
    }

    private static InterfaceMetadataService.InterfaceMetadata Metadata(string device, string state) =>
        new(device, device.StartsWith("wlan") ? "wifi" : "ethernet", state, "", "");
}
