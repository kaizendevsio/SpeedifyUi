# AGENTS.md

## Repo Shape
- Single app repo: `SpeedifyUi.sln` contains `XNetwork/XNetwork.csproj`, a Blazor Server app targeting `net9.0`; no test project, npm pipeline, or CI workflow was found.
- App wiring is in `XNetwork/Program.cs`: Interactive Server components plus singleton/hosted `SpeedifyService`, `NetworkMonitorService`, and `ConnectionHealthService`.
- Main routes are `/` (`Home.razor`), `/details` (`Statistics.razor`), `/settings`, `/controls`, `/server-statistics`, and `/ai-chat`.

## Commands
- Build: `dotnet build SpeedifyUi.sln`
- Run locally: `dotnet run --project XNetwork/XNetwork.csproj`
- Publish: `dotnet publish XNetwork/XNetwork.csproj -c Release`
- With no tests configured, use `dotnet build` plus focused manual checks in the browser and with `speedify_cli`.
- The real HTTP binding is `http://0.0.0.0:8080` from `XNetwork/appsettings.json`; `launchSettings.json` lists 5063/7061 but is not the deployment port.

## Speedify CLI
- The app requires `speedify_cli` in PATH; successful commands return JSON and errors are on stderr as documented in `speedify-cli.md`.
- All Speedify process execution belongs in `SpeedifyService`: terminating commands use `RunTerminatingCommand`, streaming stats use `StreamCommandOutputAsync("stats")`.
- Streaming stats must buffer until a complete JSON `[]` array parses; keep the 32KB runaway safeguard and `Kill(true)` plus 2-second wait cleanup for canceled streams.
- `GetStatsAsync` only yields `connection_stats` and intentionally filters the `speedify` aggregate plus `%proxy` connections.
- Units differ by API: `ConnectionItem.ReceiveBps`/`SendBps` are bytes/sec, adapter rate limits are bits/sec, adapter data usage/limits are bytes, and `0` means unlimited.
- For new CLI response models, use `System.Text.Json` with `[JsonPropertyName]` when the JSON name is not the C# property name.

## Linux And Privileges
- `NetworkMonitorService` exits unless the app runs on Linux, monitoring is enabled, and `NetworkMonitor:WhitelistedLinks` is non-empty.
- `WhitelistedLinks` is compared to `Adapter.Name` from `show adapters` (interface-like names such as `enxc8...`), not ISP/provider text.
- The monitor checks every 1s and restarts down whitelisted links after `DownTimeoutSeconds` via `/bin/bash -c "ip link set <iface> down/up"`; this needs root/process privileges.
- OS routing priority parses Linux `ip route`/`ip addr` output and applies changes with `sudo ip route ...`; web requests cannot answer sudo prompts.
- Linux system controls in `Settings.razor` call `sudo systemctl restart speedify || sudo service speedify restart` and `sudo /sbin/reboot`; configure NOPASSWD using `LINUX_SETUP.md` before testing those buttons.

## Blazor Runtime
- Components are server-side interactive from `App.razor`; timer, stream, and background callbacks should marshal UI updates with `InvokeAsync(StateHasChanged)`.
- `Home.razor` runs a 3s adapter refresh, 10s server refresh, and a stats stream; `ConnectionHealthService` also pings `1.1.1.1` every 500ms and consumes a stats stream, so avoid unbounded extra `speedify_cli stats` consumers.
- `Statistics.razor` is the `/details` page; chart setup intentionally takes two render cycles using `_readyForChartInitializationStep` before initializing canvases and starting streaming.
- Chart modules live in `XNetwork/wwwroot/js`; Tailwind, Font Awesome, Google Fonts, and Chart.js are CDN-loaded in `App.razor`, not bundled.
- JS interop can throw `JSDisconnectedException` during live chart updates or disposal; stop timers/streams or ignore it during disconnect cleanup.

## Settings Page
- `Settings.razor` is a large accordion page; reusable controls live in `XNetwork/Components/Custom` and related styles are in `XNetwork/wwwroot/app.css`.
- Most settings apply immediately through CLI handlers; pending flags `_hasUnsavedBypassChanges`, `_hasUnsavedStreamingChanges`, and `_adapterPendingChanges` only cover selected rule/list/adapter edits.
- Streaming and gaming service presets are hardcoded dictionaries, while `_enabledServices` is a separate case-insensitive `HashSet` that must stay in sync with `_bypassSettings.Services`.
- Adapter settings cache local state in `_adapterLocalState`; UI converts MB/GB to bytes for data limits, keeps rate limits as raw bits/sec, and only offers monthly reset days 1-28.
- Port rules for streaming, bypass, and fixed-delay are formatted in `SpeedifyService.FormatPortRule` as `port[-end]/protocol`.

## Instruction Sources
- Treat this root `AGENTS.md` as current; `CLAUDE.md` is older and broader.
- `.roo/rules-*` files contain useful historical gotchas but also stale claims, so verify them against code before preserving anything.
