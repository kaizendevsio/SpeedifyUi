namespace XNetwork.Models;

public class BuildInfo
{
    public string Version { get; init; } = "dev";

    public int? DeployNumber { get; init; }

    public string Commit { get; init; } = "unknown";

    public string Branch { get; init; } = "unknown";

    public DateTimeOffset? BuiltAtUtc { get; init; }

    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;

    public string DisplayVersion => string.IsNullOrWhiteSpace(Version) ? AppChangelog.CurrentVersion : Version;
}
