using XNetwork.Models;

namespace XNetwork.Tests;

public class AppChangelogTests
{
    [Fact]
    public void CurrentVersion_UsesDateBasedMonthlyRevision()
    {
        Assert.Equal("ulink-2026.06.134", AppChangelog.CurrentVersion);
        Assert.Matches(@"^ulink-\d{4}\.\d{2}\.\d+$", AppChangelog.CurrentVersion);
    }

    [Fact]
    public void Changelog_StartsWithCurrentVersion()
    {
        var entry = AppChangelog.Entries.First();

        Assert.Equal(AppChangelog.CurrentVersion, entry.Version);
        Assert.Contains(entry.Changes, change => change.Contains("version", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entry.Changes, change => change.Contains("latency", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("bypass", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("trial", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("Unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("system log", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("every 30 seconds", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("fifteen minutes", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("details sheet", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("uLink", StringComparison.Ordinal));
        Assert.Contains(AppChangelog.Entries.SelectMany(item => item.Changes),
            change => change.Contains("automatic page recovery", StringComparison.OrdinalIgnoreCase));
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
