using XNetwork.Models;

namespace XNetwork.Tests;

public class AppChangelogTests
{
    [Fact]
    public void CurrentVersion_UsesDateBasedMonthlyRevision()
    {
        Assert.Equal("xbond-2026.06.22", AppChangelog.CurrentVersion);
        Assert.Matches(@"^xbond-\d{4}\.\d{2}\.\d+$", AppChangelog.CurrentVersion);
    }

    [Fact]
    public void Changelog_StartsWithCurrentVersion()
    {
        var entry = AppChangelog.Entries.First();

        Assert.Equal(AppChangelog.CurrentVersion, entry.Version);
        Assert.Contains(entry.Changes, change => change.Contains("per-path download and upload", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entry.Changes, change => change.Contains("per-path inbound and outbound", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(entry.Changes, change => change.Contains("Pi to Vultr iperf", StringComparison.OrdinalIgnoreCase));
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
