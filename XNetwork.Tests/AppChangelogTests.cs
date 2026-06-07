using XNetwork.Models;

namespace XNetwork.Tests;

public class AppChangelogTests
{
    [Fact]
    public void CurrentVersion_UsesDateBasedMonthlyRevision()
    {
        Assert.Equal("2026.06.1", AppChangelog.CurrentVersion);
        Assert.Matches(@"^\d{4}\.\d{2}\.\d+$", AppChangelog.CurrentVersion);
    }

    [Fact]
    public void Changelog_StartsWithCurrentVersion()
    {
        var entry = Assert.Single(AppChangelog.Entries);

        Assert.Equal(AppChangelog.CurrentVersion, entry.Version);
        Assert.Contains(entry.Changes, change => change.Contains("reset obstruction map", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entry.Changes, change => change.Contains("changelog", StringComparison.OrdinalIgnoreCase));
    }
}
