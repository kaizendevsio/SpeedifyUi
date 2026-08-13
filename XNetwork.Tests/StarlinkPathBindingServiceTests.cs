using XNetwork.Models;
using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkPathBindingServiceTests
{
    [Fact]
    public void CreatePlan_RebindsNamedStarlinkPathToVerifiedInterface()
    {
        var config = new XBondClientConfig
        {
            Paths =
            [
                new() { Id = 1, Name = "Starlink", InterfaceName = "enx-old", Enabled = true },
                new() { Id = 2, Name = "Mobile", InterfaceName = "enx-mobile", Enabled = true }
            ]
        };

        var plan = StarlinkPathBindingService.CreatePlan(
            config,
            StarlinkInterfaceResolution.Available("enx-new", "verified"),
            ["Starlink"]);

        Assert.True(plan.Required);
        Assert.Equal(1, plan.PathId);
        Assert.Equal("enx-old", plan.CurrentInterface);
        Assert.Equal("enx-new", plan.DetectedInterface);
    }

    [Fact]
    public void CreatePlan_DoesNotStealInterfaceOwnedByAnotherPath()
    {
        var config = new XBondClientConfig
        {
            Paths =
            [
                new() { Id = 1, Name = "Starlink", InterfaceName = "enx-old", Enabled = true },
                new() { Id = 2, Name = "Mobile", InterfaceName = "enx-new", Enabled = true }
            ]
        };

        var plan = StarlinkPathBindingService.CreatePlan(
            config,
            StarlinkInterfaceResolution.Available("enx-new", "verified"),
            ["Starlink"]);

        Assert.False(plan.Required);
        Assert.Contains("already assigned", plan.Reason);
    }
}
