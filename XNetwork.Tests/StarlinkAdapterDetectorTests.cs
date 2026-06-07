using XNetwork.Models;
using XNetwork.Utils;

namespace XNetwork.Tests;

public class StarlinkAdapterDetectorTests
{
    [Fact]
    public void HasStarlinkMetadata_DetectsSatelliteIspTypeWithoutAdapterId()
    {
        var adapter = new Adapter(
            AdapterId: "enx-dynamic-usb-id",
            Name: "enx-dynamic-usb-id",
            Isp: "",
            State: "connected",
            Priority: "automatic",
            WorkingPriority: "always",
            Type: "Ethernet")
        {
            IspType = "Satellite"
        };

        Assert.True(StarlinkAdapterDetector.HasStarlinkMetadata(adapter));
    }

    [Fact]
    public void HasStarlinkMetadata_DetectsStarlinkIspWithoutAdapterId()
    {
        var adapter = new Adapter(
            AdapterId: "usb-position-dependent-id",
            Name: "usb-position-dependent-id",
            Isp: "Starlink",
            State: "connected",
            Priority: "automatic",
            WorkingPriority: "always",
            Type: "Ethernet");

        Assert.True(StarlinkAdapterDetector.HasStarlinkMetadata(adapter));
    }

    [Fact]
    public void HasStarlinkMetadata_DoesNotTreatInterfaceIdAsStarlink()
    {
        var adapter = new Adapter(
            AdapterId: "enxc8a3627e60c1",
            Name: "enxc8a3627e60c1",
            Isp: "",
            State: "connected",
            Priority: "automatic",
            WorkingPriority: "always",
            Type: "Ethernet");

        Assert.False(StarlinkAdapterDetector.HasStarlinkMetadata(adapter));
    }

    [Fact]
    public void MatchesManagementGateway_MatchesConfiguredStarlinkHostOnly()
    {
        Assert.True(StarlinkAdapterDetector.MatchesManagementGateway("192.168.100.1", "192.168.100.1"));
        Assert.False(StarlinkAdapterDetector.MatchesManagementGateway("192.168.3.1", "192.168.100.1"));
    }
}
