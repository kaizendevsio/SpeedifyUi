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
}
