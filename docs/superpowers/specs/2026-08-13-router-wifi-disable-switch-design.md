# Per-Adapter Router Wi-Fi Disable Switch

Date: 2026-08-13
Branch: `feature/xband-only-runtime`
Status: Approved design

## Problem

The uLink router (Raspberry Pi) has 11 saved wireless profiles, every one with
`connection.autoconnect yes`. NetworkManager therefore re-joins any known network opportunistically,
and there is no way to stop it from the app. When a known Wi-Fi network is the same network already
reached over a wired uplink, the router ends up dual-homed on one subnet, which breaks routing
assumptions for uLink paths.

`WifiService` today can list adapters, read status, scan, and connect. It cannot disconnect, and
nothing can prevent an automatic re-join.

## Goals

- A per-adapter switch that stops a Wi-Fi adapter from connecting to any network.
- The switch is enforced: reboots, manual `nmcli` use, and NetworkManager roaming cannot silently
  re-enable a disabled adapter.
- Existing Wi-Fi scan and connect flows keep working for adapters that are still enabled.

## Non-Goals

- Changing saved wireless profiles. `connection.autoconnect` on the 11 stored profiles is left
  alone, so re-enabling an adapter restores normal behavior with no cleanup.
- Removing a disabled adapter from uLink path membership. A disabled adapter's path shows as
  down/no-carrier, exactly like an unplugged modem; membership stays under Settings > uLink Adapters.
- A control on the `/wifi` page. That page manages Cudy access-point clients, not the router's own
  Wi-Fi client, so the switch belongs only in Settings > Router Wi-Fi.
- Global `nmcli radio wifi off`. It cannot express a per-adapter choice.

## Verified Environment Facts

- `xnetwork.service` runs as `xeon-network`, which has passwordless sudo.
- `nmcli device set wlan0 autoconnect yes` succeeds as that user without sudo (exit 0), so polkit
  already permits per-device control. A `sudo -n nmcli` retry is kept only as a fallback.
- `nmcli radio wifi` is `enabled`; `wlan0` is connected; `rfkill` shows no soft or hard block.

## Mechanism

Disable one adapter:

1. `nmcli device set <iface> autoconnect no`
2. `nmcli device disconnect <iface>`

Enable one adapter:

1. `nmcli device set <iface> autoconnect yes`

Enabling does not force a connection; NetworkManager re-joins a known network on its own, which
matches how the adapter behaved before it was disabled.

The adapter stays *managed*, so scanning and the existing connect flow keep working. Only automatic
joining is removed, plus the app-level guards below.

### Enforcement

`WifiControlService` runs an enforcement pass immediately at startup, immediately after any save, and
then every 30 seconds. For each adapter marked disabled, it reads `nmcli -t -f DEVICE,TYPE,STATE`
device status. If the adapter is connected or connecting, the service re-applies the disable steps and
logs a warning with the observed state.

**Known limitation:** device-level `autoconnect` is runtime state that NetworkManager does not
persist. After a reboot there is a several-second window, before uLink's first enforcement pass, in
which NetworkManager may briefly auto-join. Closing that window would need an NM `conf.d` drop-in
with `managed=0`, which persists but breaks scanning; that is deliberately out of scope.

## Components

- `XNetwork/Models/WifiControlSettings.cs` — `DisabledInterfaces` (interface -> disabled) and
  `EnforcementIntervalSeconds` (default 30, clamped 10-3600).
- `XNetwork/Models/WifiControlStatus.cs` — per-adapter enforcement state for the UI: interface,
  disabled flag, last observed device state, last applied time, last error, and a re-assert count.
- `XNetwork/Services/WifiControlSettingsStore.cs` — persists `wifi-control-settings.json` under the
  service user's `XNetwork` config directory, following `AdapterIdentitySettingsStore`.
- `XNetwork/Services/WifiControlService.cs` — singleton plus hosted service. Public surface:
  `IsDisabled(string interfaceName)`, `GetStatus()`, `SetDisabledAsync(interfaceName, disabled, ct)`,
  `EnforceAsync(ct)`. Takes an injectable command-runner delegate so tests never spawn `nmcli`.
- Pure statics on `WifiControlService` for the testable logic, mirroring
  `XBondClientWatchdogService.Evaluate`: `Evaluate(bool disabled, string deviceState)` returning
  `WifiEnforcementAction` (`None` or `Disconnect`), `BuildAutoconnectArgs`, `BuildDisconnectArgs`,
  and `ShouldRetryWithSudo(int exitCode, string error)`.

`WifiControlService` parses device status with the existing static
`WifiService.ParseWifiInterfaces`, so it does not depend on `WifiService`. The dependency direction is
`WifiService` -> `WifiControlService` and `NetworkMonitorService` -> `WifiControlService`; nothing
points back, so there is no cycle.

## Guards

These are what make the switch stick rather than merely reflect a preference:

- `WifiService.ConnectAsync` refuses when the target adapter is disabled, returning
  "Wi-Fi is disabled for <iface> in Settings" instead of running `nmcli`.
- `NetworkMonitorService` skips restarting an interface that is Wi-Fi-disabled. It restarts links with
  `ip link set <dev> up`, which would otherwise resurrect a disabled adapter. The new dependency is an
  optional constructor parameter so existing construction sites and tests keep compiling.
- Settings disables the Scan and Connect controls while the selected adapter is disabled.

## UI

Settings > Router Wi-Fi gains a per-adapter list. Each row shows the adapter display name, its
interface, a state line, and a checkbox matching the existing settings-toggle markup
(`h-5 w-5 accent-cyan-400`). The state line reads one of:

- `Connecting allowed` for an enabled adapter.
- `Disabled - enforced <n>s ago` for a disabled adapter, using `TelemetryFormatter.FormatLastUpdated`.
- The last error, in rose text, when the last apply failed.

Toggling applies immediately, the way Wi-Fi controls elsewhere on the page already behave; it does not
wait for a Save button. A newly-seen adapter defaults to enabled, so nothing changes until a switch is
flipped.

## Error Handling

- Non-Linux or missing `nmcli`: the switch renders as unsupported and no commands run, matching
  `WifiService`'s existing checks.
- A failed command is recorded in `WifiControlStatus.LastError`, surfaced in the UI, and retried on the
  next enforcement pass rather than throwing into the render path.
- `ShouldRetryWithSudo` triggers one `sudo -n nmcli` retry when the failure text indicates a
  permission or authorization problem.
- `OperationCanceledException` on shutdown is swallowed, as in the other hosted services.

## Testing

xUnit in `XNetwork.Tests`:

- `Evaluate` decision table: disabled + connected/connecting -> `Disconnect`; disabled +
  disconnected/unavailable/unmanaged -> `None`; enabled + any state -> `None`.
- `BuildAutoconnectArgs` / `BuildDisconnectArgs` exact argument order.
- `ShouldRetryWithSudo`: authorization failures retry; a generic failure does not.
- Settings store round-trip, including that an unknown interface reads back as enabled.
- `SetDisabledAsync` issues autoconnect-then-disconnect in order, via a fake command runner.
- Enforcement re-applies for a drifted adapter and does nothing for a compliant one.
- `WifiService.ConnectAsync` guard returns the refusal without invoking the runner.

## Deployment

App-only change, no Rust. Router-side `./deploy.sh` over SSH, then live verification: toggle `wlan0`
off, confirm `nmcli` reports it disconnected with `autoconnect no`, confirm the dashboard shows the
path down, wait through one enforcement pass to confirm it stays down, then restore the operator's
preferred state.

Version bump with a matching `AppChangelog` entry, plus an `AGENTS.md` journal entry.
