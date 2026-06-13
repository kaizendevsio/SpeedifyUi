using XNetwork.Models;

namespace XNetwork.Tests;

public class AppChangelogTests
{
    [Fact]
    public void CurrentVersion_UsesDateBasedMonthlyRevision()
    {
        Assert.Equal("2026.06.11", AppChangelog.CurrentVersion);
        Assert.Matches(@"^\d{4}\.\d{2}\.\d+$", AppChangelog.CurrentVersion);
    }

    [Fact]
    public void Changelog_StartsWithCurrentVersion()
    {
        var entry = AppChangelog.Entries.First();

        Assert.Equal(AppChangelog.CurrentVersion, entry.Version);
        Assert.Contains(entry.Changes, change => change.Contains("public test button", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entry.Changes, change => change.Contains("packet loss", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entry.Changes, change => change.Contains("shadow-test mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Changelog_KeepsPreviousVersionEntries()
    {
        Assert.True(AppChangelog.Entries.Count >= 2);
        Assert.Contains(AppChangelog.Entries, entry =>
            entry.Version == "2026.06.1" &&
            entry.Changes.Any(change => change.Contains("reset obstruction map", StringComparison.OrdinalIgnoreCase)) &&
            entry.Changes.Any(change => change.Contains("changelog", StringComparison.OrdinalIgnoreCase)));
    }
}
