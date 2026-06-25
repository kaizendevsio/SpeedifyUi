using Microsoft.Extensions.Logging.Abstractions;
using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class TrafficBypassServiceTests
{
    [Fact]
    public void ValidateRule_AllowsDestinationPortAndSelectedPhysicalInterface()
    {
        var service = CreateService();
        var result = service.ValidateRule(new TrafficBypassRule
        {
            DisplayName = "Work VPN",
            Destinations = ["203.0.113.10", "198.51.100.0/24"],
            Protocol = TrafficBypassProtocols.Tcp,
            Ports = ["443", "5000-5010"],
            EgressMode = TrafficBypassEgressModes.Interface,
            InterfaceName = "wlan0"
        });

        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }

    [Fact]
    public void ValidateRule_RejectsUnsafeInterfaceAndBadMatches()
    {
        var service = CreateService();
        var result = service.ValidateRule(new TrafficBypassRule
        {
            DisplayName = "Bad",
            Destinations = ["not-an-ip"],
            Ports = ["65536"],
            EgressMode = TrafficBypassEgressModes.Interface,
            InterfaceName = "xbond0"
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("Destination", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("Port", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, error => error.Contains("egress adapter", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateRule_RequiresDestinationOrPort()
    {
        var service = CreateService();
        var result = service.ValidateRule(new TrafficBypassRule
        {
            DisplayName = "Empty",
            Destinations = [],
            Ports = [],
            EgressMode = TrafficBypassEgressModes.AutoPhysical
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("destination", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NormalizeRule_SplitsCommaAndWhitespaceLists()
    {
        var normalized = TrafficBypassService.NormalizeRule(new TrafficBypassRule
        {
            DisplayName = "Split",
            Destinations = ["8.8.8.8, 8.8.4.4", "203.0.113.0/24"],
            Ports = ["443 8443", "5000-5010"],
            EgressMode = TrafficBypassEgressModes.AutoPhysical
        });

        Assert.Equal(["8.8.8.8", "8.8.4.4", "203.0.113.0/24"], normalized.Destinations);
        Assert.Equal(["443", "8443", "5000-5010"], normalized.Ports);
        Assert.Equal("", normalized.InterfaceName);
    }

    private static TrafficBypassService CreateService()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"traffic-bypass-{Guid.NewGuid():N}.json");
        return new TrafficBypassService(
            new TrafficBypassSettings(),
            new TrafficBypassSettingsStore(NullLogger<TrafficBypassSettingsStore>.Instance, filePath),
            new InterfaceMetadataService(NullLogger<InterfaceMetadataService>.Instance),
            NullLogger<TrafficBypassService>.Instance);
    }
}
