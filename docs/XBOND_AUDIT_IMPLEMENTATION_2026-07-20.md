# XBond Audit Implementation Record

Date: 2026-07-20
Branch: `feature/xband-only-runtime`
Source audit: `docs/XBOND_DEEP_AUDIT_2026-07-19.md`
Validation environment: Docker context `xeon-dev`

## Finding Traceability

| Priority | Finding | Implementation | Validation |
|---|---|---|---|
| High | Blocking TUN writes | Dedicated bounded and supervised client/server TUN writers; reordered output is admitted as one atomic bounded batch. Transient client and server saturation waits asynchronously for bounded capacity while control work continues; sustained saturation ends the session cleanly. Queue capacity remains reserved until the blocking write finishes, and peak depth is reported. | Unit tests plus client/server TUN write-backpressure scenarios |
| High | TUN reader failure | Reader exit is reported to the supervisor and causes a clean nonzero process exit | Unit tests plus injected TUN read-failure scenario |
| High | Clock-derived session identity | Random session epochs and explicit authenticated session synchronization. Server control generations use a wall-clock restart epoch so a restarted server cannot regress below the client's last accepted generation. An unknown session triggers authenticated in-process client session rotation instead of requiring a client process restart. | Clock rollback/skew and server-process-restart scenarios |
| High | Silent stale UDP paths | Rate-limited per-path hot rebind after stale ACKs and a successful interface-bound direct probe; repeated ineffective rebinds escalate to a clean session restart | Silent-blackhole and USB re-enumeration scenarios |
| High | Primary return loss under saturation | Primary return copy gets bounded backpressure and a send deadline; repeated expiry causes a clean restart | Queue-saturation and bidirectional-load scenarios |
| High | Stale return schedule | Acknowledged schedule generations; fresh scheduled peers remain usable; irrecoverable synchronization loss ends the session cleanly | Stale-return-schedule scenario |
| Medium | Cross-host wall-clock expiry/RTT | Packet acceptance and reorder timing use local monotonic time; peer wall clock remains diagnostic only | Clock rollback/skew scenarios |
| Medium | Oversized client receive buffers | MTU-sized bounded receive storage and queue byte telemetry | Load, queue-saturation, and soak RSS checks |
| Medium | Incorrect queue-pressure source | Real sender-lane queue depth, capacity, age, drop, and completion telemetry feeds scheduling | Unit tests and queue-saturation scenario |
| Medium | Invalid FEC pairing | FEC pairs only consecutive packets in the same session/schedule state and clears on eligibility changes | Unit tests |
| Medium | Stale reorder deadlines | Existing pending packet deadlines are recomputed when hold changes | Unit tests and intermittent-path scenario |
| Medium | Forced harmful recovery backup | Harmful backup inclusion requires recent positive duplicate usefulness; recovery may run anchor-only rather than force a damaging path | Scheduler tests and intermittent-path scenario |

## Additional Verified Optimizations

| Optimization | Implementation | Validation |
|---|---|---|
| Dedicated client control and repair lanes | Reserved bounded lanes, send deadlines, explicit counters, and latest-value schedule control semantics | Unit tests and heavy bidirectional load |
| Repair cache sizing | Byte-bounded adaptive cache plus limited pre-recovery gap repair | Unit tests and impairment scenarios |
| Replay-window ordering | Acceptance no longer depends on peer wall clock; per-session/class high-water replay windows replace FIFO forgetting and cover payload, control, and ACK frames. Per-path heartbeats use path-isolated replay streams and aggregate heartbeats use a reserved sequence prefix, preventing unrelated heartbeat streams from rejecting each other. | Protocol/control/heartbeat replay tests and clock scenarios |
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
| Client TUN write backpressure | Pass: transient saturation waited for bounded atomic capacity while the client and control plane remained active | `20260720-021003-tun-write-backpressure.json` |
| Server TUN write backpressure | Pass: transient pressure kept control responsive; severe sustained saturation failed closed without killing the client | `20260720-014556-server-tun-write-backpressure.json` |

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
