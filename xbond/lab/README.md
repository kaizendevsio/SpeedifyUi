# XBond Local Network-Condition Lab

This lab runs the real release builds of `xbond-client` and `xbond-server` in
privileged Linux network namespaces on the `xeon-dev` Docker context. It does
not replace the protocol with a simulator.

## Topology

The container creates:

- one client namespace with `cpath1`, `cpath2`, `cpath3`, and `xbond0`
- one server namespace with `spath1`, `spath2`, `spath3`, and `xbonds0`
- three independent router namespaces, one for each physical path
- a common server loopback endpoint at `10.255.0.1:8444`
- a TUN endpoint pair at `10.250.0.2/30` and `10.250.0.1/30`

Each client path uses its own source address, policy-routing table, Linux
interface, UDP socket, router namespace, and server return route. Impairment is
applied on the routers with `tc netem` or a narrow iptables rule.

## One-command usage

From PowerShell:

```powershell
cd C:\Users\Xeon\RiderProjects\SpeedifyUi\xbond\lab
.\run.ps1 topology-smoke
```

Run one validation scenario:

```powershell
.\run.ps1 all-intermittent
.\run.ps1 heavy-bidirectional
.\run.ps1 silent-blackhole
.\run.ps1 server-process-restart
.\run.ps1 server-tun-write-backpressure
.\run.ps1 mtu-sweep
```

Run the full matrix:

```powershell
.\run.ps1 matrix -DurationSeconds 1800
```

The full matrix and standalone `soak` scenario require at least 1,800 seconds
(30 minutes). The wrapper rejects shorter durations instead of reporting a
short run as a soak result.

For a 60-minute soak:

```powershell
.\run.ps1 soak -DurationSeconds 3600
```

Use `-NoBuild` after the image has already been built and the Rust source has
not changed.

The PowerShell wrapper requires the `xeon-dev` Docker context and fails clearly
when that environment is unavailable. It does not silently fall back to local
Docker. Results remain inside the temporary remote container until the wrapper
copies them back to `lab/results`; the container is then removed. The wrapper
never relies on a Windows bind path being visible to the remote daemon.

## Scenarios

| Scenario | Coverage |
| --- | --- |
| `topology-smoke` | Three physical paths, real TUNs, client/server startup, tunnel ping |
| `healthy-single` | Single-path RTT, loss, throughput, CPU/RSS |
| `anchor-bad-backup` | Stable path with two impaired backups |
| `all-intermittent` | Delay, jitter, correlated loss, and reordering on all paths |
| `heavy-bidirectional` | Saturated upload/download with concurrent tunnel health checks |
| `silent-blackhole` | Drops only XBond UDP on one path while direct path ICMP remains healthy |
| `usb-reenumeration` | Deletes/recreates a client veth with the same name and a new ifindex |
| `tun-read-failure` | Injects a client TUN read failure after 32 packets and verifies prompt non-zero exit without taking down the server |
| `server-process-restart` | Restarts the server under traffic and verifies authenticated automatic session replacement without restarting the client process |
| `tun-write-backpressure` | Adds client TUN write delay under load and verifies bounded queue pressure without blocking status/control progress |
| `server-tun-write-backpressure` | Exercises transient bounded server TUN pressure while control remains responsive, then verifies severe sustained saturation fails closed instead of hanging the session supervisor |
| `clock-rollback` | Restarts the client with a two-hour wall-clock rollback using libfaketime |
| `clock-skew` | Starts the client two hours behind the server |
| `stale-return-schedule` | One-way client-to-server outage, then schedule/data resynchronization |
| `queue-saturation` | Very small runtime queues plus high parallel TCP load |
| `mtu-sweep` | DF ping and throughput at TUN MTUs 1200, 1300, 1400, and 1450 |
| `soak` | Configurable impairment soak with RSS, ping, telemetry, and periodic throughput |

### Required lab-only runtime hooks

The TUN scenarios use deterministic operator/lab-only fault flags:

- `--lab-fail-tun-read-after-packets <n>`
- `--lab-tun-write-delay-ms <ms>`

If the built client does not expose a required flag, the scenario emits a
machine-readable `blocked` result and the full matrix fails. This is
intentional: TUN read failure and write backpressure are required cases and
cannot be replaced by a weaker network approximation.

Schedule-control-only loss is also not selectable at the network layer because
control and payload frames share one encrypted UDP flow. The
`stale-return-schedule` scenario therefore uses a documented one-way flow
outage and marks that coverage as partial in its result.

## Results

Results are written under `lab/results` as timestamped JSON. Each scenario
includes:

- pass/fail/blocked/error status and thresholds
- tunnel and physical-path ping/loss/RTT
- iperf3 upload/download throughput and TCP retransmits where available
- client/server process CPU and RSS
- full client and server status snapshots
- extracted queue, repair, rebind, socket generation, late, drop, reorder, TUN,
  and FEC telemetry
- qdisc statistics for impaired scenarios
- cleanup verification for namespaces, processes, and managed qdisc state
- reproducibility provenance: Git commit/branch/dirty state, Docker context and
  host, engine/kernel/OS, built image ID/digests, and exact client/server binary
  SHA-256 hashes

Server TUN telemetry includes both current and peak queue depth. Queue capacity
is retained until the blocking kernel write completes, so these values measure
actual outstanding write work rather than only channel residency.

`result.schema.json` documents the stable result envelope. Runtime telemetry is
kept as an open object so newly added XBond counters are captured without
requiring a lab schema change. The wrapper passes the same provenance values to
the container as `LAB_*` environment variables and appends them to each newly
copied scenario result. The matrix summary is an index of its scenario results;
the schema applies to the individual scenario JSON files.

## Interpreting failures

Thresholds are intentionally conservative and are recorded in every result.
A failed scenario is evidence to investigate, not a flaky test to hide. Host
CPU contention in Docker Desktop can affect absolute throughput, so compare
relative results from the same machine and image when evaluating performance
changes.

The lab always attempts cleanup. A scenario that otherwise passes is changed
to `fail` if an `xbl-*` namespace, managed process, or managed `netem` qdisc
remains.
