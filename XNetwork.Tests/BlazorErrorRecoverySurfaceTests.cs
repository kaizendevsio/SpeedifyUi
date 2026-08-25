namespace XNetwork.Tests;

public sealed class BlazorErrorRecoverySurfaceTests
{
    [Fact]
    public void FatalRecoverySurface_ReplacesStockReloadBannerAccessibly()
    {
        var layout = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Layout", "MainLayout.razor"));
        var css = File.ReadAllText(FindRepoFile("XNetwork", "Components", "Layout", "MainLayout.razor.css"));
        var app = File.ReadAllText(FindRepoFile("XNetwork", "Components", "App.razor"));
        var reconnect = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "js", "blazorReconnect.js"));

        Assert.Contains("id=\"ulink-fatal-recovery\"", layout, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", layout, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"assertive\"", layout, StringComparison.Ordinal);
        Assert.DoesNotContain("An unhandled error has occurred", layout, StringComparison.Ordinal);
        Assert.DoesNotContain(">Reload<", layout, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion: reduce", css, StringComparison.Ordinal);
        Assert.Contains("blazorErrorRecovery.js", app, StringComparison.Ordinal);
        Assert.Contains("fatalRecoveryIsActive", reconnect, StringComparison.Ordinal);
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "uLink.sln")))
            {
                return Path.Combine([directory.FullName, .. pathParts]);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate uLink repository root.");
    }
}
