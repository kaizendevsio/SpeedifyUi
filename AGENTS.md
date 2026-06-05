# AGENTS.md

## Repo Shape
- `SpeedifyUi.sln` contains three .NET 9 projects: `XNetwork/XNetwork.csproj` (Blazor Server dashboard), `SpeedifyProbeAgent/SpeedifyProbeAgent.csproj` (external health probe API), and `XNetwork.Tests/XNetwork.Tests.csproj` (xUnit tests).
- Main app wiring is in `XNetwork/Program.cs`: Interactive Server components, route transitions, singleton/hosted `SpeedifyService`, `NetworkMonitorService`, `ConnectionHealthService`, `CudyApControlService`, `AutoServerSwitchService`, `XRouterService`, and `LocalProcessTrafficService`.
- Main dashboard routes are `/` (`Home.razor`), `/details` (`Statistics.razor`), `/settings`, `/controls`, `/server-statistics`, `/server-switching`, `/ai-chat`, and `/xrouter` (`XRouter.razor`, shown in the UI as `Wifi`).
- Product branding is `XNetwork`; the historical `/xrouter` route and some class names remain for compatibility and should not be renamed casually.
- Frontend assets are mostly static/CDN: Tailwind, Font Awesome, Google Fonts, and Chart.js are loaded from CDNs in `App.razor`; local JS modules live in `XNetwork/wwwroot/js`.
- Current app logo asset is `XNetwork/wwwroot/icons/xnetwork-logo.png`; browser/PWA references use `/icons/xnetwork-logo.png?v=20260511`.

## Commands
- Build: `dotnet build SpeedifyUi.sln`
- Test: `dotnet test XNetwork.Tests/XNetwork.Tests.csproj`
- Run locally: `dotnet run --project XNetwork/XNetwork.csproj`
- Publish app: `dotnet publish XNetwork/XNetwork.csproj -c Release`
- Publish probe: `dotnet publish SpeedifyProbeAgent/SpeedifyProbeAgent.csproj -c Release`
- Real app HTTP binding is `http://0.0.0.0:8080` from `XNetwork/appsettings.json`; `launchSettings.json` ports are not the deployment binding.
- If parallel `dotnet build` and `dotnet test` collide on `obj` files, run `dotnet build-server shutdown` and rerun sequentially.

## Git And Workspace Notes
- The active deployed branch has been `bugfix/settings-dropdown-refresh`; verify with `git status`/`git branch` before assuming.
- Untracked `scripts/` has been present and unrelated; do not add, delete, or modify it unless the user explicitly asks.
- Local newline-only noise has appeared in `XNetwork/wwwroot/js/pwa.js` and `XNetwork/wwwroot/service-worker.js`; avoid committing those unless their content is intentionally changed.
- Do not commit secrets such as Cudy admin passwords, probe API keys, private SSH keys, runtime `appsettings.json`, or files under `/home/xeon-network/.config/XNetwork`.

## Deployment
- Router host is `xeon-network`; the app also responds on Tailscale IP `100.112.183.104` when Tailscale is healthy.
- From this workstation, SSH has used `C:\Users\Xeon\.ssh\speedifyui_cli_probe`; never commit key material.
- Router repo/deploy path is `/home/xeon-network/xnetwork`; service is `xnetwork.service`.
- Deploy command used from Windows: `ssh -i "C:\Users\Xeon\.ssh\speedifyui_cli_probe" xeon-network@xeon-network "cd /home/xeon-network/xnetwork; ./deploy.sh"`
- `deploy.sh` runs `git pull`, preserves published `appsettings.json` and `auto-server-switch-state.json`, cleans publish output, runs `dotnet publish XNetwork/XNetwork.csproj -c Release`, restores preserved runtime files, kills the `xnetwork.service` MainPID, and lets systemd restart it.
- Non-interactive `sudo systemctl restart xnetwork.service` is not available; the deploy script uses MainPID `kill -KILL` to trigger restart.
- After deploy, verify with `systemctl is-active xnetwork.service`, `curl -sS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8080/`, and optionally external `curl http://100.112.183.104:8080/`.
- Remote publish often emits existing CS1998 warnings in `Statistics.razor`, `SpeedifyService.cs`, and `Settings.razor`; they are known warnings, not deployment blockers.

## Speedify CLI
- The app requires `speedify_cli` in PATH; successful commands return JSON and errors are on stderr as documented in `speedify-cli.md`.
- All Speedify process execution belongs in `SpeedifyService`: terminating commands use `RunTerminatingCommand`, streaming stats use `StreamCommandOutputAsync("stats")`.
- Streaming stats must buffer until a complete JSON `[]` array parses; keep the 32KB runaway safeguard and `Kill(true)` plus 2-second wait cleanup for canceled streams.
- `GetStatsAsync` yields only per-connection `connection_stats` and intentionally filters the `speedify` aggregate plus `%proxy` connections; `GetStatsWithAggregateAsync` includes the `speedify` aggregate for dashboard actual throughput.
- Units differ by API: live Speedify 16.8.0 `ConnectionItem.ReceiveBps`/`SendBps`/`TotalBps` from `stats` match bits/sec in sustained OS/F50 counter tests despite the `Bps` JSON names and older docs; convert those fields to Mbps with `/ 1_000_000`. `show adapters` rate-limit fields `downloadBps`/`uploadBps` are bytes/sec, `speedify_cli adapter ratelimit` command inputs are bits/sec, adapter data usage/limits are bytes, and `0` means unlimited.
- For new CLI response models, use `System.Text.Json` with `[JsonPropertyName]` when JSON names differ from C# names.
- Observed runtime Speedify settings have used `bondingMode: redundant`, `transportMode: udp`, `headerCompression: true`, `jumboPackets: true`, `packetAggregation: true`, and `maxRedundant: 5`; redundant mode can make Speedify tunnel throughput much higher than Cudy client throughput.
- Large `Estimated VPN / Overhead` in the traffic modal is not necessarily pure protocol overhead; it can include redundant-mode duplication, retransmits/loss recovery, forwarded client packets that process attribution cannot map, and timing mismatch between data sources.

## Probe And Auto Server Switching
- `SpeedifyProbeAgent` is a separate minimal API that scores Speedify servers from an external vantage point; deployed probe service has been `speedify-probe-agent` on VM `speedify-probe` with Tailscale IP `100.114.215.110` and API base `http://100.114.215.110:8090`.
- Do not write the probe API key into repo files; it belongs in runtime config only.
- Router `AutoServerSwitchService` uses `ProbeScoreClient`, `ServerSwitchRecommendationSelector`, `LocalWanStabilityEvaluator`, `RecommendationConfidenceTracker`, and `AutoServerSwitchStateStore`.
- Auto-switch can block switching when the probe says the current server is still acceptable, treating local router health as a WAN issue.
- Router Tailscale connectivity can be affected by Speedify routing; restarting Speedify has previously restored router-to-probe connectivity.
- `PrivateReconnectService` is a hosted service for refreshing the private Speedify session with `speedify_cli disconnect`, a short delay, then `speedify_cli connect private`; it is disabled by default and configured by `PrivateReconnect` / `private-reconnect-settings.json`.
- Private reconnect defaults: `Enabled=false`, `IntervalMinutes=30`, `DelaySeconds=2`; the Settings page can enable it, change the interval/delay, view status/events, and trigger `Reconnect Private Now`.
- 2026-06-04: A scheduling bug was fixed in `PrivateReconnectService`: do not reschedule `NextAttemptUtc` after every sleep/check tick, or the due time is pushed forward forever and only manual reconnect works.
- Private reconnect also supports an optional high-latency trigger: only while connected to a verified private Speedify server, sustained latency defaults to `>=300ms` for `120s`, then it reconnects, observes recovery for `60s`, and if latency remains high it suppresses further health-triggered reconnects for `15m`. Manual reconnect and the scheduled timer remain separate.

## Linux And Privileges
- `NetworkMonitorService` exits unless the app runs on Linux, monitoring is enabled, and `NetworkMonitor:WhitelistedLinks` is non-empty.
- `WhitelistedLinks` is compared to `Adapter.Name` from `show adapters` (interface-like names such as `enxc8...`), not ISP/provider text.
- The monitor checks every 1s and restarts down whitelisted links after `DownTimeoutSeconds` via `/bin/bash -c "ip link set <iface> down/up"`; this needs root/process privileges.
- OS routing priority parses Linux `ip route`/`ip addr` output and applies changes with `sudo ip route ...`; web requests cannot answer sudo prompts.
- Linux system controls in `Settings.razor` call `sudo systemctl restart speedify || sudo service speedify restart` and `sudo /sbin/reboot`; configure NOPASSWD using `LINUX_SETUP.md` before testing those buttons.
- `LocalProcessTrafficService` uses `nethogs` for live process throughput on `connectify0`; `nethogs` needs root or capabilities. Use `sudo setcap cap_net_admin,cap_net_raw+eip /usr/sbin/nethogs` if process attribution is unavailable.

## Blazor Runtime
- Components are server-side interactive from `App.razor`; timer, stream, and background callbacks should marshal UI updates with `InvokeAsync(StateHasChanged)`.
- Blazor reconnect is customized in `App.razor` with `components-reconnect-modal`, `wwwroot/js/blazorReconnect.js`, and reconnect CSS in `wwwroot/app.css`; the UI should stay a minimalist bottom-center toast with a spinner plus one status line, no action buttons, and a blurred background that transitions in/out. Rejected/expired circuits auto-reload after a short delay using a MutationObserver on the reconnect element class, not only the Blazor state-change event, because mobile/PWA sessions have shown the rejected class without the event handler triggering reload. Blazor autostart is disabled so the script can set 15s server timeout, 5s keepalive, fast early retry intervals, and background retries after built-in retry failure.
- Server-side SignalR circuit timings are also tuned in `Program.cs` with `ClientTimeoutInterval=15s`, `HandshakeTimeout=15s`, and `KeepAliveInterval=5s`.
- `Home.razor` runs a 3s adapter refresh, 10s server refresh, one Speedify stats stream, and a traffic breakdown timer only while the modal is open.
- `ConnectionHealthService` pings `1.1.1.1` every 500ms and consumes its own Speedify stats stream; avoid adding unbounded extra `speedify_cli stats` consumers.
- `Statistics.razor` is the `/details` page; chart setup intentionally takes two render cycles using `_readyForChartInitializationStep` before initializing canvases and starting streaming.
- `ConnectionSummary.razor` is the dashboard throughput card; it opens the Traffic Breakdown modal when XRouter/Cudy config is available.
- JS interop can throw `JSDisconnectedException` during live chart updates or disposal; stop timers/streams or ignore it during disconnect cleanup.
- Mobile is first-class; avoid regressions in bottom tab bar, modal z-index, safe-area padding, and sticky headers.

## Traffic Breakdown
- Dashboard `Traffic Breakdown` is opened by tapping/clicking the main throughput card; the old always-visible inline attribution card was removed.
- Modal refreshes every 1s while open and shows Speedify Tunnel, Wifi Clients, Local Processes, and Estimated VPN / Overhead.
- Wifi client totals come from Cudy device table via `XRouterService.GetClientsAsync`; cache duration is 750ms so 1s UI refreshes do not over-fetch within a tick.
- Local process totals come from `LocalProcessTrafficService`, which runs `nethogs -t -b -C -d 1 -c 1 connectify0` and parses KB/s into Mbps.
- Zero-throughput nethogs rows are filtered. Non-zero `unknown TCP/0/0` or `unknown UDP/0/0` rows are shown as `Unknown tunnel flow` because the kernel/nethogs cannot map those packets to a Pi process.
- Estimated VPN / Overhead is `Speedify Tunnel - Wifi Clients - Local Processes`, clamped at zero. Treat it as an estimate, not an exact byte-accounting ledger.

## Wifi And Cudy Client Management
- The UI nav label is `Wifi`, but the route is still `/xrouter` and the component is `XRouter.razor`.
- `XRouter.razor` lists Cudy clients, shows hostname/IP/MAC/connection type/signal/online duration, live upload/download throughput, per-device history, block/unblock, and per-device rate limits.
- Cudy client list refreshes every 1s from the Cudy client table.
- Known Cudy client endpoints are `/cgi-bin/luci/admin/network/devices/devlist?detail=1`, `/cgi-bin/luci/admin/network/devices/internet`, and `/cgi-bin/luci/admin/network/devices/devinfo`.
- Cudy data caps were not implemented because the inspected firmware endpoint did not expose them.
- `CudyXRouterClientParser` parses the Cudy HTML table; keep parser tests updated when endpoint markup changes.

## Cudy AP Automation
- Cudy TR3000 control is via LuCI web forms and service apply, not physical power control.
- Runtime Cudy management has used private HTTPS at `https://192.168.145.231/`; HTTP port 80 was refused. Self-signed certificate validation is allowed only for private HTTPS management IPs.
- Login quirk: `/cgi-bin/luci` serves the login form while `/cgi-bin/luci/` can return `403`.
- Cudy wireless form paths include `/cgi-bin/luci/admin/network/wireless/config/combo`, `/cgi-bin/luci/admin/network/wireless/config/uncombine`, and `/cgi-bin/luci/admin/network/wireless/config/combine`.
- Cudy disabled fields are `cbid.wireless.wlan00.disabled` for 2.4 GHz, `cbid.wireless.wlan10.disabled` for 5 GHz, and `cbid.wireless.wlan.disabled` for Smart Connect/combined mode; value `1` means disabled.
- Posting the wireless form only saves config; the app must also POST `/cgi-bin/luci/admin/servicectl/restart/wireless,vlan` and poll `/cgi-bin/luci/admin/servicectl/status` until `finish`.
- Cudy admin password is persisted separately from non-secret automation settings. Secret file is `cudy-admin-password`; non-secret JSON is `cudy-ap-automation-settings.json`.
- Runtime Linux paths have been `/home/xeon-network/.config/XNetwork/cudy-admin-password` and `/home/xeon-network/.config/XNetwork/cudy-ap-automation-settings.json`; do not commit either.
- Cudy AP SSIDs `A` and `A.2g` disappeared from Pi scan after service apply during prior verification.

## Router Wi-Fi Tooling
- `WifiService` uses Linux `nmcli` for Wi-Fi scan/status/connect, default interface `wlan0`; password input is passed through stdin and cleared after submit.
- Router tooling observed: `nmcli` at `/usr/bin/nmcli`, `wpa_cli` at `/usr/sbin/wpa_cli`, interface `wlan0`.
- Home Wi-Fi automation stores both SSID and BSSID; Cudy automation can disable Cudy AP bands when the home network is present with sufficient signal and re-enable when missing/weak.
- `CudyApAutomationSettings` runtime defaults include `Disable2G: true`, `Disable5G: true`, signal thresholds, and check timing; verify actual runtime config before changing behavior.

## Settings Page
- `Settings.razor` is a large accordion/modal page; reusable controls live in `XNetwork/Components/Custom` and related styles are in `XNetwork/wwwroot/app.css`.
- Most settings apply immediately through CLI handlers; pending flags `_hasUnsavedBypassChanges`, `_hasUnsavedStreamingChanges`, and `_adapterPendingChanges` only cover selected rule/list/adapter edits.
- The `Private Server Reconnect` Settings accordion persists via `PrivateReconnectSettingsStore` and triggers manual/scheduled reconnects through `PrivateReconnectService`; do not call `speedify_cli` directly from UI code.
- Streaming and gaming service presets are hardcoded dictionaries, while `_enabledServices` is a separate case-insensitive `HashSet` that must stay in sync with `_bypassSettings.Services`.
- Adapter settings cache local state in `_adapterLocalState`; UI converts MB/GB to bytes for data limits, displays adapter rate limits in Mbps while converting Speedify's returned bytes/sec to command bits/sec on save, and only offers monthly reset days 1-28.
- Adapter encryption state is not returned by `speedify_cli show adapters`; read it from `speedify_cli show settings` using `perConnectionEncryptionEnabled` / `perConnectionEncryptionSettings` and merge it into adapter rows. Missing per-adapter entries inherit the global `encrypted` setting.
- Port rules for streaming, bypass, and fixed-delay are formatted in `SpeedifyService.FormatPortRule` as `port[-end]/protocol`.

## Tests
- Test project is `XNetwork.Tests`; current tests cover Cudy form/client parsing, local process traffic parsing, rolling health windows, probe score calculations, server switch recommendations, recommendation confidence, auto-switch state store, and local WAN stability.
- Latest local full test run during the adapter rate-limit unit fix passed 34 tests; do not hard-code that as a permanent invariant.
- Add focused tests for parser changes because Cudy/LuCI endpoints are HTML/form based and firmware-specific.

## Instruction Sources
- Treat this root `AGENTS.md` as current; `CLAUDE.md` is older and broader.
- `.roo/rules-*` files contain useful historical gotchas but also stale claims, so verify them against code before preserving anything.
