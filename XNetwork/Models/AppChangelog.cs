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
    public const string CurrentVersion = "2026.06.8";

    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
        new ChangelogEntry
        {
            Version = CurrentVersion,
            Date = "2026-06-08",
            Summary = "Refined actively redundant adapter detection.",
            Changes =
            [
                "Uses current nonzero tunnel throughput as the primary signal for Actively Redundant membership.",
                "Selects adapters carrying the dominant current traffic direction before falling back to total tunnel traffic.",
                "Prevents idle connected adapters with zero current throughput from filling the active redundant group.",
                "Batches Speedify stats renders so dashboard adapter speed animations do not flicker on partial stat-row updates."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.7",
            Date = "2026-06-08",
            Summary = "Corrected active redundant adapter grouping.",
            Changes =
            [
                "Changed the dashboard group title to Actively Redundant and removed the group subtitle.",
                "Detects active redundant adapters from live non-proxy tunnel traffic instead of connected Speedify rows.",
                "Caps the active group by the Speedify max redundant setting so standby adapters stay outside the group."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.6",
            Date = "2026-06-07",
            Summary = "Made redundant adapter membership visible on the dashboard.",
            Changes =
            [
                "Added a dedicated Redundant group section that lists the adapters currently carrying protected Speedify tunnel traffic.",
                "Separated connected or connecting adapters that are not currently selected for redundant traffic into an Other adapters section.",
                "Kept the group membership based on live Speedify tunnel rows, so it follows actual adapter usage instead of adapter names or USB positions."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.5",
            Date = "2026-06-07",
            Summary = "Refined dashboard adapter status, sorting, charts, and PWA theme.",
            Changes =
            [
                "Added inset spacing to Starlink action sliders so the handle no longer touches the track edge.",
                "Removed x-axis labels from dashboard detail charts, Starlink charts, and uptime charts.",
                "In redundant mode, groups adapters actively used by live Speedify tunnel rows at the top of the dashboard list.",
                "Removed the visible traffic-breakdown hint from the connection summary while keeping the card action available.",
                "Changed dashboard adapter status pills into compact status dots with hover/tap popovers and a connecting spinner ring.",
                "Updated the PWA theme color to match the app background and refreshed the service-worker cache."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.4",
            Date = "2026-06-07",
            Summary = "Cleaned up Starlink details, charts, alerts, and controls.",
            Changes =
            [
                "Removed the compact Starlink live status strip so telemetry cards start the details view.",
                "Changed Starlink detail graphs to use the same Chart.js visual style as the Analytics page.",
                "Mapped Starlink alert 19 to a friendly obstruction-map-reset message with an explanation.",
                "Changed slide-to-confirm so only dragging the left handle can advance an action.",
                "Hides stow and unstow controls when Starlink telemetry reports that the terminal has no actuators."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.3",
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
