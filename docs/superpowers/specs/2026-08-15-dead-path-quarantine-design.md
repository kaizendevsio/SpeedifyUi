# Dead uLink Path Quarantine

Date: 2026-08-15
Branch: `feature/xband-only-runtime`
Status: Approved design

## Problem

Configured uLink paths can become permanently dead in ways the dashboard cannot express:

- **Absent interface.** Path 4 binds `enxb8d4bcc3bf30`, which exists in neither `nmcli device status` nor
  `ip link`. The adapter is gone (unplugged, or re-enumerated under a new name). `ShowOnDashboard` is
  `InterfaceUp || IsActive || InCooldown || !IsConfigured`, so it is silently hidden — an operator gets no
  signal at all that a configured tunnel path has no hardware behind it.
- **Administratively disabled.** Path 5 binds `wlan0`, which was deliberately disabled in
  Settings > Router Wi-Fi (`ulink-2026.06.126`). It renders as an alarming red card reporting `100% loss`,
  which describes a fault rather than a choice the operator made.

Neither state is distinguishable from a genuine outage, and neither offers a way to act.

## Goals

- Classify why a path is dead and say so in plain language on the card.
- Keep permanently-dead paths out of the visual space used by live adapters, without hiding them.
- Offer a one-click way to take a dead path out of the tunnel.

## Non-Goals

- **No automatic removal from `client.toml`.** The USB adapters on this router re-enumerate; commit
  `46acb83` exists precisely because a Starlink adapter came back under a new interface name. An automatic
  prune would permanently drop a path that was about to return. Removal stays operator-initiated.
- **No Rust or scheduler change.** `xbond-client` already hard-excludes down/`NO-CARRIER` paths from anchor
  and backup roles, so these paths are not stealing traffic. This is app-only, deployed with `deploy.sh`.
- **No reclassification of `NO-CARRIER`.** A dish or modem that lost carrier is a real outage and keeps its
  existing red treatment.

## Verified Ground Truth (2026-08-15)

| Path | Interface | Kernel | nmcli | Expected class |
|---|---|---|---|---|
| 4 | `enxb8d4bcc3bf30` | absent | absent | `InterfaceMissing` |
| 5 | `wlan0` | DORMANT | disconnected | `WifiDisabled` |
| 1 | `enxc8a3627ddf6a` | DOWN | unavailable | `Normal` |
| 7 | `enxc8a3627e60c1` | DOWN | unavailable | `Normal` |
| 3, 6 | — | UP | connected | `Normal` |

Two of six paths move to the new group; the two genuine outages stay as normal down cards. This
distribution is the acceptance test for the classifier.

## Classification

`XBondPathStatsSnapshot` gains `Availability`:

```csharp
public enum XBondPathAvailability { Normal, InterfaceMissing, WifiDisabled }
```

Computed by a new pure static, `XBondPathAvailabilityResolver.Resolve(interfaceName, presentInterfaces, wifiDisabledInterfaces)`:

1. **`WifiDisabled`** when the interface is in the Wi-Fi disabled set. This is checked *first*: a disabled
   adapter that NetworkManager has released could also look absent, and "you turned this off" is the more
   useful explanation.
2. **`InterfaceMissing`** when `presentInterfaces` is non-empty and does not contain the interface. The
   non-empty guard matters: `InterfaceMetadataService.GetInterfacesAsync` returns an empty list on non-Linux
   hosts and when `nmcli` fails, and without the guard every path would be misreported as missing.
3. **`Normal`** otherwise, including `NO-CARRIER`.

Presence comes from the **kernel**, via `NetworkInterface.GetAllNetworkInterfaces()` — not from
NetworkManager. This was corrected during implementation: deriving presence from the nmcli list made an
existing test fail, which exposed the real flaw. nmcli's view is not authoritative about whether hardware
exists, so any path whose interface nmcli omits (or every path, when nmcli fails) would be mislabelled as
missing. Reading `/sys/class/net` through .NET is in-process, forks nothing, and cannot disagree with the
kernel. It is passed into `FromStatus` as an explicit parameter defaulting to empty, so tests stay hermetic
and existing call sites keep today's behavior.

`XBondStatsService` supplies both sets. The Wi-Fi disabled set comes from `WifiControlService`, which
depends only on its own settings, so there is no cycle. Synthetic non-configured rows are built from the
interface list itself and are therefore always `Normal`.

## Dashboard

- `ShowOnDashboard` extends to `... || Availability != Normal`, so an absent-interface path stops being
  invisible.
- `XBondStatsSnapshot` gains `UnavailablePaths` (dashboard paths where `Availability != Normal`), and
  `ActivePaths`/`StandbyPaths` exclude them so they cannot appear twice.
- Ordering places them last, under an **Unavailable** header styled like the existing "Actively Redundant"
  group header but muted (slate rather than accent).
- Their cards use slate borders and drop the throughput/latency/loss row, replacing it with a single reason
  line:
  - `InterfaceMissing` → `Adapter not present` plus the bound interface name.
  - `WifiDisabled` → `Wi-Fi disabled in Settings`.
- Grid rows: `Home.razor` positions cards with explicit `grid-row` values derived from the ordered path
  list. The unavailable group adds one header row, so the row calculation must account for it; the existing
  `GetPathGridStyle`/`GetActiveGroupGridStyle` maths is extended rather than duplicated.

## Removal

`AdapterDetailsSheet` gains a *Remove from uLink* `SlideToConfirm` (tone `warning`), rendered only for
paths that are `IsConfigured` and not `Normal`. It calls
`XBondClientConfigService.SetInterfaceEnabledAsync(interfaceName, enabled: false)`, which rewrites
`/etc/xbond/client.toml` and restarts `xbond-client.service`. The result message is surfaced in the sheet,
including the failure case where `AllowServiceControl` is false.

Slide-to-confirm matches how the Starlink destructive actions already gate themselves, so restarting the
tunnel is never one stray tap away.

## Error Handling

- Empty interface list (non-Linux, `nmcli` missing/failing) yields `Normal` for everything — the feature
  degrades to today's behavior rather than mislabelling every adapter.
- A failed removal leaves the path in place and shows the returned error; the dashboard keeps refreshing.
- Removal is guarded by the existing `IsSafeInterfaceName` and `AllowServiceControl` checks in
  `XBondClientConfigService`; this change adds no new privileged path.

## Testing

xUnit in `XNetwork.Tests`:

- `XBondPathAvailabilityResolver`: missing interface, Wi-Fi disabled, disabled-beats-missing, present-but-down
  stays `Normal`, empty-list guard, case-insensitive matching.
- `XBondStatsService.FromStatus` reproducing the ground-truth table above: exactly paths 4 and 5 classified,
  1 and 7 `Normal`.
- `ShowOnDashboard` true for an absent-interface configured path.
- `UnavailablePaths` populated and excluded from `ActivePaths`/`StandbyPaths`.

## Deployment

App-only, no Rust: router-side `./deploy.sh`. Verify the deployed version, that all routes return 200, and
that the dashboard shows `enxb8d4bcc3bf30` as `Adapter not present` and `wlan0` as `Wi-Fi disabled in
Settings` in the Unavailable group, while Starlink and `enxc8a3627e60c1` remain normal down cards.

Expect **one more card** than today, not fewer: the absent adapter becomes visible.

Version bump with a matching `AppChangelog` entry and an `AGENTS.md` journal entry.
