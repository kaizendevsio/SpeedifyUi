# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

uLink is a bonded multi-WAN router stack. It combines several consumer internet connections — fibre, Starlink, and LTE/5G modems — into one tunnel, and presents a dashboard for monitoring and controlling it.

Two halves:

- **XNetwork** — a .NET 9 Blazor Server dashboard, served on port 8080, running on the router.
- **xbond** — a Rust workspace implementing the tunnel itself: an encrypted UDP overlay that schedules packets across the available WAN paths.

The dashboard reads the tunnel's state; it does not carry traffic. Both must be deployed together whenever shared code changes.

## Technology Stack

- **.NET 9.0** with Blazor Server (`@rendermode InteractiveServer`), xUnit for tests
- **Rust** (stable) for the `xbond` workspace
- **UI**: Tailwind CSS, Chart.js
- **Real-time updates**: SignalR, built into Blazor Server
- **Router-side integration**: nftables, `ip` policy routing, dnsmasq, systemd, NetworkManager

## Commands

```bash
# .NET
dotnet build
dotnet test XNetwork.Tests/XNetwork.Tests.csproj
dotnet run --project XNetwork/XNetwork.csproj

# Rust (from xbond/)
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
```

Both suites are expected to pass and clippy is expected to be clean before deploying.

## Architecture

### xbond (Rust)

- **xbond-core** — shared logic. The interesting parts are `health.rs` (path scoring and role selection: anchor, backup, probe, trial), `anchor.rs` (score smoothing, stability, flap damping, anchor trials), `scheduler.rs` (per-packet transmission plans and redundancy policy), plus `protocol.rs`, `fec.rs`, `reorder.rs`, `repair.rs`.
- **xbond-client** — runs on the router. Owns the TUN device, the per-path UDP sockets, heartbeats, and the scheduler tick. Publishes a status document to `/run/xbond/client-status.json`.
- **xbond-server** — runs on the public host and terminates the tunnel.
- **xbond-loadgen** — load generation for testing.

### XNetwork (Blazor)

Services are grouped by concern rather than layer:

- **Tunnel** — `XBondStatusService` (parses the status file into dashboard models), `XBondStatsService`, `XBondSnapshotCache`, `XBondSpeedTestService`, `XBondClientWatchdogService`.
- **Traffic policy** — `TrafficBypassService` and `TrafficBypassReconcileService` drive an nftables/policy-routing helper so selected domains and destinations leave a chosen WAN directly instead of the tunnel.
- **Per-WAN identity and health** — `AdapterIdentityService` (interface-bound ISP lookups), `InterfaceMetadataService`, `ConnectionHealthService`.
- **Hardware integrations** — `StarlinkTelemetryService` and friends (dish telemetry and LAN access), `CudyApControlService` (the downstream AP), `F50ModemRecoveryService`, `WifiService`.

Pages: `Home`, `Statistics`, `XBond`, `XRouter`, `Settings`, `AiChat`.

### Data flow

1. `xbond-client` measures each path and writes `/run/xbond/client-status.json` roughly once per second.
2. `XBondStatusService` reads and parses that file; the dashboard renders from it.
3. Control actions from the dashboard go to the client over its control socket, or to the system via helper scripts in `XNetwork/deploy/scripts/`.

## Important Implementation Details

### Reading the status file

It is written by a separate process, so treat it as a snapshot that may change under you. It is published atomically (temp file plus rename), which is what makes a plain read safe.

### Subprocess calls are a hazard on the packet path

Forking `ip`, `nmcli` and similar from inside the scheduler tick has caused real latency spikes. Anything reachable from the packet loop should read cached state instead. When latency regresses, `grep` for `Command::new` reachable from the select loop.

### Metrics feed decisions, not just the UI

Per-path `rtt_ms` / `loss_rate` are inputs to path scoring, the stability penalty, and the redundancy policy's duplication threshold. Changing how they are measured changes routing behaviour — check the consumers before adjusting a window or a constant.

### Platform

The router-side integrations (nftables, `ip`, systemd, NetworkManager, dnsmasq) are Linux-only. The dashboard builds and tests cross-platform.

## Configuration

- **Dashboard**: `XNetwork/appsettings.json` — Kestrel endpoint, and a section per service.
- **Tunnel client**: `/etc/xbond/client.toml` on the router. `xbond/examples/client.example.toml` documents every knob, including path definitions, heartbeat cadence and windows, role selection, flap damping, and anchor trials.
- **Persisted UI settings**: written under the app's data directory, then applied by the helper scripts.

## Project Structure

```
uLink.sln
├── XNetwork/              # Blazor Server dashboard
│   ├── Components/        # Layout, Pages, Custom
│   ├── Services/          # see Architecture above
│   ├── Models/
│   └── deploy/            # systemd units, dnsmasq config, helper scripts
├── XNetwork.Tests/        # xUnit
└── xbond/                 # Rust workspace
    ├── crates/            # xbond-core, -client, -server, -loadgen
    └── examples/          # client.example.toml
```

## Deployment

- `./deploy.sh` on the router — app-only changes.
- `.\deploy-xbond-paired.ps1` from Windows — anything touching the Rust runtime, so client and server stay in step.

Never leave the client and server on mismatched protocol or scheduler code.

## Common Tasks

### Changing path scoring or role selection

Work in `xbond-core`. `health.rs` and `anchor.rs` carry tests that encode hard-won behaviour — in particular that a link which flaps must not take the anchor. Treat a failure there as a real regression rather than a test to adjust.

### Adding a traffic bypass rule type

The rule model lives in `Models/TrafficBypassSettings.cs`, the reconciliation in `TrafficBypassReconcileService`, and the nftables/routing work in `deploy/scripts/xnetwork-traffic-bypass-apply.py`. Routes in the policy tables are lost whenever their interface goes down, which is why reconciliation exists.

### Modifying UI components

Tailwind, mobile-first. Reuse the components in `Components/Custom/`. Dispose timers and subscriptions.

### Debugging the tunnel

- `/run/xbond/client-status.json` is the primary source of truth for path state.
- `journalctl -u xbond-client` carries structured JSON events, including `scheduler-tick-slow` with a per-phase breakdown.
- Separate what a metric *reports* from what the network is *doing* — bind a probe to the interface and compare.
