using Microsoft.Playwright;

namespace XNetwork.Tests;

public class BrowserSmokeTests
{
    private static readonly string[] Routes =
    [
        "/",
        "/details",
        "/xrouter",
        "/settings"
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

            Assert.Contains("XNetwork", await page.TitleAsync(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(pageErrors.Count == 0, string.Join(Environment.NewLine, pageErrors));
    }
}
