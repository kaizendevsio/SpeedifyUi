# XBond Audit Implementation Record

Date: 2026-07-20
Branch: `feature/xband-only-runtime`
Source audit: `docs/XBOND_DEEP_AUDIT_2026-07-19.md`
Validation environment: Docker context `xeon-dev`

## Finding Traceability

| Priority | Finding | Implementation | Validation |
|---|---|---|---|
| High | Blocking TUN writes | Dedicated bounded and supervised client/server TUN writers. Reordered output is admitted in ordered capacity-bounded chunks that retain one admission deadline. Admission pressure is represented as supervisor state on both sides so control and status work continue while payload admission waits. Sustained saturation ends the session cleanly. Queue capacity remains reserved until the blocking write finishes, and peak depth is reported. | Unit tests plus client/server TUN write-backpressure scenarios |
| High | TUN reader failure | Reader exit is reported to the supervisor and causes a clean nonzero process exit | Unit tests plus injected TUN read-failure scenario |
| High | Clock-derived session identity | Random session epochs and explicit authenticated session synchronization. Caller-supplied session IDs were removed. Server control generations use a wall-clock restart epoch so a restarted server cannot regress below the client's last accepted generation. Nonce derivation also includes authenticated wire send time to prevent nonce reuse if a retired 64-bit epoch is ever repeated. An unknown session triggers authenticated in-process client session rotation instead of requiring a client process restart. | Clock rollback/skew, crypto, and server-process-restart scenarios |
| High | Silent stale UDP paths | Rate-limited per-path hot rebind after stale ACKs and a successful interface-bound direct probe. Repeated failure demotes only the bad optional path while a healthy aggregate tunnel remains available; confirmed aggregate failure escalates to a clean authenticated-session reconnect. | Silent-blackhole and USB re-enumeration scenarios |
| High | Primary return loss under saturation | Primary return copy gets bounded backpressure and a send deadline; repeated expiry causes a clean restart | Queue-saturation and bidirectional-load scenarios |
| High | Stale return schedule | Acknowledged schedule generations; fresh scheduled peers remain usable; irrecoverable synchronization loss ends the session cleanly | Stale-return-schedule scenario |
| Medium | Cross-host wall-clock expiry/RTT | Packet acceptance and reorder timing use local monotonic time; peer wall clock remains diagnostic only | Clock rollback/skew scenarios |
| Medium | Oversized client receive buffers | MTU-sized bounded receive storage and queue byte telemetry | Load, queue-saturation, and soak RSS checks |
| Medium | Incorrect queue-pressure source | Composite pressure from real data/control/repair lane depth, capacity, oldest age, and newly observed enqueue/deadline drops feeds scheduling; heartbeat pending state remains separate | Unit tests and queue-saturation scenario |
| Medium | Invalid FEC pairing | FEC pairs only consecutive packets in the same session/schedule state and clears on eligibility changes | Unit tests |
| Medium | Stale reorder deadlines | Existing pending packet deadlines are recomputed when hold changes | Unit tests and intermittent-path scenario |
| Medium | Forced harmful recovery backup | Harmful backup inclusion requires recent positive duplicate usefulness; recovery may run anchor-only rather than force a damaging path | Scheduler tests and intermittent-path scenario |

## Additional Verified Optimizations

| Optimization | Implementation | Validation |
|---|---|---|
| Dedicated client control and repair lanes | Reserved bounded lanes, send deadlines, explicit counters, and latest-value schedule control semantics | Unit tests and heavy bidirectional load |
| Repair cache sizing | Allocation-byte-bounded adaptive cache, byte-budget-scaled packet allowance, and limited pre-recovery gap repair | Unit tests and impairment scenarios |
| Replay-window ordering | Acceptance no longer depends on peer wall clock; per-session/class high-water replay windows replace FIFO forgetting and cover payload, control, and ACK frames. Per-path heartbeats use path-isolated replay streams and aggregate heartbeats use a reserved sequence prefix, preventing unrelated heartbeat streams from rejecting each other. | Protocol/control/heartbeat replay tests and clock scenarios |
| Protocol direction separation | Wire protocol v2 authenticates an explicit server-to-client direction flag. Request, response, data, control, repair, and ACK nonces cannot collide across directions, and reflected frames are rejected by the receiving role. | Crypto, protocol-version, reflection, and ACK-direction tests |
| Watchdog restart accounting | A failed service restart does not consume cooldown or hourly restart quota, and the next scheduled check retries the same mismatch | .NET watchdog tests |
| PMTU evidence | Per-path/server `EMSGSIZE` telemetry and outer-fragment capture in the lab | MTU sweep |

## Pre-Candidate Blocker Results

These targeted runs use the latest source state before the reproducible release
candidate commit. They close the blockers discovered while expanding the
matrix; the full clean-candidate matrix below remains the release gate.

| Scenario | Result | Evidence |
|---|---|---|
| All paths intermittent | Pass: 0% tunnel ping loss; 4.41% of healthy aggregate throughput retained, above the 3% floor | `20260720-014749-all-intermittent.json` |
| Server process restart | Pass: client PID unchanged; authenticated session replaced; recovery in about 1.05 seconds | `20260720-015051-server-process-restart.json` |
| Client TUN write backpressure | Pass: the actual client writer reached depth 8, wrote 16 packets after 2.91 seconds of bounded queue wait, kept control/status progress active, and recovered with 0% ping loss | `20260720-033944-tun-write-backpressure.json` |
| Server TUN write backpressure | Pass: transient pressure kept control responsive; severe sustained saturation failed closed without killing the client | `20260720-034036-server-tun-write-backpressure.json` |
| Queue saturation | Pass with intentionally tiny 32/64 packet queues: 4.49 Mbps upload, 72.08 Mbps download, 6.25% concurrent ping loss, zero all-copy drops, and clean post-pressure recovery | `20260720-035242-queue-saturation.json` |
| Stale return schedule | Pass: a fresh acknowledged schedule converged in 1.86 seconds, aggregate recovery loss was 5.88%, final loss was 0%, and the client PID did not change | `20260720-035849-stale-return-schedule.json` |
| Silent UDP blackhole | Pass: the first confirmed blackhole triggered a path-only hot rebind without changing the client PID; continued ineffective rebinding escalated to a clean session restart in 39.51 seconds and recovered with 0% tunnel ping loss | `20260720-041054-silent-blackhole.json` |

These pre-candidate artifacts intentionally record `git_dirty: true` because
they were used to discover and close blockers before the release-candidate
commit. They are diagnostic evidence only. The release gate below requires a
fresh image built from committed XBond sources with `git_dirty: false`.

The release lab also rejects incomplete iperf JSON, validates every descendant
and process group was cleaned up, requires no remaining namespace processes or
netem qdiscs, and records independent queue/counter trends during the soak.

## Required Validation Matrix

Results are written to `xbond/lab/results/` as schema-validated JSON. A scenario is not complete until its result records the exact Git commit/dirty state, Docker engine and kernel, image ID, and client/server binary hashes.

| Scenario | Status |
|---|---|
| Healthy Wi-Fi only | Pending final binaries |
| Healthy anchor plus bad backup | Pending final binaries |
| All paths intermittent | Pending final binaries |
| Heavy upload and download | Pending final binaries |
| Silent UDP blackhole | Pending final binaries |
| USB re-enumeration | Pending final binaries |
| TUN read failure | Pending final binaries |
| Server process restart | Pending final binaries |
| Client TUN write backpressure | Pending final binaries |
| Server TUN write backpressure | Pending final binaries |
| Client clock rollback | Pending final binaries |
| Client/server clock skew | Pending final binaries |
| Stale return schedule | Pending final binaries |
| Queue saturation | Pending final binaries |
| MTU sweep | Pending final binaries |
| 30-minute soak | Pending final binaries |

## Completion Conditions

- All Rust and .NET tests pass.
- Every short validation scenario passes on `xeon-dev`.
- The full matrix includes an uninterrupted soak of at least 1,800 seconds.
- Paired client/server binaries are deployed and all production services and routes are verified.
- Final independent client, server/protocol, and lab audits have no unresolved high-severity findings.
