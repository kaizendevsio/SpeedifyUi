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
    public const string CurrentVersion = "xbond-2026.06.48";

    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
        new ChangelogEntry
        {
            Version = CurrentVersion,
            Date = "2026-06-18",
            Summary = "Fixed Cudy LAN management compatibility.",
            Changes =
            [
                "Cudy management now tolerates malformed HTTP header lines returned by the TR3000 firmware during LuCI login.",
                "The Wifi page and Cudy automation can use the LAN-side Cudy gateway without failing on firmware-generated invalid headers."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.47",
            Date = "2026-06-18",
            Summary = "Restored Cudy management settings.",
            Changes =
            [
                "Settings now has a Cudy Management card for editing the Cudy admin URL, updating the local runtime password, scanning trusted home Wi-Fi, and testing Cudy login.",
                "Saving Cudy settings now attempts to refresh the local XBond bypass route immediately so changed management URLs do not have to wait for an XBond restart.",
                "The Wifi page now reports which configured Cudy management URL failed when the client table times out.",
                "Cudy configuration checks now require an actual configured password instead of treating a default environment-variable name as enough."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.46",
            Date = "2026-06-18",
            Summary = "Compacted the mobile bottom navigation.",
            Changes =
            [
                "The mobile tab bar now starts compact with icon-only tabs, fixes the five-tab centering bug, and floats higher above the bottom safe area.",
                "Scrolling up or interacting with the page temporarily expands labels and normal icon sizing; scrolling down or idling compacts the bar again."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.45",
            Date = "2026-06-17",
            Summary = "Bypassed XBond for local Cudy management routes.",
            Changes =
            [
                "XBond route setup now reads the configured Cudy management URL and pins that local management host to a physical route before making xbond0 the default route.",
                "Stale local management bypass routes are cleaned up across XBond client restarts.",
                "The paired XBond deploy script now installs the route helper scripts and verifies that the configured Cudy host does not route through xbond0."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.44",
            Date = "2026-06-17",
            Summary = "Restored Analytics styling and unblocked Wifi loading.",
            Changes =
            [
                "The Analytics page now uses the restored dashboard-era card, chart, legend, and refresh rhythm while still reading XBond snapshots only.",
                "The Wifi page now renders immediately instead of waiting for a slow Cudy management login before first paint.",
                "Cudy client refreshes now fail visibly after a bounded timeout and clear the warning once a refresh succeeds."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.43",
            Date = "2026-06-17",
            Summary = "Hardened network monitor sysfs reads.",
            Changes =
            [
                "The background network monitor now treats transient Linux sysfs carrier-read failures as unknown state instead of logging an exception every second.",
                "This keeps dashboard monitoring quieter when USB network adapters appear, disappear, or expose carrier files that cannot be read at that instant."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.42",
            Date = "2026-06-17",
            Summary = "Restored the dashboard visual rhythm for XBond.",
            Changes =
            [
                "The XBond dashboard now reuses the previous dashboard summary card, animated numbers, skeleton loading states, and adapter-list animation patterns.",
                "Adapter cards return to the compact dashboard typography with signal bars, status dots, hover/tap popovers, and separate animated down/up/latency/loss values.",
                "The dashboard remains XBond-only; no legacy tunnel runtime polling or controls were reintroduced."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.41",
            Date = "2026-06-17",
            Summary = "Added adaptive XBond recovery reorder hold.",
            Changes =
            [
                "The XBond server now starts recovery ingress reordering at a lower hold and grows toward the reliability maximum only when reorder or repair pressure continues.",
                "Recovery exit resets the server ingress reorder hold back to normal immediately.",
                "Operator status now reports the adaptive hold range, calm sample count, and last hold adjustment reason."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.40",
            Date = "2026-06-17",
            Summary = "Added XBond recovery packet repair.",
            Changes =
            [
                "Recovery mode now requests targeted packet repair for reorder gaps before the normal timeout releases them.",
                "Client and server keep short resend caches and answer repair requests with encrypted repair frames on current live paths.",
                "Operator status now includes repair request, repair delivery, cache miss, queue drop, and late repair counters."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.39",
            Date = "2026-06-17",
            Summary = "Reduced XBond polling and dataplane overhead.",
            Changes =
            [
                "Dashboard, analytics, health, and diagnostics now share a short-lived XBond snapshot cache instead of independently reading runtime status.",
                "Interface provider enrichment now caches slower modem gateway probes separately and probes gateways in parallel with shorter timeouts.",
                "XBond reorder release timing now uses local monotonic receive deadlines instead of peer timestamps, and sender workers reuse per-path encode buffers.",
                "Recovery duplicate scheduling now prunes harmful backup paths while still keeping one usable backup duplicate when available."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.38",
            Date = "2026-06-16",
            Summary = "Improved XBond recovery reorder behavior.",
            Changes =
            [
                "XBond clients now tell the server when recovery mode is active so the server can use a longer ingress reorder hold during degraded all-path recovery.",
                "The server now writes operator-only ingress reorder counters to a status artifact for intermittent-path collapse captures.",
                "Recovery schedule updates now keep duplicate path membership stable across minor score churn unless a path is hard-demoted."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.37",
            Date = "2026-06-16",
            Summary = "Added XBond full-redundant recovery mode.",
            Changes =
            [
                "Balanced and Reliable policies now enter recovery when every usable live path is degraded.",
                "Recovery mode duplicates all traffic, including bulk TCP, across every usable path until a clean path stays healthy for the exit window.",
                "Runtime status now reports whether recovery is active, why it entered or exited, and which paths are eligible."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.36",
            Date = "2026-06-16",
            Summary = "Aligned XBond route checks with the dashboard latency target.",
            Changes =
            [
                "XBond speed test route verification now checks the route to Google DNS at 8.8.8.8.",
                "Hidden operator scoped-route defaults also use 8.8.8.8 so app diagnostics no longer keep a stale Cloudflare target."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.35",
            Date = "2026-06-16",
            Summary = "Tightened XBond operator tooling boundaries.",
            Changes =
            [
                "Operator performance speed test deployments now have a paired deploy script for app, client, and server runtime changes.",
                "Normal Settings no longer exposes the persisted Diagnostic policy or scoped route diagnostics.",
                "Backend diagnostic overrides use an absolute XBond client path when running through sudo."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.34",
            Date = "2026-06-16",
            Summary = "Fixed operator diagnostic override permissions.",
            Changes =
            [
                "Operator performance speed test diagnostics now call XBond override commands through non-interactive sudo when configured, matching the root-owned control socket.",
                "This lets backend diagnostics use live overrides without making /run/xbond/client-control.sock world-writable.",
                "The user-facing /xbond page remains limited to runtime status and the manual speed test."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.33",
            Date = "2026-06-16",
            Summary = "Added the Wifi route alias.",
            Changes =
            [
                "The Wifi page now responds on both /xrouter and /wifi so operator route checks and navigation use the same user-facing name.",
                "The manual XBond speed test remains the only user-facing diagnostic action on /xbond.",
                "This is an app-only route fix; XBond runtime binaries remain on the matching deployed scheduler and diagnostics build."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.32",
            Date = "2026-06-16",
            Summary = "Improved XBond performance diagnostics and scheduling.",
            Changes =
            [
                "Operator performance speed test diagnostics can temporarily override the live XBond mode without rewriting config or restarting the tunnel.",
                "The XBond scheduler now uses hysteresis so a better path must stay better before replacing the current anchor, while hard-demoted paths are replaced immediately.",
                "Client and server packet sends avoid an extra payload clone before encryption on the hot path."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.31",
            Date = "2026-06-15",
            Summary = "Removed remaining XBond page controls.",
            Changes =
            [
                "The XBond page is now read-only except for the manual tunnel speed test.",
                "Service start, stop, boot, and deep diagnostic controls remain operator tooling instead of normal UI."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.30",
            Date = "2026-06-15",
            Summary = "Simplified the XBond diagnostics page.",
            Changes =
            [
                "The XBond page now exposes the manual tunnel speed test as the only user-facing diagnostic action.",
                "Heartbeat, multi-path, bad-backup simulation, performance matrix, MTU sweep, and scoped-route controls remain operator tooling instead of normal UI."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.29",
            Date = "2026-06-15",
            Summary = "Polished XBond native iperf diagnostic messages.",
            Changes =
            [
                "Native adapter iperf errors now show the concise iperf error instead of a raw JSON body.",
                "Performance Matrix diagnostics still explain that native adapter tests need a separate public/native iperf endpoint."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.28",
            Date = "2026-06-15",
            Summary = "Fixed XBond performance matrix artifact and native endpoint reporting.",
            Changes =
            [
                "Performance Matrix artifacts now fall back to a writable local diagnostics directory if /var/lib/xnetwork is not writable.",
                "Deployments now create /var/lib/xnetwork/diagnostics for the XNetwork service user when privileges allow.",
                "Native adapter iperf tests now report missing public/native iperf endpoints directly instead of waiting on long generic timeouts."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.27",
            Date = "2026-06-15",
            Summary = "Added XBond performance matrix diagnostics.",
            Changes =
            [
                "XBond diagnostics can now run native adapter, anchor-only, duplicate, FEC, and public speed tests from one matrix.",
                "Runtime status now exposes reorder counters, path demotion reasons, stale ACK age, send failures, duplicate usefulness, throughput collapse, and process memory.",
                "Added an MTU sweep diagnostic with a recommended MTU/MSS result."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.26",
            Date = "2026-06-15",
            Summary = "Changed dashboard latency probe target.",
            Changes =
            [
                "Dashboard connection health now probes Google DNS at 8.8.8.8 instead of Cloudflare DNS at 1.1.1.1.",
                "XBond route diagnostics keep their separate target control unchanged."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.25",
            Date = "2026-06-15",
            Summary = "Added adaptive XBond redundancy and degraded-path diagnostics.",
            Changes =
            [
                "XBond now supports redundancy policies so healthy bulk traffic can avoid unnecessary duplicate sends while small or lossy traffic stays protected.",
                "Client and server tunnel receive paths now use a bounded reorder buffer before writing packets to the tunnel device.",
                "Settings exposes XBond policy thresholds, and the XBond diagnostics page can run a bounded bad-backup simulation."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.24",
            Date = "2026-06-15",
            Summary = "Fixed XBond return scheduling and duplicate receive visibility.",
            Changes =
            [
                "XBond clients now send their live schedule to the server so return traffic follows the current anchor and backup paths.",
                "Per-adapter Down values now include duplicate receive load, while the connection summary still shows useful tunnel download.",
                "Path cards show duplicate downlink details when duplicate return traffic is present."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.23",
            Date = "2026-06-15",
            Summary = "Clarified dashboard path throughput labels.",
            Changes =
            [
                "Dashboard adapter cards now label per-path XBond throughput as Down and Up.",
                "Keeps the split XBond path throughput and dual server/public speed test from the prior revision."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.22",
            Date = "2026-06-15",
            Summary = "Split XBond path throughput and added server speed testing.",
            Changes =
            [
                "Dashboard and analytics now show per-path download and upload separately.",
                "XBond runtime status now reports per-path inbound and outbound throughput.",
                "Tunnel Speed Test now runs Pi to Vultr iperf throughput before the public speedtest."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.21",
            Date = "2026-06-15",
            Summary = "Added XBond tunnel speed testing.",
            Changes =
            [
                "Adds a manual Tunnel Speed Test button to the XBond page.",
                "Runs speedtest-cli through the current default route and reports download, upload, and ping.",
                "Shows whether the default route was using xbond0 when the test started."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.20",
            Date = "2026-06-15",
            Summary = "Made XBond heartbeat diagnostics route-aware.",
            Changes =
            [
                "The Heartbeat diagnostic now uses the same route-aware XBond multi-ping verifier as the multi-path diagnostic.",
                "This avoids false packet-loss reports from the older single-socket ping diagnostic while the live XBond tunnel is running.",
                "Heartbeat results still show ACK count and RTT for the selected live XBond path."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.19",
            Date = "2026-06-15",
            Summary = "Added XBond adapter selection in Settings.",
            Changes =
            [
                "Settings can now show connected ethernet and Wi-Fi adapters that are available for XBond.",
                "Adapters can be added to or removed from the XBond tunnel without manually editing client.toml.",
                "Saving adapter membership rewrites the XBond client config and restarts the XBond client service."
            ]
        },
        new ChangelogEntry
        {
            Version = "xbond-2026.06.18",
            Date = "2026-06-15",
            Summary = "XBond-only runtime branch.",
            Changes =
            [
                "Removes the legacy tunnel runtime services and makes XBond the only traffic engine in this branch.",
                "Dashboard and analytics now read XBond runtime status and path telemetry.",
                "Renames tunnel controls and diagnostics to XBond active mode."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.16",
            Date = "2026-06-14",
            Summary = "Made the XBond tunnel bidirectional.",
            Changes =
            [
                "The XBond client now writes return packets from the server back into the client TUN.",
                "The XBond server now reads its TUN and sends return packets back to the latest known client path peers.",
                "This enables a real XBond TUN ping test without changing the router default route."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.15",
            Date = "2026-06-14",
            Summary = "Added XBond FEC recovery.",
            Changes =
            [
                "Added XOR parity FEC blocks for XBond AnchorFec traffic.",
                "The XBond server can recover one missing packet from each two-packet parity block when the paired data packet and parity arrive.",
                "Keeps FEC in the XBond tunnel path only; production routing is still disabled by default."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.14",
            Date = "2026-06-14",
            Summary = "Added XBond traffic-engine controls.",
            Changes =
            [
                "Added a persisted XBond traffic-engine mode for staged tunnel testing.",
                "Added guarded XBond service controls that stay locked unless explicitly enabled in configuration.",
                "Locks XBond primary mode behind a separate configuration flag so production routing cannot change by accident.",
                "Changed multi-path XBond probes to use bind-device physical path isolation instead of the legacy tunnel bypass by default."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.13",
            Date = "2026-06-14",
            Summary = "Added XBond multi-path shadow probing.",
            Changes =
            [
                "Added an XBond multi-path probe command that duplicates heartbeat packets across configured paths.",
                "Shows first-arrival, ACK, packet loss, and route-verification details for each XBond path.",
                "Added structured XBond server packet events for first arrivals and duplicate or late drops."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.12",
            Date = "2026-06-14",
            Summary = "Stabilized the XBond public lab test.",
            Changes =
            [
                "Added a short settle window after creating the temporary legacy tunnel bypass before sending XBond heartbeat packets.",
                "Keeps the public lab test cleanup behavior unchanged after the run completes."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.11",
            Date = "2026-06-14",
            Summary = "Added XBond public heartbeat lab controls.",
            Changes =
            [
                "Added an XBond Lab public test button that temporarily bypasses UDP 8444 through legacy tunnel.",
                "Shows XBond heartbeat packet loss, RTT, client bind address, and bypass cleanup status in the dashboard.",
                "Keeps XBond in shadow-test mode without routing production traffic."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.10",
            Date = "2026-06-14",
            Summary = "Added disabled XBond live-test deployment support.",
            Changes =
            [
                "Added an XBond client heartbeat ping command for sealed UDP server/client smoke tests.",
                "Fixed XBond duplicate detection so separate sessions can reuse packet sequence numbers safely.",
                "Prepared XBond for disabled deployment on the router and private server without routing production traffic."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.9",
            Date = "2026-06-13",
            Summary = "Added XBond prototype planning and observability.",
            Changes =
            [
                "Saved the XBond Rust dataplane and Blazor control-plane implementation plan in the repository.",
                "Added a Rust XBond workspace with protocol framing, duplicate detection, path-health scoring, and scheduler foundations.",
                "Added a read-only XBond page in XNetwork for prototype status, anchor path, packet counters, and path roles."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.8",
            Date = "2026-06-08",
            Summary = "Refined actively redundant adapter detection.",
            Changes =
            [
                "Uses current nonzero tunnel throughput as the primary signal for Actively Redundant membership.",
                "Selects adapters carrying the dominant current traffic direction before falling back to total tunnel traffic.",
                "Prevents idle connected adapters with zero current throughput from filling the active redundant group.",
                "Batches legacy tunnel stats renders so dashboard adapter speed animations do not flicker on partial stat-row updates."
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
                "Detects active redundant adapters from live non-proxy tunnel traffic instead of connected legacy tunnel rows.",
                "Caps the active group by the legacy tunnel max redundant setting so standby adapters stay outside the group."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.6",
            Date = "2026-06-07",
            Summary = "Made redundant adapter membership visible on the dashboard.",
            Changes =
            [
                "Added a dedicated Redundant group section that lists the adapters currently carrying protected legacy tunnel tunnel traffic.",
                "Separated connected or connecting adapters that are not currently selected for redundant traffic into an Other adapters section.",
                "Kept the group membership based on live legacy tunnel tunnel rows, so it follows actual adapter usage instead of adapter names or USB positions."
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
                "In redundant mode, groups adapters actively used by live legacy tunnel tunnel rows at the top of the dashboard list.",
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
