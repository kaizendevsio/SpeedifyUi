using System.Globalization;
using Microsoft.Playwright;

namespace XNetwork.Tests;

public class BrowserSmokeTests
{
    private static readonly string[] Routes =
    [
        "/",
        "/details",
        "/xrouter",
        "/settings",
        "/xbond"
    ];

    [Fact]
    public async Task KeyRoutesLoadInBrowserWhenBaseUrlIsConfigured()
    {
        var baseUrl = Environment.GetEnvironmentVariable("XNETWORK_BROWSER_SMOKE_BASE_URL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize
            {
                Width = 390,
                Height = 844
            }
        });

        var page = await context.NewPageAsync();
        var pageErrors = new List<string>();
        page.PageError += (_, error) => pageErrors.Add(error);

        foreach (var route in Routes)
        {
            var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), route.TrimStart('/'));
            var response = await page.GotoAsync(uri.ToString(), new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 15_000
            });

            Assert.NotNull(response);
            Assert.True(response!.Ok, $"{route} returned HTTP {(int)response.Status}");

            await page.Locator("body").WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 10_000
            });

            await page.WaitForFunctionAsync("() => document.title.includes('uLink')", null, new PageWaitForFunctionOptions
            {
                Timeout = 10_000
            });
            Assert.Contains("uLink", await page.TitleAsync(), StringComparison.Ordinal);
        }

        var removedLiveRoute = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "live");
        var liveResponse = await page.GotoAsync(removedLiveRoute.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 15_000
        });

        Assert.NotNull(liveResponse);
        Assert.Equal(404, liveResponse!.Status);

        Assert.True(pageErrors.Count == 0, string.Join(Environment.NewLine, pageErrors));
    }

    [Fact]
    public async Task StatusPills_HoldAsTrueCirclesThenExpandWithoutIconShiftOnMobile()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 360, Height = 800 },
            ReducedMotion = ReducedMotion.NoPreference
        });
        var page = await context.NewPageAsync();
        var styles = await File.ReadAllTextAsync(FindRepoFile("XNetwork", "wwwroot", "app.css"));
        await page.SetContentAsync($$"""
            <!doctype html>
            <html>
            <head><style>{{styles}}</style></head>
            <body>
                <div id="status-pill-regression-host"
                     class="connection-summary-pills"
                     style="display:flex;flex-wrap:wrap;align-items:center;width:220px;position:fixed;left:12px;top:12px">
                    <span class="animated-status-pill-shell connection-summary-pill-good" data-test-pill="first">
                        <span class="connection-summary-pill">
                            <span class="animated-status-pill-icon"><i>&#9679;</i></span>
                            <span class="animated-status-pill-text">Protecting from loss</span>
                        </span>
                    </span>
                    <span class="animated-status-pill-shell connection-summary-pill-warning" data-test-pill="second">
                        <span class="connection-summary-pill">
                            <span class="animated-status-pill-icon"><i>&#9679;</i></span>
                            <span class="animated-status-pill-text">Recovery 500 ms</span>
                        </span>
                    </span>
                </div>
            </body>
            </html>
            """);

        var first = page.Locator("[data-test-pill='first']");
        var icon = first.Locator(".animated-status-pill-icon");
        await SetAnimationTimeAsync(page, 220);

        var collapsed = await first.BoundingBoxAsync();
        var collapsedIcon = await icon.BoundingBoxAsync();
        Assert.NotNull(collapsed);
        Assert.NotNull(collapsedIcon);
        Assert.InRange(Math.Abs(collapsed!.Width - collapsed.Height), 0, 0.75);
        Assert.InRange(Math.Abs((collapsedIcon!.X + collapsedIcon.Width / 2) - (collapsed.X + collapsed.Width / 2)), 0, 0.75);

        var collapsedStyle = await first.EvaluateAsync<string[]>("""
            element => {
                const style = getComputedStyle(element);
                return [
                    style.boxSizing,
                    style.borderRadius,
                    style.borderRightWidth,
                    style.paddingLeft,
                    style.paddingRight,
                    style.overflow
                ];
            }
            """);
        Assert.Equal("border-box", collapsedStyle[0]);
        Assert.NotEqual("0px", collapsedStyle[2]);
        Assert.Equal("0px", collapsedStyle[3]);
        Assert.Equal("0px", collapsedStyle[4]);
        Assert.Contains(collapsedStyle[5], new[] { "hidden", "clip" });

        await SetAnimationTimeAsync(page, 940);
        var held = await first.BoundingBoxAsync();
        Assert.NotNull(held);
        Assert.InRange(Math.Abs(held!.Width - held.Height), 0, 1.0);

        await SetAnimationTimeAsync(page, 1_360);
        var expanded = await first.BoundingBoxAsync();
        var expandedIcon = await icon.BoundingBoxAsync();
        Assert.NotNull(expanded);
        Assert.NotNull(expandedIcon);
        Assert.True(expanded!.Width > expanded.Height + 40, $"Expected expanded pill, got {expanded.Width}x{expanded.Height}.");
        Assert.InRange(Math.Abs((expandedIcon!.X + expandedIcon.Width / 2) - (expanded.X + collapsed.Width / 2)), 0, 0.75);

        var layout = await page.Locator("#status-pill-regression-host").EvaluateAsync<int[]>("""
            element => [element.clientWidth, element.scrollWidth]
            """);
        Assert.True(layout[1] <= layout[0] + 1, "Animated pills overflowed their narrow mobile container.");
        Assert.Equal("nowrap", await first.Locator(".animated-status-pill-text").EvaluateAsync<string>("element => getComputedStyle(element).whiteSpace"));

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
        await page.EvaluateAsync("""
            () => {
                const host = document.querySelector('#status-pill-regression-host');
                host.replaceWith(host.cloneNode(true));
            }
            """);
        await page.WaitForTimeoutAsync(25);
        var reducedMotionPill = page.Locator("[data-test-pill='first']");
        var reducedMotionBox = await reducedMotionPill.BoundingBoxAsync();
        Assert.NotNull(reducedMotionBox);
        Assert.True(reducedMotionBox!.Width > reducedMotionBox.Height + 40, "Reduced motion should render the final expanded state immediately.");
        Assert.Equal("1", await reducedMotionPill.EvaluateAsync<string>("element => getComputedStyle(element).opacity"));
    }

    [Fact]
    public async Task ConnectionSummaryPillRow_CollapsesWithoutPlaceholderAndRespectsReducedMotion()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 390, Height = 844 },
            ReducedMotion = ReducedMotion.NoPreference
        });
        var page = await context.NewPageAsync();
        var styles = await File.ReadAllTextAsync(FindRepoFile("XNetwork", "wwwroot", "app.css"));
        await page.SetContentAsync($$"""
            <!doctype html>
            <html>
            <head><style>{{styles}}</style></head>
            <body style="margin:0;padding:16px;background:#202020">
                <section id="card" class="connection-summary-card ulink-dynamic-card" style="width:320px;border:1px solid #555;padding:16px;background:#262626">
                    <p style="margin:0;color:white">Low latency</p>
                    <div id="row" class="connection-summary-pill-row ulink-collapsible-row">
                        <div class="ulink-collapsible-row-inner">
                            <div class="connection-summary-pills" style="display:flex;flex-wrap:wrap;align-items:center">
                                <span class="animated-status-pill-shell connection-summary-pill-good">
                                    <span class="connection-summary-pill">
                                        <span class="animated-status-pill-icon"><i>&#9679;</i></span>
                                        <span class="animated-status-pill-text">Protecting from loss</span>
                                    </span>
                                </span>
                            </div>
                        </div>
                    </div>
                    <div id="metrics" style="height:48px;margin-top:12px;background:#303030"></div>
                </section>
            </body>
            </html>
            """);

        var row = page.Locator("#row");
        var card = page.Locator("#card");
        var hiddenRow = await row.BoundingBoxAsync();
        var hiddenCard = await card.BoundingBoxAsync();
        Assert.NotNull(hiddenRow);
        Assert.NotNull(hiddenCard);
        Assert.InRange(hiddenRow!.Height, 0, 1);

        await row.EvaluateAsync("element => element.classList.add('ulink-collapsible-row-visible')");
        await page.WaitForTimeoutAsync(500);

        var visibleRow = await row.BoundingBoxAsync();
        var visibleCard = await card.BoundingBoxAsync();
        Assert.NotNull(visibleRow);
        Assert.NotNull(visibleCard);
        Assert.True(visibleRow!.Height >= 23, $"Expected visible pill row height, got {visibleRow.Height}.");
        Assert.True(visibleCard!.Height > hiddenCard!.Height + 20, "Card height should grow only when the pill row is visible.");

        await page.EmulateMediaAsync(new PageEmulateMediaOptions { ReducedMotion = ReducedMotion.Reduce });
        var transitionDurations = await row.EvaluateAsync<string>("element => getComputedStyle(element).transitionDuration");
        foreach (var duration in transitionDurations.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.InRange(ParseCssDurationMs(duration), 0, 5);
        }
    }

    private static string FindRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(segments)} from {AppContext.BaseDirectory}.");
    }

    private static Task SetAnimationTimeAsync(IPage page, double milliseconds)
    {
        return page.EvaluateAsync("""
            milliseconds => {
                for (const animation of document.getAnimations()) {
                    animation.pause();
                    animation.currentTime = milliseconds;
                }
            }
            """, milliseconds);
    }

    private static double ParseCssDurationMs(string value)
    {
        value = value.Trim();
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return double.Parse(value[..^2], CultureInfo.InvariantCulture);
        }

        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            return double.Parse(value[..^1], CultureInfo.InvariantCulture) * 1000;
        }

        return 0;
    }
}
