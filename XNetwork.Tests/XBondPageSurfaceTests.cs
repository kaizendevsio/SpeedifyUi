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
        Assert.Contains("_snapshot.EffectiveServerHealthStatus != \"healthy\"", home);
        Assert.Contains("Recovery @_snapshot.RecoveryHoldMs ms", home);
        Assert.DoesNotContain("IsRecoveryActive", summary);
        Assert.DoesNotContain("RecoveryHoldMs", summary);
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
