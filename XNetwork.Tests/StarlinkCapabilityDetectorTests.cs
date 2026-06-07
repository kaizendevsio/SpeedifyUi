using XNetwork.Services;

namespace XNetwork.Tests;

public class StarlinkCapabilityDetectorTests
{
    [Fact]
    public void Detect_FindsControlsFromStarlinkWebAssets()
    {
        var snapshot = StarlinkCapabilityDetector.Detect(
            "192.168.100.1",
            statusAvailable: true,
            webUiReachable: true,
            rootHtml: """<script src="/static/js/script.js.gz"></script>""",
            scriptText: "Diagnostic Reboot Stow Unstow");

        Assert.True(snapshot.IsAvailable);
        Assert.Contains(snapshot.Capabilities, c => c.Key == "status" && c.IsDetected);
        Assert.Contains(snapshot.Capabilities, c => c.Key == "web-ui" && c.IsDetected && c.IsActionable);
        Assert.Contains(snapshot.Capabilities, c => c.Key == "diagnostics" && c.IsDetected);
        Assert.Contains(snapshot.Capabilities, c => c.Key == "reboot" && c.IsDetected && c.IsDisruptive && c.DirectCommand == "reboot");
        Assert.Contains(snapshot.Capabilities, c => c.Key == "stow" && c.IsDetected && c.IsDisruptive && c.DirectCommand == "stow");
        Assert.Contains(snapshot.Capabilities, c => c.Key == "unstow" && c.IsDetected && c.IsDisruptive && c.DirectCommand == "unstow");
        Assert.Contains(snapshot.Capabilities, c => c.Key == "dish-clear-obstruction-map" && c.IsDetected && c.IsDisruptive && c.DirectCommand == "dish_clear_obstruction_map");
    }

    [Fact]
    public void Detect_ExposesDirectCommandsWhenFirmwareSymbolsArePresent()
    {
        var snapshot = StarlinkCapabilityDetector.Detect(
            "192.168.100.1",
            statusAvailable: true,
            webUiReachable: false,
            rootHtml: null,
            scriptText: "RebootRequest setReboot DishStowRequest setDishStow setUnstow");

        var reboot = Assert.Single(snapshot.Capabilities, c => c.Key == "reboot");
        Assert.True(reboot.IsDetected);
        Assert.True(reboot.IsActionable);
        Assert.Equal("reboot", reboot.DirectCommand);
        Assert.Null(reboot.ActionUrl);
        Assert.Null(reboot.DisabledReason);
    }

    [Fact]
    public void Detect_ExposesClearObstructionMapWhenLocalApiIsReachable()
    {
        var snapshot = StarlinkCapabilityDetector.Detect(
            "192.168.100.1",
            statusAvailable: true,
            webUiReachable: false,
            rootHtml: null,
            scriptText: null);

        var resetMap = Assert.Single(snapshot.Capabilities, c => c.Key == "dish-clear-obstruction-map");
        Assert.True(resetMap.IsDetected);
        Assert.True(resetMap.IsActionable);
        Assert.True(resetMap.IsDisruptive);
        Assert.Equal("dish_clear_obstruction_map", resetMap.DirectCommand);
        Assert.Contains("hours or days", resetMap.Description);
    }
}
