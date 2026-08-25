# XBond Implementation Plan

Date: 2026-06-13

## Summary

Build `XBond` as a side-by-side reliability tunnel prototype before replacing the legacy VPN runtime. The packet dataplane will be Rust. XNetwork/Blazor will remain the UI and control plane.

The core invariant is: XBond must never perform worse than the best currently working adapter just because a weaker redundant adapter is present.

## Architecture

- `xbond-client` runs on `xeon-network`.
- `xbond-server` runs on `xeon-speedify-vultr-01`.
- XNetwork observes and controls the client through a local status command/API.
- V1 is a test tunnel only; it must not route all router traffic or replace the legacy VPN runtime by default.

Target flow:

```text
test traffic -> xbond0 TUN on Pi -> xbond-client -> per-adapter UDP sockets -> xbond-server on Vultr -> internet NAT
```

The first implementation should establish protocol, scheduler, status, and health foundations before enabling real TUN/NAT traffic.

## Core Protocol

- Every packet has a session ID, sequence number, path ID, send timestamp, packet kind, and authenticated payload.
- Packet kinds: `data`, `duplicate`, `fec`, `heartbeat`, and `control`.
- Receiver uses first-arrival-wins delivery.
- Late duplicates are dropped and counted.
- Packets that miss their deadline are discarded rather than blocking newer traffic.
- Encryption must use a proven AEAD such as ChaCha20-Poly1305; no custom crypto.

## Adapter Model

- Each adapter/path has an isolated queue and UDP socket.
- A bad path must not block the stable path.
- The current anchor is selected dynamically from observed health, not adapter name.
- Backups send duplicate/FEC/probe traffic only when useful.
- Path health tracks RTT, jitter, loss, late-packet rate, queue depth, throughput, and up/down state.

## Scheduling Modes

Initial modes:

- `AnchorOnly`: send over the best path only.
- `AnchorDuplicate1`: send data over anchor plus one backup duplicate.
- `AnchorFec`: send data immediately on anchor and parity/FEC on backup paths.
- `FullDuplicateDebug`: send duplicates on all available paths for diagnostics.

Default for future live testing: `AnchorFec`.

## Blazor / XNetwork

- Add an XBond status model and service.
- Add a read-only XBond UI page for status, current mode, anchor, path health, duplicate drops, and FEC counters.
- Keep the legacy runtime's controls separate so XBond can be compared side-by-side.
- Do not start/stop privileged tunnel services from the first UI implementation unless explicitly enabled later.

## Deployment Shape

Pi:

```text
/usr/local/bin/xbond-client
/etc/xbond/client.toml
/etc/systemd/system/xbond-client.service
```

VPS:

```text
/usr/local/bin/xbond-server
/etc/xbond/server.toml
/etc/systemd/system/xbond-server.service
```

Use a dedicated UDP port, for example `8444/udp`; do not reuse the legacy runtime's ports.

## Test Milestones

1. Protocol encode/decode, encryption, and duplicate-window tests pass.
2. Scheduler tests prove a flapping backup cannot displace or delay a healthy anchor.
3. `xbond-server` can listen for UDP heartbeat/control frames and return acknowledgements.
4. `xbond-client status --json` returns parseable status for XNetwork.
5. XNetwork builds and shows the XBond prototype state.
6. Later live tests validate single-path tunnel, two-path duplicate mode, bad-link simulation, and FEC.

## Defaults

- V1 scope: test tunnel only.
- Default mode: `AnchorFec`.
- Max active backups: `2`.
- Realtime packet deadline: `120 ms`.
- Anchor promotion hold: `30 s`.
- Anchor demotion threshold: sustained high latency/loss for `10-20 s`.
- Backup cooldown: `2-5 min`.
- Encryption: enabled.
- Compression: disabled.
- Throughput bonding: disabled for V1.
