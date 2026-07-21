using System.Text.RegularExpressions;
using XNetwork.Models;

namespace XNetwork.Tests;

public sealed partial class VisibleBrandingTests
{
    [Fact]
    public void RazorAndManifest_DoNotExposeLegacyProductName()
    {
        var componentRoot = Path.GetDirectoryName(FindRepoFile("XNetwork", "Components", "App.razor"))!;

        foreach (var file in Directory.EnumerateFiles(componentRoot, "*.razor", SearchOption.AllDirectories))
        {
            foreach (var sourceLine in File.ReadLines(file))
            {
                var line = RemoveAllowedInternalIdentifiers(sourceLine);
                Assert.DoesNotMatch(LegacyProductName(), line);
            }
        }

        var manifest = File.ReadAllText(FindRepoFile("XNetwork", "wwwroot", "manifest.json"));
        Assert.DoesNotMatch(LegacyProductName(), manifest);
    }

    [Fact]
    public void UiFallbacksAndChangelog_DoNotExposeLegacyProductName()
    {
        var sourceRoots = new[]
        {
            Path.GetDirectoryName(FindRepoFile("XNetwork", "Models", "XBondStatus.cs"))!,
            Path.GetDirectoryName(FindRepoFile("XNetwork", "Services", "XBondStatusService.cs"))!
        };

        foreach (var root in sourceRoots)
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
            {
                foreach (var sourceLine in File.ReadLines(file))
                {
                    var line = RemoveAllowedInternalIdentifiers(sourceLine);
                    Assert.DoesNotMatch(LegacyDisplayName(), line);
                }
            }
        }

        foreach (var entry in AppChangelog.Entries)
        {
            Assert.False(entry.Version.StartsWith("xbond-", StringComparison.OrdinalIgnoreCase),
                $"Legacy version prefix found in changelog: {entry.Version}");
            Assert.DoesNotContain("xbond", entry.Summary, StringComparison.OrdinalIgnoreCase);

            foreach (var change in entry.Changes)
            {
                Assert.DoesNotContain("xbond", change, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static string RemoveAllowedInternalIdentifiers(string value)
    {
        var sanitized = value;
        string[] allowed =
        [
            "/run/xbond/",
            "/etc/xbond/",
            "/usr/local/sbin/xbond-",
            "/home/xeon-network/.config/XNetwork/xbond-",
            "/xbond",
            "xbond0",
            "xbonds0",
            "xbond-path-card",
            "xbond-client.service",
            "xbond-server.service",
            "xbond-server-nat.service",
            "xbond-client",
            "xbond-server",
            "xbond-active",
            "XBOND_PSK"
        ];

        foreach (var identifier in allowed)
        {
            sanitized = sanitized.Replace(identifier, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        return sanitized;
    }

    private static string FindRepoFile(params string[] pathParts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpeedifyUi.sln")))
            {
                return Path.Combine([directory.FullName, .. pathParts]);
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate SpeedifyUi repository root.");
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_])xbond(?![A-Za-z0-9_])", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyProductName();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])XBond(?![A-Za-z0-9_])")]
    private static partial Regex LegacyDisplayName();
}
