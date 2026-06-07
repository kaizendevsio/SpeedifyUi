namespace XNetwork.Models;

public sealed record ChangelogEntry
{
    public required string Version { get; init; }

    public required string Date { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
}

public static class AppChangelog
{
    public const string CurrentVersion = "2026.06.3";

    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
        new ChangelogEntry
        {
            Version = CurrentVersion,
            Date = "2026-06-07",
            Summary = "Polished Starlink details layout and actions.",
            Changes =
            [
                "Combined duplicate Starlink local app and diagnostics links when they point to the same URL.",
                "Collapsed the Starlink live summary into a compact status strip.",
                "Reduced the visual weight of Starlink action rows and refined slide-to-confirm controls."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.2",
            Date = "2026-06-07",
            Summary = "Simplified Starlink actions with slide confirmation.",
            Changes =
            [
                "Replaced Starlink command confirmation popups with inline slide-to-confirm controls.",
                "Simplified the Starlink action list by removing repeated status badges and oversized command buttons.",
                "Kept reset obstruction map guarded as a deliberate destructive action without requiring typed text."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.1",
            Date = "2026-06-07",
            Summary = "Starlink maintenance controls and date-based app versioning.",
            Changes =
            [
                "Added a direct Starlink Mini reset obstruction map action with typed confirmation.",
                "Added a date-based version badge and changelog viewer.",
                "Changed deploy versioning from a plain counter to YYYY.MM.N."
            ]
        }
    ];
}
