# Per-Adapter Details Sheet And Dynamic ISP Names

Date: 2026-08-13
Branch: `feature/xband-only-runtime`
Status: Approved design

## Problem

Two limitations on the uLink dashboard (`XNetwork/Components/Pages/Home.razor`):

1. Only Starlink adapter cards are interactive. `OnPathCardClick` returns early unless
   `IsStarlinkPath(path)` matches, so every other adapter card has no detail view. Latency,
   loss, throughput history, and upstream identity are unavailable per adapter.
2. Adapter display names are effectively hardcoded per host. `XBondStatsService.ResolvePathName`
   resolves a name from a manual alias or the NetworkManager connection name that an operator
   typed once (`Smart`, `Dito`, `Globe`). Nothing discovers the actual upstream ISP, so a swapped
   modem, a new SIM, or a re-plugged USB adapter shows a stale or generic name.

## Goals

- Every dashboard adapter card opens a detail action sheet, not only Starlink.
- Adapter names are derived from the live upstream ISP through a free third-party API,
  with a manual override retained.
- No per-adapter hardcoding of provider names, USB ids, or interface names.

## Non-Goals

- New per-adapter destructive controls (modem reboot, USB reset). No per-adapter command path
  exists today; F50 recovery and the link watchdog are host-wide services. Starlink dish
  commands remain exactly as they are.
- Changes to the Rust `xbond` client/server. This is an app-only change, so the paired
  runtime deploy is not required.
- IPv6 identity lookups. Production routing on this branch is IPv4-only.

## Architecture

Three new units plus one enabling refactor.

### 1. `AdapterIdentityService` — who is upstream of each adapter

Singleton registered as a hosted service, following `StarlinkTelemetryService` and
`F50ModemTelemetryService` conventions.

Responsibilities:

- Enumerate candidate interfaces from `InterfaceMetadataService` (`GetInterfacesAsync` for devices,
  `GetDefaultGatewayRoutesAsync` for gateways), *not* from `XBondSnapshotCache`. This direction is
  required: `XBondStatsService` consumes identities to resolve names, and `XBondSnapshotCache` wraps
  `XBondStatsService`, so reading the snapshot here would create a dependency cycle. Consequently
  `XBondStatsService` only ever reads already-cached identities synchronously and never awaits a
  lookup while building a snapshot.
- Skip interfaces that must never be probed, using the same rules as
  `InterfaceMetadataService.ShouldSkipGatewayProbe`: `xbond*`, `tailscale*`, `p2p-*`, `lo`,
  and interfaces that are down.
- For each remaining interface, issue one interface-bound HTTP GET so the request egresses
  through that WAN instead of the `xbond0` default route. This is what makes the answer
  per-adapter rather than per-tunnel.
- Cache results per interface with TTL and backoff, and expose lookups to the UI and to
  name resolution.

Public surface:

```csharp
AdapterIdentity? Get(string interfaceName);
IReadOnlyDictionary<string, AdapterIdentity> GetAll();
IReadOnlyDictionary<string, string> GetDisplayNames();          // interface -> normalized ISP name
Task<AdapterIdentity?> RefreshAsync(string interfaceName, CancellationToken ct);  // "Refresh identity now"
```

Caching rules:

- Success TTL: 15 minutes.
- Failure backoff: 2 minutes (failures are cached so a dead endpoint is not hammered).
- Immediate invalidation when the interface's default gateway changes, and eviction when the
  interface disappears. This covers modem swaps and re-plugged USB adapters without waiting out the
  TTL; a same-gateway dynamic-IP change is picked up by the TTL.
- Refresh loop tick: 60 seconds. Probes within a tick are staggered so bursts stay well inside
  free-tier rate limits. With five adapters and a 15-minute TTL this is roughly 20 requests/hour.

Endpoints, tried in order until one parses:

1. `https://ipwho.is/` — HTTPS, keyless.
2. `http://ip-api.com/json/` — keyless fallback, 45 requests/minute.

### 2. `InterfaceBoundHttpClientFactory` — shared SO_BINDTODEVICE plumbing

The `SO_BINDTODEVICE` socket handler currently lives inside
`XNetwork/Services/StarlinkBoundHttpClientFactory.cs`. It moves to a new
`InterfaceBoundHttpClientFactory` with a static `CreateHandler(string interfaceName, TimeSpan timeout)`
and `CreateClient(...)`. `StarlinkBoundHttpClientFactory` delegates to it and keeps its current
public surface (`IStarlinkHttpClientFactory`, `StarlinkHttpClientLease`) so Starlink behavior is
unchanged.

Linux-only, as today: the handler throws on non-Linux, and `AdapterIdentityService` no-ops off Linux
the same way `InterfaceMetadataService.GetInterfacesAsync` does.

DNS note: the endpoint hostname is resolved by the OS resolver over the default route, while the TCP
connection is bound to the adapter. The resolved address is the same either way, so the public IP the
endpoint reports is still that adapter's egress IP.

### 3. `AdapterTelemetryHistoryService` — bounded per-adapter history

Singleton hosted service sampling `XBondSnapshotCache` every 1 second into a bounded ring per
interface: 300 samples (about 5 minutes) of `TimestampUtc`, `RttMs`, `LossPercent`, `JitterMs`,
`DownloadMbps`, `UploadMbps`.

Rationale: `/details` accumulates chart points client-side in Chart.js, so a freshly opened chart
starts empty. `ConnectionHealthService` keeps only a 30-sample health window and its buffer size is
load-bearing for health metrics. A dedicated bounded history means the sheet shows a populated chart
the moment it opens, and mirrors the existing `StarlinkTelemetryHistory` shape.

The ring itself is a separate plain class (`AdapterTelemetryHistory`) so pruning is unit-testable
without the hosted service, exactly like `StarlinkTelemetryHistory`.

### 4. Name resolution

`AdapterNameResolver` — a new static, pure, unit-testable resolver. Precedence:

1. Manual alias from Settings > Link Watchdog (`NetworkMonitorSettingsStore.GetAdapterAlias`).
2. Normalized ISP name from `AdapterIdentityService`.
3. Interface metadata display name (NetworkManager connection name, or the F50 modem
   `network_provider` readout from the existing gateway probe).
4. `path.Name` from the XBond runtime.
5. The raw interface name.

`XBondStatsService.FromStatus` currently receives interfaces with aliases already folded into
`InterfaceMetadata.DisplayName` by `ApplyAdapterAliases`, which makes alias and nmcli name
indistinguishable. To place the ISP name between them, the stats path passes aliases and ISP names
as separate dictionaries and delegates to `AdapterNameResolver`. `ApplyAdapterAliases` stays for its
other callers, and the existing `FromStatus` overloads keep working by passing an empty ISP
dictionary, so current tests remain valid.

ISP name normalization, applied for display only:

- Trim and collapse whitespace.
- Drop a leading `AS####` token (`ip-api` returns `as` as `AS10139 Smart Communications`).
- Drop trailing corporate suffixes: `Inc.`, `Inc`, `Ltd.`, `Ltd`, `LLC`, `Corp.`, `Corp`, `Co.`, `S.A.`.
- Fall back to the unnormalized string if normalization empties it.

The raw ISP, org, and AS strings remain visible in the detail sheet.

## Components

- `Components/Custom/AdapterDetailsSheet.razor` — the generic sheet. Parameters: `IsOpen`, `Path`,
  `Identity`, `Samples`, `AdminUrl`, `OnClose`, `OnRefreshIdentity`, `IsRefreshingIdentity`,
  `ExtraSection` (`RenderFragment?`). Wraps the existing `ActionSheet`.
- `Components/Custom/StarlinkDetailsSection.razor` — today's Starlink sheet body (tiles, dish charts,
  alerts, capability actions, hardware/software/management footer), moved out of `Home.razor` and
  passed to `AdapterDetailsSheet` as `ExtraSection` for Starlink paths.
- `Components/Custom/AdapterTelemetryChart.razor` — generic chart over `AdapterTelemetrySample`,
  modeled on `StarlinkTelemetryChart` (per-instance canvas id, hash-guarded re-render, async
  disposal of the JS module).
- `Utils/TelemetryFormatter.cs` — the shared `Format*` helpers currently private to `Home.razor`
  (`FormatNullableMs`, `FormatNullableSpeed`, `FormatNullablePercent`, `FormatNullableDegrees`,
  `FormatDuration`, `FormatLastUpdated`, `FormatUnknown`, and friends), so both sheets use one copy.

`Home.razor` changes:

- `OnPathCardClick` opens the sheet for every path; the Starlink early-return is removed.
- `GetPathCardClass` applies the pointer/hover/focus affordance to every card.
- One `AdapterDetailsSheet` instance driven by `_selectedPath`, with the Starlink section supplied
  only when `IsStarlinkPath(path)`.
- The existing inline Starlink telemetry strip on the card stays as-is.

`Home.razor` is currently 1323 lines, roughly 40 private methods of which most are Starlink
formatting. Extracting the Starlink section and the shared formatters is what allows a second sheet
without duplicating any of that code; it is scoped to code this feature touches.

### Sheet content

For every adapter:

- **Live charts** — latency, loss, download, upload, from `AdapterTelemetryHistory`.
- **Identity** — public IP as seen through the adapter, ISP, org / AS label, city, region, country,
  gateway, interface name, bind address, socket generation, last rebind reason, last updated,
  and the source endpoint that answered.
- **Cellular** — network generation, signal bars, and the modem admin link through the existing
  `LocalDeviceProxyService`, rendered when `path.HasCellularTelemetry`.
- **Actions** — open admin page (existing local device proxy URL) and *Refresh identity now*.
- **Technical details** — role, score, late percent, queue depth, heartbeat sample count,
  cooldown/demotion reason, matching what the card shows under
  `UiDisplayPreferences.AdapterTechnicalDetails`.

Starlink additionally renders `StarlinkDetailsSection` with its unchanged dish telemetry and
existing slide-to-confirm commands.

## Configuration

New `AdapterIdentity` section in `XNetwork/appsettings.json`, bound like `StarlinkTelemetry`:

```json
"AdapterIdentity": {
  "Enabled": true,
  "Endpoints": ["https://ipwho.is/", "http://ip-api.com/json/"],
  "RefreshIntervalSeconds": 60,
  "SuccessTtlMinutes": 15,
  "FailureBackoffSeconds": 120,
  "RequestTimeoutSeconds": 5
}
```

A user-facing on/off toggle is persisted through a new `AdapterIdentitySettingsStore`, following the
existing `NetworkMonitorSettingsStore` / `F50ModemRecoverySettingsStore` pattern (JSON under the
service user's `XNetwork` config directory), and surfaced in `Settings.razor`. Default on. When
disabled, the refresh loop idles, cached identities are cleared, names fall back to the pre-existing
chain, and the sheet's identity block reports that lookups are disabled.

## Error Handling

- A failed or unparsable lookup degrades silently: the name falls back down the precedence chain and
  the identity block shows "ISP unavailable" plus the reason. Nothing blocks card or sheet rendering.
- Endpoint failures are cached with the failure backoff so one dead endpoint does not generate
  per-tick traffic.
- `OperationCanceledException` on shutdown is swallowed as in the existing services; other exceptions
  are logged at Debug like `InterfaceMetadataService`'s gateway probe, because a missing upstream
  answer is an expected field condition, not an app fault.
- `SO_BINDTODEVICE` failures (interface disappeared mid-probe) are treated as a normal lookup
  failure for that interface only.
- The history ring is bounded by count, so a long-running process cannot grow it without limit.

## Testing

xUnit in `XNetwork.Tests` (no bUnit in the project, so coverage targets services and pure statics):

- `ipwho.is` response parsing: success, `success: false` error payload, missing `connection` block.
- `ip-api.com` response parsing: `status: success`, `status: fail` with `message`, missing fields.
- ISP name normalization: `AS####` prefix stripping, corporate suffix stripping, whitespace
  collapsing, empty-after-normalization fallback.
- `AdapterNameResolver` precedence, including alias-wins-over-ISP and full fallback to interface name.
- Identity cache behavior with an injected `TimeProvider` (the seam `InterfaceMetadataService`
  already uses): success TTL expiry, failure backoff, invalidation when the gateway changes.
- Skip rules: `xbond0`, `tailscale0`, `lo`, and down interfaces are never probed.
- `AdapterTelemetryHistory` pruning by sample count and per-interface isolation.

Commands: `dotnet build SpeedifyUi.sln` and `dotnet test XNetwork.Tests/XNetwork.Tests.csproj`.

## Versioning

Bump `AppChangelog.CurrentVersion` from `ulink-2026.06.122` to the next release and add the matching
changelog entry in the same change, per the repo convention.

## Deployment

App-only change, no Rust. Per `AGENTS.md`, that means the router-side script, not the paired script:

```
ssh -i "C:\Users\Xeon\.ssh\speedifyui_cli_probe" xeon-network@xeon-network "cd /home/xeon-network/xnetwork; ./deploy.sh"
```

Post-deploy verification:

- `systemctl is-active xnetwork.service`
- HTTP 200 on `/`, `/details`, `/xbond`, `/settings`, `/xrouter`
- Deployed `build-info.json` reports the new version and commit
- Each dashboard adapter card opens its sheet, and adapter names show resolved ISP names
