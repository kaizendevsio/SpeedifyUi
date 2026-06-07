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
        Assert.Contains(snapshot.Capabilities, c => c.Key == "reboot" && c.IsDetected && c.IsDisruptive);
        Assert.Contains(snapshot.Capabilities, c => c.Key == "stow" && c.IsDetected && c.IsDisruptive);
        Assert.Contains(snapshot.Capabilities, c => c.Key == "unstow" && c.IsDetected && c.IsDisruptive);
    }

    [Fact]
    public void Detect_DoesNotExposeActionsWhenWebUiIsUnavailable()
    {
        var snapshot = StarlinkCapabilityDetector.Detect(
            "192.168.100.1",
            statusAvailable: true,
            webUiReachable: false,
            rootHtml: null,
            scriptText: "Reboot Stow");

        var reboot = Assert.Single(snapshot.Capabilities, c => c.Key == "reboot");
        Assert.True(reboot.IsDetected);
        Assert.False(reboot.IsActionable);
        Assert.Null(reboot.ActionUrl);
        Assert.NotNull(reboot.DisabledReason);
    }
}
