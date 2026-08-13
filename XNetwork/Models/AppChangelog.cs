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
    public const string CurrentVersion = "ulink-2026.06.123";

    public static IReadOnlyList<ChangelogEntry> Entries { get; } =
    [
        new ChangelogEntry
        {
            Version = CurrentVersion,
            Date = "2026-08-13",
            Summary = "Opened adapter details for every adapter and named them from their live provider.",
            Changes =
            [
                "Bumped the uLink interface version for per-adapter details and provider naming.",
                "Opened a details sheet from every adapter card with live latency, loss, download, and upload graphs.",
                "Added upstream details per adapter, including public IP, provider, autonomous system, and location.",
                "Named adapters from the provider detected through each adapter's own connection, while manual display names still win.",
                "Added a Link Watchdog setting to turn provider lookups off, and a per-adapter refresh for a fresh lookup."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.122",
            Date = "2026-08-13",
            Summary = "Made Starlink USB interface changes recover without restarting uLink.",
            Changes =
            [
                "Bumped the uLink interface version for automatic Starlink path recovery.",
                "Verified the real Starlink adapter through its interface-bound management endpoint instead of trusting stale interface names.",
                "Hot-rebound only the Starlink path socket after USB re-enumeration while preserving the rest of the tunnel.",
                "Persisted the verified replacement interface for subsequent boots without restarting the uLink client."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.121",
            Date = "2026-08-13",
            Summary = "Made Link Watchdog adapter configuration manageable.",
            Changes =
            [
                "Bumped the uLink interface version for manageable Link Watchdog settings.",
                "Added adapter selection, editing, and confirmed removal to Link Watchdog.",
                "Applied saved Link Watchdog changes immediately to the running monitor service.",
                "Added persistent display-only adapter aliases while preserving Linux interface identities."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.119",
            Date = "2026-07-24",
            Summary = "Hardened the high-throughput uLink dataplane.",
            Changes =
            [
                "Bumped the uLink interface version for the high-throughput dataplane hardening release.",
                "Added bounded UDP receive batching and larger effective socket buffers for sustained packet rates.",
                "Added pressure-aware duplicate and FEC suppression plus a clean reconnect when hard queue pressure persists.",
                "Expanded high-throughput telemetry for effective buffers, queue pressure and age, kernel errors, saturation, and dataplane stage timings.",
                "Expanded bounded load validation across parallel streams, concurrent clients, fixed-rate UDP, and protocol-scale sessions."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.118",
            Date = "2026-07-22",
            Summary = "Fixed tunnel scheduler path collapse scoring.",
            Changes =
            [
                "Bumped the uLink interface version for the tunnel scheduler scoring fix.",
                "Stopped idle standby paths from being penalized as throughput-collapsed just because they are not currently scheduled for payload traffic.",
                "Changed path collapse scoring to use recent payload traffic opportunities instead of an all-time per-process peak, while still penalizing active paths that genuinely collapse under offered load."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.117",
            Date = "2026-07-22",
            Summary = "Smoothed dashboard card spacing and app chrome.",
            Changes =
            [
                "Bumped the uLink interface version for the dashboard spacing and chrome polish update.",
                "Collapsed the connection health status badge row to zero height when no badges are visible.",
                "Added a reusable reduced-motion-safe collapsible row pattern for dynamic uLink card content.",
                "Aligned the mobile app header with the page surface and changed the changelog button to a familiar information icon."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.116",
            Date = "2026-07-21",
            Summary = "Raised per-path heartbeat frequency.",
            Changes =
            [
                "Bumped the uLink interface version for high-frequency path heartbeat telemetry.",
                "Changed per-path heartbeats to run every 200 ms while keeping scheduler, role, status, and aggregate tunnel health updates on their one-second cadence.",
                "Added configurable 100-sample per-path heartbeat windows with warmup handling, four-miss hard demotion, and 15-success recovery.",
                "Updated dashboard and analytics path loss displays so warming paths show unknown quality instead of misleading early loss."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.115",
            Date = "2026-07-21",
            Summary = "Corrected compact status badge animation geometry.",
            Changes =
            [
                "Bumped the uLink interface version for the status badge geometry correction.",
                "Made newly appearing status badges render as complete centered circles during their icon-only hold.",
                "Kept icons stationary while badges expand smoothly to reveal their labels after one second.",
                "Improved independent badge wrapping and reduced-motion behavior on narrow mobile screens."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.114",
            Date = "2026-07-21",
            Summary = "Refined release labels and dashboard appearance controls.",
            Changes =
            [
                "Changed compact version badges to show the date-based release number without the product prefix.",
                "Added a persisted Appearance setting for showing the dashboard connection mode, defaulting to hidden.",
                "Kept recovery, loss protection, and server health status pills independent from the mode preference."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.113",
            Date = "2026-07-21",
            Summary = "Improved Starlink management access and page recovery.",
            Changes =
            [
                "Added dynamic LAN forwarding to the Starlink management endpoint through the currently detected physical adapter.",
                "Added automatic cleanup and reapplication when Starlink disconnects or re-enumerates on USB.",
                "Replaced the stock Blazor error banner with bounded automatic page recovery and a minimalist uLink fallback."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.112",
            Date = "2026-07-21",
            Summary = "Finished the visible uLink naming cleanup.",
            Changes =
            [
                "Removed legacy implementation names from rendered historical release notes.",
                "Kept compatibility routes, service names, status files, and protocol identifiers unchanged internally.",
                "Expanded the branding audit so future changelog entries cannot expose the retired product name."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.111",
            Date = "2026-07-21",
            Summary = "Completed the uLink product naming transition.",
            Changes =
            [
                "Changed the visible version prefix to ulink and removed remaining legacy product names from the current interface.",
                "Updated dashboard, adapter cards, analytics, Wifi, Settings, diagnostics, accessibility text, and changelog branding to uLink.",
                "Kept internal tunnel services, protocol identifiers, configuration keys, and compatibility routes unchanged."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.110",
            Date = "2026-07-21",
            Summary = "Simplified navigation and refined connection status motion.",
            Changes =
            [
                "Removed the Live page and navigation item so Dashboard, Analytics, Wifi, and Settings remain the focused primary workflow.",
                "Added independent icon-first connection status pills that pause briefly before expanding and fade cleanly when removed.",
                "Added clearer adapter separation below the Actively Redundant group and standardized the user-facing brand casing as uLink."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.109",
            Date = "2026-07-21",
            Summary = "Refined dashboard grouping and Live signal nodes.",
            Changes =
            [
                "Restored a shared Actively Redundant surface while preserving keyed adapter movement between active and standby roles.",
                "Moved animated connection status pills below the summary and added a persisted Adapter technical details preference.",
                "Darkened dashboard tunnel charts, neutralized navigation states, and replaced Live adapter rings with animated three-dimensional WiFi signal nodes."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.108",
            Date = "2026-07-21",
            Summary = "Smoothed live adapter role changes.",
            Changes =
            [
                "Removed the redundant Adapters heading and live-count pill from the dashboard.",
                "Added keyed ease-out movement when adapter roles reorder without fading cards or throughput values.",
                "Animated dashboard and modem signal bars as their measured strength changes."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.107",
            Date = "2026-07-21",
            Summary = "Tightened the compact Live viewport.",
            Changes =
            [
                "Removed redundant in-scene labels from compact Live view so the topology and lower telemetry HUD remain visually separate."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.106",
            Date = "2026-07-21",
            Summary = "Polished Live topology labels.",
            Changes =
            [
                "Moved Live adapter labels clear of their energy gates and removed redundant scene text for a cleaner desktop topology."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.105",
            Date = "2026-07-21",
            Summary = "Introduced the uLink identity and a cleaner live topology.",
            Changes =
            [
                "Renamed the user-facing application to uLink and replaced the router artwork with a purpose-built vector logo across the header, favicon, and PWA metadata.",
                "Consolidated tunnel mode, protection, recovery, and actionable server health into the connection summary to reduce dashboard height.",
                "Rebuilt Live as an interactive abstract flow topology with energy gates, a linked uLink core, a secure relay aperture, and mobile-aware rendering.",
                "Adapter state colors now fade without layout movement, the top bar is flat, and changelog sheets animate both opening and closing."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.104",
            Date = "2026-07-21",
            Summary = "Simplified dashboard tunnel status.",
            Changes =
            [
                "Removed the duplicate recovery badge from the connection summary while keeping recovery state on the uLink tunnel card.",
                "Removed the redundant uLink active badge.",
                "Server health now stays hidden while healthy and appears only when degraded, down, or unknown."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.103",
            Date = "2026-07-20",
            Summary = "Tightened uLink runtime accounting and release validation.",
            Changes =
            [
                "Packet-pool and sender-lane telemetry now reports bounded live occupancy, generation-aware drops, and socket-rebind state without adding work to the packet hot path.",
                "Client and server repair-cache status now exposes authoritative retained entries and accounted bytes for memory and quiescence validation.",
                "The validation lab compares clean and impaired uLink runs fairly, rejects incomplete samples, and uses robust long-run memory evidence instead of a single noisy slope.",
                "Anchor impairment tests now require sustained scheduler state, healthy anchor evidence, degraded backups, and clean network-emulation cleanup."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.102",
            Date = "2026-07-20",
            Summary = "Hardened uLink performance, control paths, and automatic recovery.",
            Changes =
            [
                "Client and server packet pools are bounded and reusable to reduce allocation pressure without retaining excess memory.",
                "Server control lanes stay nonblocking under load while preserving distinct peer and session responses.",
                "Silent-blackhole detection uses robust interface-bound probes against the uLink endpoint and independent targets.",
                "The fail-closed watchdog persists restart safety state and exposes Settings recovery when durable state is unavailable.",
                "Soak validation now requires multiple complete throughput baselines and stricter cleanup, provenance, and growth checks."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.101",
            Date = "2026-07-20",
            Summary = "Hardened uLink recovery, session handling, and bounded packet queues.",
            Changes =
            [
                "Client and server now fail closed on saturated TUN batches instead of stalling the tunnel supervisor one packet at a time.",
                "Authenticated server restarts trigger a clean client session replacement, and replay protection now covers control and ACK frames.",
                "Repeated ineffective path rebinds escalate to a clean tunnel session restart, while harmful recovery paths are pruned unless they provide measurable duplicate value."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.100",
            Date = "2026-07-12",
            Summary = "Added guarded automatic recovery for a stalled uLink client.",
            Changes =
            [
                "A configurable watchdog now compares logical tunnel health with interface-bound probes from each configured physical uLink path.",
                "The client service restarts only after a sustained tunnel/physical mismatch, with cooldown, post-restart grace, and hourly restart limits.",
                "Settings now show watchdog evidence, direct path probe results, current mismatch count, and a manual check action."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.99",
            Date = "2026-07-03",
            Summary = "Moved XNetwork to a monochrome dark theme.",
            Changes =
            [
                "App chrome, cards, controls, modals, and the Live view now use neutral dark-gray surfaces with light-gray text.",
                "Dashboard sparklines are monochrome and distinguish tunnel, anchor, and backup paths with solid, dashed, and dotted strokes.",
                "Analytics charts now use a muted pastel palette while adapter and health states keep soft semantic colors."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.98",
            Date = "2026-07-03",
            Summary = "Tucked the dashboard sparkline behind the summary metrics.",
            Changes =
            [
                "The dashboard connection card sparkline now renders as a background layer instead of taking its own vertical space.",
                "The Tunnel, Internet, Download, and Upload metrics remain visible while the card height stays tighter on mobile.",
                "No tunnel routing, server health, or uLink runtime behavior changed."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.97",
            Date = "2026-07-03",
            Summary = "Added dashboard internet latency.",
            Changes =
            [
                "The dashboard connection card now shows uLink tunnel latency and internet latency as separate values.",
                "Internet latency reuses the existing Pi-side rolling ping to 8.8.8.8 through the current default route.",
                "The tunnel latency remains the uLink heartbeat RTT to Vultr and is still used for tunnel health status."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.96",
            Date = "2026-07-03",
            Summary = "Added Vultr server egress health telemetry.",
            Changes =
            [
                "uLink server now probes configurable external TCP targets from Vultr and reports server egress health without changing routing or QoS.",
                "The Pi client relays server health through the normal uLink runtime status so the dashboard can show Server OK, degraded, down, or unknown.",
                "Analytics now includes a server egress latency view to compare Vultr-side connectivity against tunnel RTT."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.95",
            Date = "2026-06-28",
            Summary = "Tuned Live satellite mobile framing.",
            Changes =
            [
                "Pulled the compact Live satellite relay farther into frame so its solar panels are visible on mobile.",
                "Scaled the compact satellite endpoint down slightly without changing the energy beams or adapter spacing.",
                "Kept the wider hardware-node spacing from the prior Live update."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.94",
            Date = "2026-06-28",
            Summary = "Improved Live hardware spacing and satellite endpoint.",
            Changes =
            [
                "Spread compact Live adapter hardware nodes farther apart so tower glyphs do not visually stack on top of each other.",
                "Replaced the server-side portal/gate visual with a satellite relay endpoint using solar panels, antenna detail, and signal rings.",
                "Updated the fallback Live canvas endpoint to use the same satellite relay concept."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.93",
            Date = "2026-06-28",
            Summary = "Changed Live nodes into network hardware glyphs.",
            Changes =
            [
                "Replaced planet-like Live adapter orbs with dish and cell-tower style 3D glyphs.",
                "Cellular/F50 provider paths render as tower silhouettes with pulsing signal arcs, while Wi-Fi/Starlink-like paths render as dish emitters.",
                "Kept the uLink energy beams, topology framing, and 3D pan/rotate camera behavior."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.92",
            Date = "2026-06-28",
            Summary = "Fixed Live mobile topology framing.",
            Changes =
            [
                "Pulled Live adapter nodes back inside the compact mobile viewport so local adapters, uLink core, and server endpoint are all visible by default.",
                "Kept the energy-topology geometry, role-colored beams, and 3D pan/rotate controls from the previous Live rebuild.",
                "Adjusted only the compact scene framing; desktop topology remains unchanged."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.91",
            Date = "2026-06-28",
            Summary = "Rebuilt Live as an energy topology.",
            Changes =
            [
                "Replaced the large globe-like Live core with smaller reactor rings and shield bands so the view no longer reads as a decorative orb.",
                "Reframed the Three.js scene around explicit local adapter nodes, the uLink core, the Vultr server endpoint, and stronger role-colored energy beams.",
                "Preserved 3D pan, zoom, rotate, and adapter tap selection while keeping the default mobile camera framed on the full topology."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.90",
            Date = "2026-06-28",
            Summary = "Raised Live adapter nodes above the HUD.",
            Changes =
            [
                "Moved compact Live adapter energy nodes upward so they are visible in the 3D scene instead of sitting behind the lower path chips.",
                "Increased the adapter energy seed size while keeping the non-blocky beam/shield visual style.",
                "Preserved the 3D cockpit topology and orbit/pan/zoom camera controls."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.89",
            Date = "2026-06-28",
            Summary = "Made Live adapter energy nodes visible.",
            Changes =
            [
                "Added bright energy seeds inside each Live path emitter so adapter nodes are visible in the 3D cockpit without using blocky geometry.",
                "Pulled the compact mobile adapter column further into the visible scene lane while preserving the left-adapter, center-core, right-server topology.",
                "Kept the orbit, zoom, pan, and tap-to-select controls."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.88",
            Date = "2026-06-28",
            Summary = "Fixed Live cockpit beam visibility.",
            Changes =
            [
                "Changed Live cockpit energy beams, path emitters, labels, and shield materials to render without depth hiding so active paths remain visible through the transparent core.",
                "Kept the adapter-left, uLink-core, server-right 3D cockpit layout and orbit/pan/zoom camera controls.",
                "Preserved the operational role colors for anchor, backup, standby, warning, and down paths."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.87",
            Date = "2026-06-28",
            Summary = "Improved Live cockpit adapter visibility.",
            Changes =
            [
                "Moved mobile Live adapter emitters into the visible cockpit lane so the local side reads clearly without panning.",
                "Strengthened active path energy beams from adapters to the uLink core and from the core to the server endpoint.",
                "Kept the existing 3D orbit, zoom, pan, and tap-to-select scene controls."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.86",
            Date = "2026-06-28",
            Summary = "Tuned Live cockpit mobile framing.",
            Changes =
            [
                "Adjusted the Live cockpit default mobile camera framing so local adapters, the uLink core, and the server endpoint are visible without panning first.",
                "Reduced the core shield size so the energy beams and endpoint layout read more clearly on narrow screens.",
                "Kept the 3D orbit, zoom, pan, and adapter selection controls from the previous Live cockpit update."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.85",
            Date = "2026-06-28",
            Summary = "Reworked Live into a 3D uLink cockpit.",
            Changes =
            [
                "Replaced the primitive Live tab geometry with a Three.js cockpit layout: local adapters on the left, uLink core in the center, and the Vultr server endpoint on the right.",
                "Live beams now use the operational color language: orange anchor, pink redundant backup, dim standby/probe, and red or amber degraded paths.",
                "Added orbit-style scene controls so drag rotates the camera, mouse wheel or pinch zooms, and Shift/right-drag or two-finger drag pans around the cockpit."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.84",
            Date = "2026-06-28",
            Summary = "Reworked Live into an energy-wave view.",
            Changes =
            [
                "Replaced the primitive Live tab geometry with an abstract Three.js energy visualization using glowing tunnel cores, flowing beams, wave rings, and particle packets.",
                "Path emitters now read as signal sources instead of toy adapter objects while keeping the existing uLink status, path selection, and metric bindings.",
                "The canvas fallback was updated to use the same energy-beam visual language."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.83",
            Date = "2026-06-28",
            Summary = "Added the animated uLink Live view.",
            Changes =
            [
                "Added a Live tab with an animated Three.js scene for uLink tunnel health, active paths, recovery, and loss-protection state.",
                "Live adapter nodes can be selected to inspect path latency, loss, and throughput without changing the operational dashboard.",
                "Primary route transitions now include the Live tab between Dashboard and Analytics.",
                "Tightened the mobile Live HUD metric sizing so values fit without truncation."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.81",
            Date = "2026-06-26",
            Summary = "Fixed F50 USB target resolution.",
            Changes =
            [
                "F50 modem recovery now resolves USB reset targets with Linux readlink -f before falling back to managed symlink resolution.",
                "This fixes automatic USB reset target detection for modem interfaces whose /sys/class/net device links are relative symlinks."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.80",
            Date = "2026-06-26",
            Summary = "Added F50 recovery observability.",
            Changes =
            [
                "F50 modem recovery now logs service start, scheduled/manual check start, and each modem recovery decision.",
                "The logs make it clear whether automation is rebinding a stale uLink path, resetting a modem, cooling down, or taking no action."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.79",
            Date = "2026-06-26",
            Summary = "Added F50 modem recovery and uLink hot rebind.",
            Changes =
            [
                "uLink client paths can now be manually hot-rebound through the client control socket after a USB modem re-enumerates.",
                "Settings now includes configurable F50 modem recovery checks with interval, cooldown, ping target, and USB reset controls.",
                "The background recovery worker can rebind a stale uLink path or USB-reset a failed modem, then skips the next check after a failed recovery attempt."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.78",
            Date = "2026-06-25",
            Summary = "Restored Starlink telemetry on uLink.",
            Changes =
            [
                "Starlink telemetry now resolves the matching uLink physical path dynamically instead of relying on hardcoded USB adapter IDs.",
                "Starlink web and gRPC requests are bound to the resolved Linux interface so dish telemetry can work while the default route points through the tunnel interface.",
                "Dashboard Starlink cards again show direct dish stats and open the restored Starlink details/actions sheet."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.77",
            Date = "2026-06-25",
            Summary = "Added transparent modem admin ports.",
            Changes =
            [
                "Local device proxies can now run in port mode so modem admin UIs stay mounted at / on dedicated ports.",
                "Smart, DITO, and GOMO modem defaults use ports 18081, 18082, and 18083 instead of fragile path rewriting.",
                "Dashboard adapter admin buttons open the matching configured modem admin page instead of uLink diagnostics."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.76",
            Date = "2026-06-25",
            Summary = "Fixed modem proxy browser redirects.",
            Changes =
            [
                "Local device proxy roots such as /smart/ no longer redirect back to themselves.",
                "F50 modem JavaScript redirects through variables such as tempUrl are now scoped under the configured proxy route.",
                "Browser-opened modem admin pages stay under /smart, /dito, or /gomo instead of escaping to app-root paths or blocked data pages."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.75",
            Date = "2026-06-25",
            Summary = "Added traffic bypass rules and fixed modem proxy paths.",
            Changes =
            [
                "Settings now includes configurable traffic bypass rules for destination IP/CIDR and port matches with Auto physical or selected-adapter egress.",
                "Traffic bypass rules are applied by a Linux route helper using nftables marks and per-rule policy routes outside uLink.",
                "Local device proxies now keep modem mobile redirects inside their configured route so pages such as /gomo/mobile.html do not escape to the app root.",
                "Remaining top bar, Settings modal, and action sheet surfaces now use the VS Code modern dark palette more consistently."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.74",
            Date = "2026-06-25",
            Summary = "Refined Cudy controls and XNetwork shell UI.",
            Changes =
            [
                "Wifi page now reads Cudy radio state and exposes manual 2.4 GHz / 5 GHz radio controls.",
                "Local device proxy add and edit fields now open in a modal instead of staying inline.",
                "Router Wi-Fi settings can select any NetworkManager Wi-Fi adapter, including USB Wi-Fi adapters.",
                "Primary navigation now keeps uLink diagnostics inside Settings and uses a four-item mobile tab bar.",
                "The dark theme now follows a VS Code modern dark palette."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.73",
            Date = "2026-06-25",
            Summary = "Fixed F50 telemetry firmware compatibility.",
            Changes =
            [
                "F50 telemetry still uses the direct modem API, but now includes the minimal AJAX headers required by the firmware to avoid none-secure API errors.",
                "Smart and Dito F50 modems can populate lean cellular generation and signal-bar badges when their API reports those fields."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.72",
            Date = "2026-06-25",
            Summary = "Added local device proxies and F50 signal badges.",
            Changes =
            [
                "Settings can add, edit, and delete configurable local proxy routes for modem and router admin pages.",
                "Local device proxy targets are forwarded without an extra XNetwork authentication layer so each device keeps its own login.",
                "Adapter cards can show lean F50 cellular generation and signal bars from direct modem telemetry."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.71",
            Date = "2026-06-20",
            Summary = "Hardened IPv4 interface binding.",
            Changes =
            [
                "uLink now refuses to bind a path to a route source IP unless that IP is actually assigned to the requested interface.",
                "Configured WAN interfaces are treated as unavailable until they have an IPv4 address, preventing IPv6-only or DHCP-stalled links from being counted as usable uLink paths."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.70",
            Date = "2026-06-20",
            Summary = "Fixed recovery duplicate pruning.",
            Changes =
            [
                "Recovery mode no longer excludes idle probe paths just because their collapse score is high while they are not carrying traffic.",
                "Active paths that are genuinely collapsing under load can still be pruned when healthier backup paths are available."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.69",
            Date = "2026-06-20",
            Summary = "Added active redundancy count.",
            Changes =
            [
                "The dashboard Actively Redundant header now shows the number of paths currently included in the live uLink schedule.",
                "The count uses the same runtime schedule-backed membership as the adapter cards, so it follows normal and recovery-mode schedule changes."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.68",
            Date = "2026-06-19",
            Summary = "Smoothed live chart left-edge exits.",
            Changes =
            [
                "Live Analytics charts now use a numeric scrolling x-window so the outgoing left edge slides out instead of disappearing after the update.",
                "The dashboard sparkline uses the same scrolling window behavior for consistent right-to-left motion.",
                "The service-worker cache was refreshed so browsers pick up the updated chart script."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.66",
            Date = "2026-06-19",
            Summary = "Fixed route path normalization on the router.",
            Changes =
            [
                "Fixed a Linux runtime recursion bug in route path normalization that could crash the dashboard service.",
                "Kept the tab-order-aware transition direction and smooth chart motion from the previous build."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.65",
            Date = "2026-06-19",
            Summary = "Smoothed tab direction and live charts.",
            Changes =
            [
                "Tab transitions now choose forward or back motion from the mobile navigation order while keeping the existing easing.",
                "Analytics charts now animate horizontally instead of drawing new points upward from the baseline.",
                "The dashboard summary sparkline now uses the same smooth right-to-left update motion.",
                "The service-worker cache was refreshed so mobile/PWA clients fetch the updated chart script."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.64",
            Date = "2026-06-19",
            Summary = "Moved the loss-protection badge to the tunnel card.",
            Changes =
            [
                "The Protecting from loss badge now appears beside the uLink mode label in the tunnel card.",
                "The connection summary card returns to showing only the current tunnel status, description, and metrics."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.63",
            Date = "2026-06-19",
            Summary = "Switched public speed tests to Ookla and surfaced loss protection.",
            Changes =
            [
                "Public speed tests now call the official Ookla Speedtest CLI and parse its JSON result format.",
                "The dashboard connection card can show a Protecting from loss badge when active path loss is being absorbed by stable uLink tunnel health.",
                "The uLink speed test panel now labels the public test as the official Ookla speedtest."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.62",
            Date = "2026-06-19",
            Summary = "Simplified dashboard motion and health tiers.",
            Changes =
            [
                "Removed the experimental rubber-card drag motion and route-direction override that could make tab transitions flicker.",
                "Dashboard tab transitions now use a simpler fade-slide with a stronger ease-out curve.",
                "Dashboard adapter cards no longer show verbose scheduler reason text, and stable low-latency tunnel health can now show Excellent."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.61",
            Date = "2026-06-19",
            Summary = "Refined dashboard and settings motion.",
            Changes =
            [
                "The dashboard uLink panel now hides the raw server endpoint and tunnel device while keeping policy and recovery context visible.",
                "Settings sections now open as focused launchers instead of showing every control inline, and the on-board Wi-Fi connector is restored.",
                "Dashboard cards, settings launchers, and page transitions now use softer rubber-style motion with tab-order-aware transition direction."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.60",
            Date = "2026-06-18",
            Summary = "Surfaced uLink recovery hold telemetry.",
            Changes =
            [
                "uLink server recovery and ingress reorder hold telemetry now flows into the router client status JSON.",
                "The dashboard connection card shows a Recovery badge with the current server hold time while recovery is active.",
                "Analytics now includes a Recovery Hold chart so adaptive hold changes can be reviewed over time."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.59",
            Date = "2026-06-18",
            Summary = "Removed path-derived connection health substitution.",
            Changes =
            [
                "Dashboard and Analytics connection-level health now require aggregate uLink tunnel heartbeat telemetry instead of substituting active physical-path RTT and loss.",
                "Analytics now labels the connection-level RTT and loss as tunnel metrics, and its connection chart series is labeled uLink Tunnel.",
                "Per-adapter cards and chart lines still show physical path RTT and loss for troubleshooting."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.58",
            Date = "2026-06-18",
            Summary = "Moved dashboard health to tunnel telemetry.",
            Changes =
            [
                "The dashboard top connection card now uses aggregate uLink tunnel heartbeat RTT and loss when available instead of worst active physical-path health.",
                "Adapter cards still show per-path RTT and loss so degraded backups remain visible without automatically downgrading the top tunnel status."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.57",
            Date = "2026-06-18",
            Summary = "Simplified dashboard throughput labels.",
            Changes =
            [
                "The dashboard summary labels are back to Download and Upload while keeping the tooltip that explains they represent useful tunnel payload."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.56",
            Date = "2026-06-18",
            Summary = "Clarified dashboard tunnel throughput.",
            Changes =
            [
                "The dashboard summary now keeps sub-Mbps precision so small tunnel traffic no longer rounds down to zero before formatting.",
                "The top connection card labels now say Tunnel down and Tunnel up to distinguish useful tunnel payload from per-adapter wire traffic."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.55",
            Date = "2026-06-18",
            Summary = "Clarified dashboard chart path colors.",
            Changes =
            [
                "The dashboard summary chart now uses orange for the active anchor path overlay.",
                "The backup path overlay is now a brighter pink so it is easier to distinguish from the main tunnel line."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.54",
            Date = "2026-06-18",
            Summary = "Smoothed adapter status color changes.",
            Changes =
            [
                "Dashboard adapter status dots now fade their color and glow when path state changes instead of switching instantly.",
                "The status dot button also transitions its background so connected, warning, and disconnected changes feel less abrupt."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.53",
            Date = "2026-06-18",
            Summary = "Fixed dashboard sparkline after tab navigation.",
            Changes =
            [
                "Dashboard summary charts now use per-instance chart IDs so delayed route-transition disposal cannot destroy the newly opened dashboard chart.",
                "The dashboard sparkline now includes faint anchor and backup download lines beside the main tunnel download line."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.52",
            Date = "2026-06-18",
            Summary = "Explained uLink policy modes in Settings.",
            Changes =
            [
                "The uLink policy selector now describes what Balanced, Reliable, and Fast modes do.",
                "The policy save hint now follows the selected mode instead of always describing Balanced mode."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.51",
            Date = "2026-06-18",
            Summary = "Fixed stale uLink modem sockets after adapter churn.",
            Changes =
            [
                "uLink interface-only paths now bind to the current IPv4 source address for that interface instead of relying on an unspecified source.",
                "Old uLink per-path sender and receiver tasks are stopped when path sockets are removed or recreated, preventing stale modem sockets from keeping adapters stuck at 100% loss."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.50",
            Date = "2026-06-18",
            Summary = "Stopped showing stale RTT as live latency.",
            Changes =
            [
                "Paths with stale ACKs or effectively 100% heartbeat loss now show unavailable latency instead of a frozen last-known RTT.",
                "Dashboard status dots now warn or disconnect high-loss uLink paths instead of presenting them as healthy connected paths."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.49",
            Date = "2026-06-18",
            Summary = "Hid the Cudy WAN handoff from adapter lists.",
            Changes =
            [
                "The Raspberry Pi eth0 Cudy WAN handoff no longer appears as a live uLink dashboard adapter.",
                "The live adapter count now reflects usable WAN paths instead of the local Cudy management/upstream link."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.48",
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
            Version = "ulink-2026.06.47",
            Date = "2026-06-18",
            Summary = "Restored Cudy management settings.",
            Changes =
            [
                "Settings now has a Cudy Management card for editing the Cudy admin URL, updating the local runtime password, scanning trusted home Wi-Fi, and testing Cudy login.",
                "Saving Cudy settings now attempts to refresh the local uLink bypass route immediately so changed management URLs do not have to wait for an uLink restart.",
                "The Wifi page now reports which configured Cudy management URL failed when the client table times out.",
                "Cudy configuration checks now require an actual configured password instead of treating a default environment-variable name as enough."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.46",
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
            Version = "ulink-2026.06.45",
            Date = "2026-06-17",
            Summary = "Bypassed uLink for local Cudy management routes.",
            Changes =
            [
                "uLink route setup now reads the configured Cudy management URL and pins that local management host to a physical route before making the tunnel interface the default route.",
                "Stale local management bypass routes are cleaned up across uLink client restarts.",
                "The paired uLink deploy script now installs the route helper scripts and verifies that the configured Cudy host does not route through the tunnel interface."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.44",
            Date = "2026-06-17",
            Summary = "Restored Analytics styling and unblocked Wifi loading.",
            Changes =
            [
                "The Analytics page now uses the restored dashboard-era card, chart, legend, and refresh rhythm while still reading uLink snapshots only.",
                "The Wifi page now renders immediately instead of waiting for a slow Cudy management login before first paint.",
                "Cudy client refreshes now fail visibly after a bounded timeout and clear the warning once a refresh succeeds."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.43",
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
            Version = "ulink-2026.06.42",
            Date = "2026-06-17",
            Summary = "Restored the dashboard visual rhythm for uLink.",
            Changes =
            [
                "The uLink dashboard now reuses the previous dashboard summary card, animated numbers, skeleton loading states, and adapter-list animation patterns.",
                "Adapter cards return to the compact dashboard typography with signal bars, status dots, hover/tap popovers, and separate animated down/up/latency/loss values.",
                "The dashboard remains uLink-only; no legacy tunnel runtime polling or controls were reintroduced."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.41",
            Date = "2026-06-17",
            Summary = "Added adaptive uLink recovery reorder hold.",
            Changes =
            [
                "The uLink server now starts recovery ingress reordering at a lower hold and grows toward the reliability maximum only when reorder or repair pressure continues.",
                "Recovery exit resets the server ingress reorder hold back to normal immediately.",
                "Operator status now reports the adaptive hold range, calm sample count, and last hold adjustment reason."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.40",
            Date = "2026-06-17",
            Summary = "Added uLink recovery packet repair.",
            Changes =
            [
                "Recovery mode now requests targeted packet repair for reorder gaps before the normal timeout releases them.",
                "Client and server keep short resend caches and answer repair requests with encrypted repair frames on current live paths.",
                "Operator status now includes repair request, repair delivery, cache miss, queue drop, and late repair counters."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.39",
            Date = "2026-06-17",
            Summary = "Reduced uLink polling and dataplane overhead.",
            Changes =
            [
                "Dashboard, analytics, health, and diagnostics now share a short-lived uLink snapshot cache instead of independently reading runtime status.",
                "Interface provider enrichment now caches slower modem gateway probes separately and probes gateways in parallel with shorter timeouts.",
                "uLink reorder release timing now uses local monotonic receive deadlines instead of peer timestamps, and sender workers reuse per-path encode buffers.",
                "Recovery duplicate scheduling now prunes harmful backup paths while still keeping one usable backup duplicate when available."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.38",
            Date = "2026-06-16",
            Summary = "Improved uLink recovery reorder behavior.",
            Changes =
            [
                "uLink clients now tell the server when recovery mode is active so the server can use a longer ingress reorder hold during degraded all-path recovery.",
                "The server now writes operator-only ingress reorder counters to a status artifact for intermittent-path collapse captures.",
                "Recovery schedule updates now keep duplicate path membership stable across minor score churn unless a path is hard-demoted."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.37",
            Date = "2026-06-16",
            Summary = "Added uLink full-redundant recovery mode.",
            Changes =
            [
                "Balanced and Reliable policies now enter recovery when every usable live path is degraded.",
                "Recovery mode duplicates all traffic, including bulk TCP, across every usable path until a clean path stays healthy for the exit window.",
                "Runtime status now reports whether recovery is active, why it entered or exited, and which paths are eligible."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.36",
            Date = "2026-06-16",
            Summary = "Aligned uLink route checks with the dashboard latency target.",
            Changes =
            [
                "uLink speed test route verification now checks the route to Google DNS at 8.8.8.8.",
                "Hidden operator scoped-route defaults also use 8.8.8.8 so app diagnostics no longer keep a stale Cloudflare target."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.35",
            Date = "2026-06-16",
            Summary = "Tightened uLink operator tooling boundaries.",
            Changes =
            [
                "Operator performance speed test deployments now have a paired deploy script for app, client, and server runtime changes.",
                "Normal Settings no longer exposes the persisted Diagnostic policy or scoped route diagnostics.",
                "Backend diagnostic overrides use an absolute uLink client path when running through sudo."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.34",
            Date = "2026-06-16",
            Summary = "Fixed operator diagnostic override permissions.",
            Changes =
            [
                "Operator performance speed test diagnostics now call uLink override commands through non-interactive sudo when configured, matching the root-owned control socket.",
                "This lets backend diagnostics use live overrides without making the runtime control socket world-writable.",
                "The user-facing diagnostics page remains limited to runtime status and the manual speed test."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.33",
            Date = "2026-06-16",
            Summary = "Added the Wifi route alias.",
            Changes =
            [
                "The Wifi page now responds on both /xrouter and /wifi so operator route checks and navigation use the same user-facing name.",
                "The manual uLink speed test remains the only user-facing action on the diagnostics page.",
                "This is an app-only route fix; uLink runtime binaries remain on the matching deployed scheduler and diagnostics build."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.32",
            Date = "2026-06-16",
            Summary = "Improved uLink performance diagnostics and scheduling.",
            Changes =
            [
                "Operator performance speed test diagnostics can temporarily override the live uLink mode without rewriting config or restarting the tunnel.",
                "The uLink scheduler now uses hysteresis so a better path must stay better before replacing the current anchor, while hard-demoted paths are replaced immediately.",
                "Client and server packet sends avoid an extra payload clone before encryption on the hot path."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.31",
            Date = "2026-06-15",
            Summary = "Removed remaining uLink page controls.",
            Changes =
            [
                "The uLink page is now read-only except for the manual tunnel speed test.",
                "Service start, stop, boot, and deep diagnostic controls remain operator tooling instead of normal UI."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.30",
            Date = "2026-06-15",
            Summary = "Simplified the uLink diagnostics page.",
            Changes =
            [
                "The uLink page now exposes the manual tunnel speed test as the only user-facing diagnostic action.",
                "Heartbeat, multi-path, bad-backup simulation, performance matrix, MTU sweep, and scoped-route controls remain operator tooling instead of normal UI."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.29",
            Date = "2026-06-15",
            Summary = "Polished uLink native iperf diagnostic messages.",
            Changes =
            [
                "Native adapter iperf errors now show the concise iperf error instead of a raw JSON body.",
                "Performance Matrix diagnostics still explain that native adapter tests need a separate public/native iperf endpoint."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.28",
            Date = "2026-06-15",
            Summary = "Fixed uLink performance matrix artifact and native endpoint reporting.",
            Changes =
            [
                "Performance Matrix artifacts now fall back to a writable local diagnostics directory if /var/lib/xnetwork is not writable.",
                "Deployments now create /var/lib/xnetwork/diagnostics for the XNetwork service user when privileges allow.",
                "Native adapter iperf tests now report missing public/native iperf endpoints directly instead of waiting on long generic timeouts."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.27",
            Date = "2026-06-15",
            Summary = "Added uLink performance matrix diagnostics.",
            Changes =
            [
                "uLink diagnostics can now run native adapter, anchor-only, duplicate, FEC, and public speed tests from one matrix.",
                "Runtime status now exposes reorder counters, path demotion reasons, stale ACK age, send failures, duplicate usefulness, throughput collapse, and process memory.",
                "Added an MTU sweep diagnostic with a recommended MTU/MSS result."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.26",
            Date = "2026-06-15",
            Summary = "Changed dashboard latency probe target.",
            Changes =
            [
                "Dashboard connection health now probes Google DNS at 8.8.8.8 instead of Cloudflare DNS at 1.1.1.1.",
                "uLink route diagnostics keep their separate target control unchanged."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.25",
            Date = "2026-06-15",
            Summary = "Added adaptive uLink redundancy and degraded-path diagnostics.",
            Changes =
            [
                "uLink now supports redundancy policies so healthy bulk traffic can avoid unnecessary duplicate sends while small or lossy traffic stays protected.",
                "Client and server tunnel receive paths now use a bounded reorder buffer before writing packets to the tunnel device.",
                "Settings exposes uLink policy thresholds, and the uLink diagnostics page can run a bounded bad-backup simulation."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.24",
            Date = "2026-06-15",
            Summary = "Fixed uLink return scheduling and duplicate receive visibility.",
            Changes =
            [
                "uLink clients now send their live schedule to the server so return traffic follows the current anchor and backup paths.",
                "Per-adapter Down values now include duplicate receive load, while the connection summary still shows useful tunnel download.",
                "Path cards show duplicate downlink details when duplicate return traffic is present."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.23",
            Date = "2026-06-15",
            Summary = "Clarified dashboard path throughput labels.",
            Changes =
            [
                "Dashboard adapter cards now label per-path uLink throughput as Down and Up.",
                "Keeps the split uLink path throughput and dual server/public speed test from the prior revision."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.22",
            Date = "2026-06-15",
            Summary = "Split uLink path throughput and added server speed testing.",
            Changes =
            [
                "Dashboard and analytics now show per-path download and upload separately.",
                "uLink runtime status now reports per-path inbound and outbound throughput.",
                "Tunnel Speed Test now runs Pi to Vultr iperf throughput before the public speedtest."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.21",
            Date = "2026-06-15",
            Summary = "Added uLink tunnel speed testing.",
            Changes =
            [
                "Adds a manual Tunnel Speed Test button to the uLink page.",
                "Runs speedtest-cli through the current default route and reports download, upload, and ping.",
                "Shows whether the default route was using the tunnel interface when the test started."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.20",
            Date = "2026-06-15",
            Summary = "Made uLink heartbeat diagnostics route-aware.",
            Changes =
            [
                "The Heartbeat diagnostic now uses the same route-aware uLink multi-ping verifier as the multi-path diagnostic.",
                "This avoids false packet-loss reports from the older single-socket ping diagnostic while the live uLink tunnel is running.",
                "Heartbeat results still show ACK count and RTT for the selected live uLink path."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.19",
            Date = "2026-06-15",
            Summary = "Added uLink adapter selection in Settings.",
            Changes =
            [
                "Settings can now show connected ethernet and Wi-Fi adapters that are available for uLink.",
                "Adapters can be added to or removed from the uLink tunnel without manually editing client.toml.",
                "Saving adapter membership rewrites the uLink client config and restarts the uLink client service."
            ]
        },
        new ChangelogEntry
        {
            Version = "ulink-2026.06.18",
            Date = "2026-06-15",
            Summary = "uLink-only runtime branch.",
            Changes =
            [
                "Removes the legacy tunnel runtime services and makes uLink the only traffic engine in this branch.",
                "Dashboard and analytics now read uLink runtime status and path telemetry.",
                "Renames tunnel controls and diagnostics to uLink active mode."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.16",
            Date = "2026-06-14",
            Summary = "Made the uLink tunnel bidirectional.",
            Changes =
            [
                "The uLink client now writes return packets from the server back into the client TUN.",
                "The uLink server now reads its TUN and sends return packets back to the latest known client path peers.",
                "This enables a real uLink TUN ping test without changing the router default route."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.15",
            Date = "2026-06-14",
            Summary = "Added uLink FEC recovery.",
            Changes =
            [
                "Added XOR parity FEC blocks for uLink AnchorFec traffic.",
                "The uLink server can recover one missing packet from each two-packet parity block when the paired data packet and parity arrive.",
                "Keeps FEC in the uLink tunnel path only; production routing is still disabled by default."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.14",
            Date = "2026-06-14",
            Summary = "Added uLink traffic-engine controls.",
            Changes =
            [
                "Added a persisted uLink traffic-engine mode for staged tunnel testing.",
                "Added guarded uLink service controls that stay locked unless explicitly enabled in configuration.",
                "Locks uLink primary mode behind a separate configuration flag so production routing cannot change by accident.",
                "Changed multi-path uLink probes to use bind-device physical path isolation instead of the legacy tunnel bypass by default."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.13",
            Date = "2026-06-14",
            Summary = "Added uLink multi-path shadow probing.",
            Changes =
            [
                "Added an uLink multi-path probe command that duplicates heartbeat packets across configured paths.",
                "Shows first-arrival, ACK, packet loss, and route-verification details for each uLink path.",
                "Added structured uLink server packet events for first arrivals and duplicate or late drops."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.12",
            Date = "2026-06-14",
            Summary = "Stabilized the uLink public lab test.",
            Changes =
            [
                "Added a short settle window after creating the temporary legacy tunnel bypass before sending uLink heartbeat packets.",
                "Keeps the public lab test cleanup behavior unchanged after the run completes."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.11",
            Date = "2026-06-14",
            Summary = "Added uLink public heartbeat lab controls.",
            Changes =
            [
                "Added an uLink Lab public test button that temporarily bypasses UDP 8444 through legacy tunnel.",
                "Shows uLink heartbeat packet loss, RTT, client bind address, and bypass cleanup status in the dashboard.",
                "Keeps uLink in shadow-test mode without routing production traffic."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.10",
            Date = "2026-06-14",
            Summary = "Added disabled uLink live-test deployment support.",
            Changes =
            [
                "Added an uLink client heartbeat ping command for sealed UDP server/client smoke tests.",
                "Fixed uLink duplicate detection so separate sessions can reuse packet sequence numbers safely.",
                "Prepared uLink for disabled deployment on the router and private server without routing production traffic."
            ]
        },
        new ChangelogEntry
        {
            Version = "2026.06.9",
            Date = "2026-06-13",
            Summary = "Added uLink prototype planning and observability.",
            Changes =
            [
                "Saved the uLink Rust dataplane and Blazor control-plane implementation plan in the repository.",
                "Added a Rust uLink workspace with protocol framing, duplicate detection, path-health scoring, and scheduler foundations.",
                "Added a read-only uLink page in XNetwork for prototype status, anchor path, packet counters, and path roles."
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
