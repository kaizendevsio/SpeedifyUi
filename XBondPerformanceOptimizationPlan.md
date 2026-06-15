# XBond Performance Optimization Plan

Date: 2026-06-15

## Goal

Use diagnostics as engineering tooling, not as normal user-facing dashboard UI. The user-facing XBond page should expose the simple tunnel speed test only. Deep tests should be run by the agent/operator to identify Rust client/server bottlenecks and validate improvements.

## Current Findings

- The Performance Matrix UI is too noisy and should not be exposed as a regular app workflow.
- Native adapter rows currently do not measure native speed because the VPS only exposes `xbond-iperf3.service` on the tunnel IP `10.250.0.1:5201`; native tests need a separate deliberately exposed native/public iperf endpoint.
- Matrix mode changes restart `xbond-client.service` repeatedly. That disrupts the tunnel and can produce misleading iperf failures such as `Connection reset by peer`.
- Latest matrix samples showed XBond tunnel throughput can reach roughly 120-135 Mbps down, but public speedtest can fall much lower under live conditions.
- Duplicate/FEC runs showed high loss on backup/return paths, and server logs showed frequent return schedule changes between paths during active traffic.
- CPU attribution is incomplete in the matrix because process CPU samples are often null; this blocks confident CPU-vs-network classification.

## Evidence Reviewed

- Latest live artifact reviewed: `/var/lib/xnetwork/diagnostics/performance-matrix-20260615-154531.json`.
- Native adapter measurements in that artifact all failed because `45.77.241.247:5202` was not running a native/public iperf listener; they should not be interpreted as adapter-speed results.
- XBond anchor-only reported upload about `13.9 Mbps`, RTT about `204 ms`, and `Connection reset by peer`; client logs show this run happened while the matrix restarted `xbond-client.service`.
- XBond duplicate and FEC runs reached about `123-132 Mbps` down and `37-47 Mbps` up, but reported high path loss (`50%` duplicate, `30%` FEC) and retransmits.
- Public speedtest through XBond was much lower than tunnel-local iperf at about `40 Mbps` down and `29 Mbps` up.
- Client logs show repeated `xbond-client.service` restarts to switch `anchor-only`, `anchor-duplicate-1`, and `anchor-fec`.
- Server logs show frequent `return-schedule-updated` events, including path flips during active traffic, so scheduler churn/hysteresis is a likely optimization target.

## Implementation Plan

1. Clean the user-facing `/xbond` page.
   - Keep runtime summary, service status, path roles, and the manual tunnel speed test.
   - Remove heartbeat, multi-path, bad-backup simulation, matrix, MTU sweep, and scoped-route controls from the visible UI.
   - Keep backend diagnostic services available for operator/agent use.

2. Make diagnostics non-disruptive.
   - Stop using repeated `xbond-client.service` restarts as the primary benchmarking mechanism.
   - Add a Rust control path for temporary mode/policy overrides or a separate diagnostic client instance that does not own production routing.
   - Ensure each benchmark run has a stable warm-up period and does not reset the tunnel mid-test.

3. Add a safe native throughput baseline.
   - Provide an operator-only way to run temporary public/native iperf on the VPS, ideally firewalled to current Pi egress IPs and auto-stopped after the run.
   - Measure each physical adapter with `iperf3 --bind-dev <iface>`.
   - Compare native adapter, XBond tunnel-local, and public speedtest results in a saved artifact.

4. Fix measurement gaps.
   - Capture client and server CPU/RSS during tests.
   - Record packet rate, queue depth, retransmits, send failures, stale ACK age, duplicate usefulness, and scheduler decisions per run.
   - Include server-side counters in the artifact, not only Pi-side status.

5. Improve scheduler stability.
   - Add hysteresis before switching anchor/backup roles during active bulk traffic.
   - Penalize paths whose duplicate traffic arrives late or causes retransmits without contributing first-arrival packets.
   - Keep unstable backups in probe-only until they prove useful over a sustained window.

6. Improve bulk throughput policy.
   - For `Balanced`, use anchor-first for bulk TCP and duplicate only small/interactive or loss-triggered packets.
   - For `Reliable`, keep duplicate protection but avoid letting backup send stalls block anchor sends.
   - Evaluate FEC only after scheduler stability is improved.

7. Optimize Rust dataplane hot path.
   - Audit packet clone/allocation count on client and server.
   - Batch TUN reads/writes where practical.
   - Keep status/log writes off the hot path.
   - Measure encryption/sealing cost and consider buffer reuse.

8. Validate with repeatable soak tests.
   - Wi-Fi only.
   - Wi-Fi plus one bad modem.
   - Good modem plus bad modem.
   - Bulk speedtest while duplicate mode is active.
   - Moonlight/interactive session while a backup degrades.

## Acceptance Criteria

- `/xbond` is clean and user-facing, with only simple speed testing exposed.
- Engineering diagnostics produce artifacts without requiring the user to click through noisy panels.
- Native adapter baseline is available when deliberately enabled.
- A bad backup no longer drags down a healthy anchor during bulk traffic.
- XBond tunnel-local throughput moves closer to native adapter throughput, with measured evidence for each optimization.
