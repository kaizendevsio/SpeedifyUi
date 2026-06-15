using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class InterfaceMetadataServiceTests
{
    [Fact]
    public void ParseNmcliDeviceStatus_UsesNonGenericConnectionNames()
    {
        var names = InterfaceMetadataService.ParseNmcliDeviceStatus(
            """
            enx103c59f1039c:ethernet:connected:Smart Communications
            enxb8d4bcbcb0f0:ethernet:connected:Dito\:Telecommunity
            enxc8a3627e60c1:ethernet:connected:Wired connection 1
            lo:loopback:connected:lo
            """);

        Assert.Equal("Smart Communications", names["enx103c59f1039c"]);
        Assert.Equal("Dito:Telecommunity", names["enxb8d4bcbcb0f0"]);
        Assert.False(names.ContainsKey("enxc8a3627e60c1"));
        Assert.False(names.ContainsKey("lo"));
    }

    [Fact]
    public void XBondStatsService_UsesLiveInterfaceNameWhenAvailable()
    {
        var status = new XBondStatus
        {
            Schedule = new XBondSchedulePlan
            {
                DataPathIds = [2],
                DuplicatePathIds = [3]
            },
            Paths =
            [
                new XBondPathStatus
                {
                    PathId = 2,
                    Name = "Configured Smart",
                    InterfaceName = "enx103c59f1039c",
                    Role = "anchor",
                    InterfaceUp = true
                },
                new XBondPathStatus
                {
                    PathId = 3,
                    Name = "Configured Dito",
                    InterfaceName = "enxb8d4bcbcb0f0",
                    Role = "backup",
                    InterfaceUp = true
                }
            ]
        };

        var snapshot = XBondStatsService.FromStatus(
            status,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["enx103c59f1039c"] = "Smart Communications"
            });

        Assert.Equal("Smart Communications", snapshot.Paths[0].Name);
        Assert.Equal("Configured Dito", snapshot.Paths[1].Name);
    }

    [Fact]
    public void ParseDefaultRoutes_ReadsGatewayRoutesFromLinuxJson()
    {
        var routes = InterfaceMetadataService.ParseDefaultRoutes(
            """
            [
              {"dst":"default","dev":"xbond0","prefsrc":"10.250.0.2"},
              {"dst":"default","gateway":"192.168.3.1","dev":"enx103c59f1039c"},
              {"dst":"default","gateway":"192.168.4.1","dev":"enxb8d4bcbcb0f0"}
            ]
            """);

        Assert.Collection(
            routes,
            route =>
            {
                Assert.Equal("enx103c59f1039c", route.Device);
                Assert.Equal("192.168.3.1", route.Gateway);
            },
            route =>
            {
                Assert.Equal("enxb8d4bcbcb0f0", route.Device);
                Assert.Equal("192.168.4.1", route.Gateway);
            });
    }

    [Fact]
    public void SelectProviderName_UsesF50NetworkProvider()
    {
        var provider = InterfaceMetadataService.SelectProviderName(new InterfaceMetadataService.ModemProviderResponse
        {
            NetworkProvider = "SMART",
            Operator = "",
            Error = null
        });

        Assert.Equal("SMART", provider);
    }

    [Fact]
    public void SelectProviderName_IgnoresBlockedOrEmptyResponses()
    {
        var blocked = InterfaceMetadataService.SelectProviderName(new InterfaceMetadataService.ModemProviderResponse
        {
            NetworkProvider = "SMART",
            Error = "none secure connection"
        });
        var noService = InterfaceMetadataService.SelectProviderName(new InterfaceMetadataService.ModemProviderResponse
        {
            NetworkProvider = "Limited Service"
        });

        Assert.Null(blocked);
        Assert.Null(noService);
    }
}
