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
        Assert.Contains("Recovery {RecoveryHoldMs} ms", summary);
        Assert.Contains("\"healthy\"", summary);
    }

    [Fact]
    public void Dashboard_UsesStableKeyedFlipCardsWithoutAdapterHeading()
    {
        var home = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Home.razor"));
        var animation = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "js", "animatedUi.js"));

        Assert.DoesNotContain(">Adapters</h3>", home);
        Assert.DoesNotContain("GetLivePathCount", home);
        Assert.Contains("@key=\"path.PathId\"", home);
        Assert.Contains("data-flip-key=\"@path.PathId\"", home);
        Assert.Contains("GetOrderedDashboardPaths()", home);
        Assert.Contains("enableFlipList", home);
        Assert.Contains("new MutationObserver", animation);
        Assert.Contains("item.animate", animation);
        Assert.Contains("cubic-bezier(0.16, 1, 0.3, 1)", animation);
        Assert.Contains("prefersReducedMotion()", animation);
        Assert.Contains("active-redundant-group-surface", home);
        Assert.Contains("active-redundant-member", home);
    }

    [Fact]
    public void Dashboard_StatusPillsAnimateIndependentlyBelowSubtitle()
    {
        var summary = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Custom", "ConnectionSummary.razor"));
        var styles = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "app.css"));

        Assert.True(summary.IndexOf("GetStatusDescription()", StringComparison.Ordinal) <
                    summary.IndexOf("connection-summary-pills", StringComparison.Ordinal));
        Assert.Contains("<AnimatedStatusPill", summary);
        Assert.Contains("animated-status-pill-shell", styles);
        Assert.Contains("status-pill-enter-expand", styles);
        Assert.Contains("prefers-reduced-motion: reduce", styles);
    }

    [Fact]
    public void Dashboard_TechnicalRowsFollowPersistedAppearancePreference()
    {
        var home = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Home.razor"));
        var settings = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Settings.razor"));

        Assert.Contains("UiDisplayPreferences.AdapterTechnicalDetails", home);
        Assert.Contains("Adapter technical details", settings);
        Assert.Contains("UiDisplayPreferencesStore.SaveAsync", settings);
    }

    [Fact]
    public void Dashboard_SignalBarsAnimateInsideStableSlots()
    {
        var home = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Home.razor"));
        var styles = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "app.css"));

        Assert.Contains("ulink-signal-bar-slot", home);
        Assert.Contains("ulink-cellular-signal-bar-slot", home);
        Assert.Contains("--signal-bar-height", home);
        Assert.Contains("height 420ms cubic-bezier(0.16, 1, 0.3, 1)", styles);
        Assert.Contains("background-color 420ms ease-out", styles);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", styles);
    }

    [Fact]
    public void UserFacingShell_UsesuLinkBrandAndVectorLogo()
    {
        var layout = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Layout", "MainLayout.razor"));
        var manifest = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "manifest.json"));
        var logo = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "icons", "ulink-logo.svg"));

        Assert.Contains(">uLink<", layout);
        Assert.Contains("/icons/ulink-logo.svg", layout);
        Assert.DoesNotContain("xnetwork-logo", layout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"name\": \"uLink\"", manifest);
        Assert.Contains("<svg", logo);
        Assert.Contains("<title id=\"title\">uLink</title>", logo);
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
    public void LiveSurface_IsRemovedFromRoutesNavigationAndAssets()
    {
        var layout = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Layout", "MainLayout.razor"));
        var routes = File.ReadAllText(FindRepoFile("XNetwork", "Services", "RouteTransitionDirectionService.cs"));

        Assert.DoesNotContain("href=\"/live\"", layout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"/live\"", routes, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(FindRepoFile("XNetwork", "Components", "Pages", "Live.razor")));
        Assert.False(File.Exists(FindRepoFile("XNetwork", "wwwroot", "js", "liveView.js")));
    }

    [Fact]
    public void ConnectionStatusPills_UseIconHoldExpandAndDelayedFadeRemoval()
    {
        var component = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Custom", "AnimatedStatusPill.razor"));
        var styles = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "app.css"));

        Assert.Contains("TimeSpan.FromMilliseconds(220)", component);
        Assert.Contains("Task.Delay(ExitDuration", component);
        Assert.Contains("animated-status-pill-exiting", component);
        Assert.Contains("status-pill-enter-expand 1.36s", styles);
        Assert.Contains("0%, 74%", styles);
        Assert.Contains("status-pill-exit 220ms", styles);
        Assert.Contains("prefers-reduced-motion: reduce", styles);
    }

    [Fact]
    public void Dashboard_SeparatesFirstNonredundantAdapterFromActiveGroup()
    {
        var home = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Pages", "Home.razor"));
        var styles = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "app.css"));

        Assert.Contains("first-nonredundant-adapter", home);
        Assert.Contains(".adapter-flip-list > .first-nonredundant-adapter", styles);
        Assert.Contains("margin-top: 1rem", styles);
    }

    [Fact]
    public void NavigationAndDashboardChartUseNeutralStates()
    {
        var layout = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Layout", "MainLayout.razor"));
        var styles = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "app.css"));
        var charts = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "js", "statisticsCharts.js"));

        Assert.Contains("desktop-nav-item-active", layout);
        Assert.Contains(".mobile-tabbar-item-active:hover", styles);
        Assert.DoesNotContain("rgba(30, 41, 59, 0.62)", styles);
        Assert.Contains("DASHBOARD_TUNNEL_COLOR = 'rgba(126, 126, 126, 0.78)'", charts);
        Assert.Contains("borderDash: [6, 5]", charts);
        Assert.Contains("borderDash: [2, 5]", charts);
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
