using System.Text.RegularExpressions;

namespace XNetwork.Models;

public class BuildInfo
{
    private const int ReleaseYearMinimum = 2020;
    private static readonly Regex ReleaseVersionPattern = new(
        @"(?<!\d)(?<release>\d{4}\.\d{2}\.\d+)(?!\d)",
        RegexOptions.CultureInvariant);

    public string Version { get; init; } = "dev";

    public int? DeployNumber { get; init; }

    public string Commit { get; init; } = "unknown";

    public string Branch { get; init; } = "unknown";

    public DateTimeOffset? BuiltAtUtc { get; init; }

    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;

    public string DisplayVersion => FormatDisplayVersion(Version, AppChangelog.CurrentVersion);

    public static string FormatDisplayVersion(string? version, string fallbackVersion)
    {
        if (TryExtractReleaseVersion(version, out var displayVersion))
        {
            return displayVersion;
        }

        return TryExtractReleaseVersion(fallbackVersion, out displayVersion)
            ? displayVersion
            : fallbackVersion;
    }

    private static bool TryExtractReleaseVersion(string? value, out string displayVersion)
    {
        displayVersion = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = ReleaseVersionPattern.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var candidate = match.Groups["release"].Value;

        var parts = candidate.Split('.');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var year) ||
            year < ReleaseYearMinimum ||
            !int.TryParse(parts[1], out _) ||
            !int.TryParse(parts[2], out _))
        {
            return false;
        }

        displayVersion = candidate;
        return true;
    }
}
