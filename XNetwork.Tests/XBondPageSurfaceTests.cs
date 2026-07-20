namespace XNetwork.Tests;

public class XBondPageSurfaceTests
{
    [Fact]
    public void XBondPage_ExposesOnlySpeedTestAsUserFacingAction()
    {
        var content = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "XBond.razor"));

        Assert.Contains("Run speed test", content);

        foreach (var text in new[]
        {
            "Run heartbeat",
            "Run multi-path",
            "Simulate bad backup",
            "Run matrix",
            "MTU sweep",
            "Run route test",
            ">Start</button>",
            ">Stop</button>",
            ">Enable boot</button>"
        })
        {
            Assert.DoesNotContain(text, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SettingsPage_ShowsBlockedWatchdogAndConfirmedRecoveryAction()
    {
        var content = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Settings.razor"));

        Assert.Contains("Automatic restarts blocked", content);
        Assert.Contains("Reinitialize safety state", content);
        Assert.Contains("RequiredConfirmationText=\"RESET WATCHDOG\"", content);
        Assert.Contains("? \"Blocked\"", content);
        Assert.Contains(
            "isError: _clientWatchdogStatus.AutomaticRestartsBlocked",
            content);
    }

    [Fact]
    public void Dashboard_ShowsOnlyActionableTunnelStatusBadges()
    {
        var home = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Home.razor"));
        var summary = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Custom", "ConnectionSummary.razor"));

        Assert.DoesNotContain("GetServerBadgeClass", home);
        Assert.DoesNotContain("XBond active", home);
        Assert.DoesNotContain("<h3 class=\"text-lg font-semibold text-white\">XBond Tunnel</h3>", home);
        Assert.Contains("Mode=\"@FormatMode(_snapshot.RedundancyPolicy)\"", home);
        Assert.Contains("IsRecoveryActive=\"@_snapshot.IsRecoveryActive\"", home);
        Assert.Contains("ShowServerHealth", summary);
        Assert.Contains("Recovery @RecoveryHoldMs ms", summary);
        Assert.Contains("\"healthy\"", summary);
    }

    [Fact]
    public void UserFacingShell_UsesUlinkBrandAndVectorLogo()
    {
        var layout = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Layout", "MainLayout.razor"));
        var manifest = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "manifest.json"));
        var logo = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "icons", "ulink-logo.svg"));

        Assert.Contains(">Ulink<", layout);
        Assert.Contains("/icons/ulink-logo.svg", layout);
        Assert.DoesNotContain("xnetwork-logo", layout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\": \"Ulink\"", manifest);
        Assert.Contains("<svg", logo);
        Assert.Contains("<title id=\"title\">Ulink</title>", logo);
    }

    [Fact]
    public void ChangelogSheet_DelaysRemovalForExitAnimation()
    {
        var sheet = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Custom", "ActionSheet.razor"));
        var animation = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "js", "animatedUi.js"));

        Assert.Contains("@if (_isRendered)", sheet);
        Assert.Contains("action-sheet-panel-closing", sheet);
        Assert.Contains("CompleteCloseAsync", sheet);
        Assert.Contains("prefersReducedMotion", sheet);
        Assert.Contains("prefersReducedMotion", animation);
    }

    [Fact]
    public void LiveScene_UsesAbstractUlinkTopologyWithCameraControls()
    {
        var live = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "js", "liveView.js"));

        Assert.Contains("createUlinkRibbon", live);
        Assert.Contains("createRelayAperture", live);
        Assert.Contains("Ulink Core", live);
        Assert.Contains("rotateCamera", live);
        Assert.Contains("panCamera", live);
        Assert.Contains("handleWheel", live);
        Assert.Contains("1000 / 30", live);
        Assert.DoesNotContain("createSatelliteEndpoint", live);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidateRoot = directory.FullName;
            if (File.Exists(Path.Combine(candidateRoot, "SpeedifyUi.sln")))
            {
                return Path.Combine([candidateRoot, .. pathParts]);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SpeedifyUi repository root.");
    }
}
