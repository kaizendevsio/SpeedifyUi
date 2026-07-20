# XBond Client, Server, and Protocol Deep Audit

Date: 2026-07-19
Branch: `feature/xband-only-runtime`
Audited commit: `561c3bf8141445c7afa27dd00c4446a285cb69fe`
Scope: Rust XBond client, server, core protocol, scheduler, queues, crypto, TUN, repair, FEC, and recovery behavior

Out of scope: XNetwork visual design, Speedify, Proxmox, modem/Starlink management, and unrelated application services

## Purpose

This audit identifies current opportunities to improve:

- memory efficiency
- tunnel throughput
- latency
- recovery when several adapters degrade at the same time
- resistance to stalls that currently require restarting `xbond-client.service`

This is a read-only assessment. No source code, runtime configuration, or deployed service was changed.

## Review Method

Three independent subagents reviewed the client, server, and core protocol separately. A fourth subagent counterchecked their findings against the audited commit, removed duplicates, rejected claims that were no longer true, and separated confirmed defects from optimization opportunities.

File and line references in this report apply to the audited commit above.

The current Rust test suite was also run:

- `cargo test --workspace --all-targets --locked`
- Result: 124 tests passed

Passing tests are valuable, but several findings concern prolonged load, clock changes, queue saturation, device replacement, and simultaneous link impairment. Those cases need targeted integration and soak tests in addition to unit tests.

## Executive Summary

XBond already has several sound foundations:

- first-arrival-wins duplicate handling
- authenticated encryption with a cached cipher
- bounded queues and resend caches
- per-path UDP sockets with Linux interface binding
- scheduler demotion and adaptive recovery hold
- client path socket hot rebind support
- separate server control and payload handling

Commit `561c3bf` fixed the previously observed primary-sender control starvation. That issue must not be treated as still unresolved.

The main remaining risks are:

1. TUN writes still run synchronously inside the main async client and server loops.
2. A TUN reader can stop while the process continues to look healthy.
3. Clock-derived session IDs and cross-host wall-clock timing can reject valid traffic.
4. A silent UDP socket or NAT blackhole does not automatically trigger a path rebind.
5. Server downstream data can be dropped when a per-path return queue is full.
6. A stale return schedule intentionally stops downstream forwarding after its grace period.
7. The client receive queue can retain roughly 256 MiB of buffer capacity at its configured maximum.
8. Some scheduler telemetry does not represent the actual sender queues it is meant to protect.

These explain why a tunnel can appear healthy at the process level but stall under heavy load or after interface churn.

## Priority Overview

| Priority | Finding | Type | Main impact |
|---|---|---|---|
| High | Blocking TUN writes in main loops | Structural defect/risk | Control and heartbeat work can stall |
| High | TUN reader failure is not supervised | Defect | One traffic direction can die silently |
| High | Clock-derived session identity | Protocol defect | Restart can be rejected; nonce reuse risk |
| High | Silent stale UDP paths are not automatically rebound | Recovery defect | Path remains at 100% loss until manual action |
| High | Server primary return packets can be dropped on queue saturation | Dataplane defect | TCP retransmits and download collapse |
| High | Stale return schedule can deliberately stop downstream traffic | Reliability design risk | One-way control loss becomes full downstream outage |
| Medium | Frame expiry and RTT depend on wall-clock timestamps | Protocol defect | Clock skew can create false loss and wrong RTT |
| Medium | Client receive buffers can retain about 256 MiB | Bounded memory risk | RSS spikes and allocator pressure |
| Medium | Queue pressure is calculated from heartbeat state | Observability defect | Scheduler misses real congestion |
| Medium | FEC can pair packets that are not consecutive | Correctness defect | Parity becomes unusable or misleading |
| Medium | Recovery hold changes do not update queued deadlines | Recovery defect | Old packets use the wrong hold after mode changes |
| Medium | Recovery forces one backup even when all are harmful | Policy risk | A bad link can consume resources without helping |

## Detailed Findings

### 1. Blocking TUN writes can pause the main control loop

Severity: High
Classification: Structural defect/risk
Confidence: High

**Cause**

`XBondTun::write_packet` calls blocking `File::write_all`. Client and server receive/reorder processing call it directly from their main async supervisor loops.

**Effect**

Most TUN writes complete quickly. If the kernel TUN queue applies backpressure, however, the Tokio worker executing the supervisor can block. During that time the same loop may not process:

- heartbeat ACKs
- schedule updates
- socket rebind events
- repair requests
- health transitions

This can make all adapters appear lost even though the physical interfaces still work.

**Solution**

Add a dedicated, supervised TUN writer worker on both client and server:

- main loop sends packets to a bounded writer queue
- writer performs the blocking write outside the async supervisor
- primary traffic applies bounded backpressure
- queue depth, oldest packet age, write duration, and failures are reported
- prolonged writer failure triggers a clean session restart instead of leaving a partial tunnel alive

**Tradeoff**

A bounded queue adds a small amount of memory and implementation complexity. It also requires an explicit overload policy so latency does not grow without limit.

**Evidence**

- `xbond/crates/xbond-core/src/tun.rs:26-31`
- `xbond/crates/xbond-client/src/main.rs:2203-2217`
- `xbond/crates/xbond-server/src/main.rs:920-1004`
- `xbond/crates/xbond-server/src/main.rs:2157-2170`

### 2. TUN reader failure is not supervised

Severity: High
Classification: Defect
Confidence: High

**Cause**

Client and server start the blocking TUN reader as a detached task. On a permanent read error, the task logs the problem and exits. The main tunnel loop is not notified.

**Effect**

The process and systemd service can remain active while one direction no longer receives traffic from the TUN device. Health checks based only on process state can therefore report a running service even though the tunnel is partially dead.

**Solution**

Supervise the reader task:

- send an explicit reader-stopped event to the main loop
- attempt a bounded TUN reopen when safe
- if the TUN cannot be restored, terminate the tunnel process so systemd performs a clean restart
- expose reader generation and last successful read time in operator telemetry

**Tradeoff**

Hot reopening is smoother but more complex. Exiting for systemd restart is simpler and safer, but briefly interrupts active sessions.

**Evidence**

- `xbond/crates/xbond-client/src/main.rs:1215-1232`
- `xbond/crates/xbond-server/src/main.rs:622-643`

### 3. Session identity depends on the system clock

Severity: High
Classification: Protocol defect
Confidence: High

**Cause**

The client defaults `session_id` to the current Unix time in microseconds. The server accepts only session IDs that are greater than or equal to the latest accepted ID.

The authenticated-encryption nonce is also derived partly from that session ID. Session identity should therefore be unique and should not depend on a clock that can move backward.

**Effect**

After a clock rollback, VM restore, or time correction, a restarted client can present a lower session ID. The server then rejects its packets until the server restarts or the clock catches up.

A repeated session ID also weakens the guarantee that encryption nonces never repeat with the same key.

**Solution**

Introduce an explicit session-open handshake:

- generate a cryptographically random session epoch
- authenticate the epoch
- reset replay, reorder, repair, and return-schedule state only after accepting the new session
- compare session equality, not numeric clock order

**Tradeoff**

This is a wire-protocol change and requires a paired client/server deployment. A compatibility version or controlled cutover is required.

**Evidence**

- `xbond/crates/xbond-client/src/main.rs:1169-1175`
- `xbond/crates/xbond-server/src/main.rs:704-705`
- `xbond/crates/xbond-server/src/main.rs:2026-2028`
- `xbond/crates/xbond-core/src/crypto.rs:88-98`

### 4. Silent UDP blackholes do not automatically rebind a path

Severity: High
Classification: Recovery defect
Confidence: Medium-high

**Cause**

The client can recreate a path socket when:

- interface address or device identity changes
- an explicit operator rebind is requested
- a socket operation reports an error such as `ENODEV`

A UDP socket can also become useless without returning a socket error, for example after NAT state loss, modem firmware trouble, or a stale lower-level path. Heartbeat expiry currently records loss, but does not by itself request socket recreation.

**Effect**

The interface may have working internet outside XBond while its existing XBond UDP path remains at 100% loss. Manual path rebind or service restart restores it because a new socket and NAT mapping are created.

**Solution**

Add a rate-limited silent-blackhole detector:

1. path ACKs remain stale past a threshold
2. an interface-bound outside-tunnel probe succeeds
3. no recent rebind is already in progress
4. recreate only that path socket
5. if repeated rebinds fail, cleanly restart the XBond session

This should use hysteresis to avoid rebinding during ordinary short outages.

**Tradeoff**

Aggressive thresholds can cause unnecessary churn on intermittent mobile links. The detector needs cooldown, success confirmation, and a maximum retry rate.

**Evidence**

- `xbond/crates/xbond-client/src/main.rs:2289-2303`
- `xbond/crates/xbond-client/src/main.rs:2363-2374`
- `xbond/crates/xbond-client/src/main.rs:2408-2445`
- `xbond/crates/xbond-client/src/main.rs:2641-2654`
- `xbond/crates/xbond-client/src/main.rs:2791-2813`
- `xbond/crates/xbond-client/src/main.rs:3241-3255`

### 5. Server primary return traffic is dropped when a path queue is full

Severity: High
Classification: Dataplane defect under saturation
Confidence: High

**Cause**

The server enqueues downstream packets with `try_send`. If the selected path queue is full, the packet is skipped, including a primary `Data` packet. The implementation counts the full event but does not guarantee that one copy was accepted by any return path.

**Effect**

During a heavy download or temporarily blocked path:

- inner TCP sees loss and retransmits
- throughput can collapse
- queue loss can be mistaken for physical adapter loss
- a stable anchor may underperform even when it has available capacity after a short burst

**Solution**

Use separate semantics:

- primary return copy: isolated bounded backpressure
- duplicate/FEC copies: best-effort `try_send`
- if no primary copy can be accepted within a short deadline, record an explicit `all-return-copies-dropped` event
- keep control and repair traffic in their existing priority lane

**Tradeoff**

Waiting briefly for the primary queue protects reliability but can add queueing latency. The wait must be short and observable.

**Evidence**

- `xbond/crates/xbond-server/src/main.rs:1176-1238`
- `xbond/crates/xbond-server/src/main.rs:1435-1479`

### 6. A stale return schedule can stop all downstream forwarding

Severity: High
Classification: Reliability design risk
Confidence: High

**Cause**

The server considers the client's return schedule stale after 10 seconds and allows a further 5-second grace period. After the grace expires, target selection returns no paths.

This fail-closed behavior prevents the server from using obsolete path membership, but it converts a one-way schedule-control failure into a complete downstream outage.

**Effect**

Client-to-server traffic or individual physical paths may still be alive, while server-to-client traffic stops. The service remains running, which can look like a mysterious stall.

**Solution**

Use acknowledged schedule generations:

- client sends generation N
- server ACKs generation N
- server continues using the last acknowledged schedule while at least one referenced peer remains fresh
- if control synchronization cannot be restored, end the session and perform a clean reconnect

This matches the desired policy of fixing stale state directly rather than indefinitely falling back through increasingly inaccurate states.

**Tradeoff**

Retaining the last acknowledged schedule briefly may send packets to a path that has recently changed. The server must still reject missing or hard-stale peers.

**Evidence**

- `xbond/crates/xbond-server/src/main.rs:36-38`
- `xbond/crates/xbond-server/src/main.rs:2048-2083`

### 7. Frame expiry and RTT use wall-clock time across machines

Severity: Medium
Classification: Protocol defect under clock skew
Confidence: High

**Cause**

Frames carry `send_micros` from the sender's Unix clock. The receiver compares it with its own Unix clock for expiry. Client RTT also subtracts the echoed sender timestamp from its current wall clock even though the client already stores a local `Instant` for each pending heartbeat.

**Effect**

If client and server clocks differ or NTP steps a clock:

- valid frames can be marked expired
- RTT can jump or become misleading
- scheduler decisions can demote healthy paths
- all paths can appear lost at once

**Solution**

- use local monotonic receive deadlines for expiry and reorder decisions
- use the stored `Instant.elapsed()` for client RTT
- retain sender timestamps only for diagnostics where clock uncertainty is acceptable
- if absolute timestamps remain in the wire format, do not use them for packet acceptance

**Tradeoff**

The receiver cannot know true one-way latency without synchronized clocks. Round-trip health remains accurate and is sufficient for current scheduling.

**Evidence**

- `xbond/crates/xbond-core/src/protocol.rs:88-90`
- `xbond/crates/xbond-core/src/protocol.rs:372-392`
- `xbond/crates/xbond-client/src/main.rs:3118`
- `xbond/crates/xbond-client/src/main.rs:3290-3300`
- `xbond/crates/xbond-client/src/main.rs:3314-3325`
- `xbond/crates/xbond-client/src/main.rs:4568-4581`

### 8. Client receive buffers can retain about 256 MiB

Severity: Medium
Classification: Bounded memory and allocation risk, not an unbounded leak
Confidence: High

**Cause**

Each client path receiver:

1. allocates a 65,535-byte UDP receive buffer
2. allocates a payload `Vec` with 65,535 bytes of capacity
3. transfers that payload buffer into `InboundTunnelFrame`
4. immediately allocates another 65,535-capacity payload buffer

The default inbound channel capacity is 4,096 frames. If it fills with these buffers, retained capacity can reach:

`65,535 x 4,096 = 268,431,360 bytes`, approximately 256 MiB, excluding frame and allocator overhead.

This is a worst-case bound, not evidence of an unbounded memory leak.

**Effect**

- large temporary RSS growth
- repeated allocation/free activity at packet rate
- more allocator and cache pressure on the Raspberry Pi
- longer pauses when the consumer falls behind

**Solution**

- size payload buffers for the configured tunnel MTU plus protocol overhead
- use a bounded reusable buffer pool or slab
- return consumed buffers to the originating receiver
- cap memory by bytes as well as packet count
- record current and peak queued bytes

The server already uses a smaller expected payload capacity of 2,048 bytes, although it still allocates a replacement buffer for each accepted frame.

**Tradeoff**

Pooling requires ownership discipline and careful handling during path rebind. The pool must remain bounded so it cannot become a different source of memory growth.

**Evidence**

- `xbond/crates/xbond-core/src/config.rs:144-149`
- `xbond/crates/xbond-client/src/main.rs:2638-2666`
- `xbond/crates/xbond-client/src/main.rs:2822`
- `xbond/crates/xbond-server/src/main.rs:588-604`

### 9. Scheduler queue pressure does not measure sender queues

Severity: Medium
Classification: Observability and scheduling defect
Confidence: High

**Cause**

Path `queue_depth` and `queue_pressure` are calculated from the number of pending heartbeat ACKs. They do not use the actual path sender queue depth, capacity, oldest item age, or queue-full events.

**Effect**

The scheduler can:

- report queue pressure when the sender queue is actually empty
- miss a full sender queue while heartbeats are still being acknowledged
- keep a congested path as anchor or backup longer than intended
- misclassify queue congestion as general path loss

**Solution**

Expose real per-path sender metrics:

- current depth and capacity
- oldest queued packet age
- primary, duplicate, FEC, repair, and control drops
- send completion delay
- queue-full event rate

Use those values for queue-pressure scoring. Keep heartbeat pending count as a separate health metric.

**Tradeoff**

Exact queue metrics add atomics or small status messages between workers and the supervisor. Sampling once per scheduler tick avoids hot-path contention.

**Evidence**

- `xbond/crates/xbond-client/src/main.rs:2688-2706`
- `xbond/crates/xbond-core/src/health.rs:78-125`

### 10. FEC can combine packets that are not a valid consecutive pair

Severity: Medium
Classification: Correctness defect
Confidence: Medium-high

**Cause**

The client keeps one global `pending_fec_source`. It pairs that saved packet with the next packet for which the current transmission plan includes FEC. The saved state is not clearly invalidated when:

- an intervening packet is not FEC-eligible
- policy or schedule changes
- recovery membership changes

The FEC decoder assumes the protected packets are `base_sequence` and `base_sequence + 1`.

**Effect**

Parity may be generated from nonconsecutive packets while the receiver interprets it as a consecutive pair. Recovery then fails or reconstructs data that is rejected by length/sequence checks. Bandwidth and CPU are spent without improving delivery.

**Solution**

- pair only sequence N and N+1
- clear pending FEC state on any intervening ineligible packet
- clear it when policy, schedule generation, or recovery mode changes
- key pending state by session and schedule generation
- in a future protocol version, explicitly encode both protected sequence IDs

**Tradeoff**

Stricter pairing may produce fewer parity packets when traffic eligibility changes frequently, but every produced block will be meaningful.

**Evidence**

- `xbond/crates/xbond-client/src/main.rs:1314-1319`
- `xbond/crates/xbond-client/src/main.rs:1935-1994`
- `xbond/crates/xbond-core/src/fec.rs:78-94`
- `xbond/crates/xbond-server/src/main.rs:1590-1593`

### 11. Recovery hold changes do not update packets already waiting

Severity: Medium
Classification: Recovery defect
Confidence: High

**Cause**

Each queued reorder packet stores a fixed release deadline when it is inserted. `set_hold_micros` changes only the hold used for future packets.

**Effect**

- entering recovery does not extend the deadline of packets already pending
- exiting recovery can leave packets waiting under the previous longer recovery deadline
- measured latency can remain elevated briefly after recovery ends
- early recovery packets may be released too quickly to benefit from the new hold

**Solution**

Store local arrival time and recompute or clamp pending deadlines when hold changes:

- on recovery entry, extend pending deadlines up to the new hold
- on recovery exit, shorten pending deadlines to the normal hold
- keep an absolute maximum residence time

**Tradeoff**

Updating a bounded `BTreeMap` is an O(n) operation when the hold changes. Hold changes are infrequent, so this is preferable to incorrect packet timing.

**Evidence**

- `xbond/crates/xbond-core/src/reorder.rs:12-17`
- `xbond/crates/xbond-core/src/reorder.rs:51-74`
- `xbond/crates/xbond-core/src/reorder.rs:184-190`

### 12. Recovery forces one backup even when all backups are harmful

Severity: Medium
Classification: Reliability policy risk, not an unconditional defect
Confidence: High

**Cause**

Recovery filters out hard-ineligible paths and prefers non-harmful duplicates. If every remaining eligible backup is classified as harmful, the scheduler deliberately keeps the first candidate anyway.

This behavior is covered by a test, so it is intentional.

**Effect**

In simultaneous impairment, the chosen backup may:

- consume scarce modem upload capacity
- add encryption and copying work
- fill queues
- deliver almost no useful first-arrival packets
- indirectly increase latency on the healthy anchor

On the other hand, the backup can occasionally deliver a packet the anchor loses. Whether it helps depends on measured duplicate usefulness.

**Solution**

Make the minimum backup conditional on positive recent value:

- keep a backup only when it has delivered useful first-arrival or repair packets within a recent window
- otherwise use anchor-only data plus low-rate probes
- re-enable duplication quickly when the probe demonstrates recovery
- expose the decision reason in operator telemetry

**Tradeoff**

Anchor-only operation reduces protection if all paths are poor. This should be driven by measured delivery benefit, not a fixed rule that always removes bad backups.

**Evidence**

- `xbond/crates/xbond-core/src/scheduler.rs:401-445`
- `xbond/crates/xbond-core/src/scheduler.rs:1121-1138`

## Additional Verified Optimizations

These are worthwhile, but they rank below the main findings.

### Dedicated client control and repair lanes

Client schedule-control and repair-request functions iterate through path sockets and await sends serially. Usually UDP sends complete immediately, but a non-writable socket can delay the whole operation.

Use small reserved per-path control queues with send deadlines and coalescing. Repair frames also currently share the normal path sender queue and are dropped when it is full; a bounded priority repair lane would improve recovery under the exact load where repair is most needed.

Evidence:

- `xbond/crates/xbond-client/src/main.rs:2824-2883`
- `xbond/crates/xbond-client/src/main.rs:2929-2987`
- `xbond/crates/xbond-client/src/main.rs:2992-3042`

### Repair cache sizing

The cache is limited to 4,096 packets and three seconds. At high packet rates, 4,096 packets can represent much less than three seconds, so useful packets may be evicted before a repair request arrives.

Size the cache by bytes and measured bitrate, and allow limited gap-triggered repair before full recovery instead of waiting until recovery is active.

Evidence:

- `xbond/crates/xbond-client/src/main.rs:2818-2821`
- `xbond/crates/xbond-client/src/main.rs:2939-2948`
- `xbond/crates/xbond-server/src/main.rs:32-35`
- `xbond/crates/xbond-core/src/repair.rs:24-84`

### Replay-window ordering

The duplicate window records a frame before the expiry check. Expired authenticated frames therefore occupy duplicate-window capacity. The FIFO window can also forget an old sequence after capacity eviction.

Check expiry before duplicate insertion and use a per-session high-water sequence with a backward bitmap.

Evidence:

- `xbond/crates/xbond-core/src/protocol.rs:296-339`
- `xbond/crates/xbond-core/src/protocol.rs:372-392`

## MTU Assessment

The configured XBond MTU of 1,400 bytes is not a confirmed defect for the current IPv4 deployment.

Approximate outer IPv4 size:

- inner packet: 1,400 bytes
- XBond header: 40 bytes
- authentication tag: 16 bytes
- UDP: 8 bytes
- outer IPv4: 20 bytes
- total: approximately 1,484 bytes

This fits a standard 1,500-byte IPv4 path.

It is still a heterogeneous-path risk:

- some mobile or tunneled providers expose less than 1,500 bytes
- an IPv6 outer header would bring the same packet to roughly 1,504 bytes
- current code has no per-path PMTU learning

Recommendation:

1. add `EMSGSIZE`, fragmentation, and PMTU telemetry
2. run per-path DF/PMTU diagnostics
3. choose the lowest proven safe active-path MTU
4. keep optional TCP MSS clamping available

Do not reduce MTU blindly before measurement because a smaller MTU increases packet rate and per-packet CPU cost.

Evidence:

- `xbond/crates/xbond-client/src/main.rs:105-106`
- `xbond/crates/xbond-server/src/main.rs:88-89`
- `xbond/crates/xbond-core/src/protocol.rs:9-11`
- `xbond/crates/xbond-core/src/crypto.rs:7`

## Staged Implementation Roadmap

### Phase 1: Low-risk correctness and observability

1. Use stored monotonic `Instant` values for RTT.
2. Replace heartbeat-derived queue pressure with real sender queue metrics.
3. Notify the supervisor when a TUN reader exits.
4. Clear FEC pending state across nonconsecutive packets and schedule changes.
5. Add silent-blackhole path rebind using stale ACK plus successful direct probe.
6. Add explicit all-copies-dropped and TUN-write-latency counters.

Expected result: better diagnosis and fewer path stalls without a wire-format change.

### Phase 2: Dataplane isolation

1. Add dedicated bounded TUN writers on client and server.
2. Give client control and repair traffic reserved queues.
3. Protect the server primary return copy with bounded backpressure.
4. Replace 65,535-capacity per-frame client payload allocation with MTU-sized pooled buffers.
5. Size repair storage by bytes and observed traffic rate.

Expected result: lower allocation pressure, fewer load-induced losses, and control progress that is independent of TUN or payload backpressure.

### Phase 3: Session and protocol hardening

1. Add random authenticated session epochs and a session-open handshake.
2. Remove cross-host wall-clock expiry from packet acceptance.
3. Add acknowledged return-schedule generations.
4. Perform a clean session reconnect when schedule synchronization cannot be restored.
5. Replace FIFO duplicate tracking with a replay bitmap.

Expected result: reliable restart behavior and fewer false losses caused by stale or skewed state.

### Phase 4: Severe simultaneous-impairment tuning

1. Recompute pending reorder deadlines when recovery hold changes.
2. Make harmful-backup inclusion depend on measured duplicate usefulness.
3. Enable limited pre-recovery gap repair.
4. Tune recovery hold, repair window, and duplication using the same impairment replay.
5. Add PMTU evidence before changing MTU or MSS defaults.

Expected result: better survival when all adapters are intermittent without forcing unnecessary latency during normal operation.

## Required Validation Matrix

Each phase should be tested with repeatable captures, not only a public speed test.

| Scenario | What to verify |
|---|---|
| Healthy Wi-Fi only | Near-native throughput, CPU, RSS, RTT |
| Healthy anchor plus bad backup | Bad backup cannot slow or stall anchor |
| All paths intermittent | Packet survival, TCP retransmits, recovery latency |
| Heavy upload and download | Control ticks continue; no all-path false loss |
| Silent UDP blackhole | Per-path rebind occurs without full service restart |
| USB re-enumeration | New socket generation replaces stale socket cleanly |
| TUN read failure | Service recovers or exits; no false active state |
| TUN write backpressure | Control and heartbeat loops continue |
| Client clock rollback | New session is accepted |
| Client/server clock skew | Valid frames are not falsely expired |
| Stale return schedule | Schedule resynchronizes or cleanly reconnects |
| Queue saturation | At least one primary copy is accepted or explicit drop is recorded |
| MTU sweep | No fragmentation/`EMSGSIZE`; throughput comparison |
| 30-60 minute soak | Stable RSS, no unexplained queue growth, no restart |

For every run, capture:

- client and server CPU/RSS
- real sender queue depth and oldest age
- TUN queue depth and write latency
- RTT, jitter, path loss, and tunnel loss
- useful versus late duplicates
- repair hits, misses, and queue drops
- socket generation and rebind reason
- TCP retransmits and achieved throughput

## Claims Rejected Or Not Currently Supported

### "The primary sender still blocks the supervisor under queue saturation"

Rejected as a current unresolved finding. Commit `561c3bf` moved waiting for a full primary sender queue out of the main supervisor and added bounded completion/reporting behavior.

The separate server downstream `try_send` data-loss issue remains valid.

### "XBond has an unbounded memory leak"

Not supported by this audit. Queues and caches are generally bounded. The client inbound queue has a large bounded worst-case capacity and excessive allocation churn, which is a memory-efficiency risk rather than an unbounded leak.

### "MTU 1400 is definitely broken"

Rejected. It fits a normal 1,500-byte IPv4 path after current XBond, UDP, and IPv4 overhead. It remains a lower-MTU and future IPv6 risk that should be measured.

### "The cipher is rebuilt for every packet"

Rejected. `XBondKey` stores a cached `XChaCha20Poly1305` cipher.

### "First-arrival-wins duplicate handling is missing"

Rejected. The duplicate window classifies the first authenticated matching data/duplicate sequence as accepted and drops later copies.

### "Path sockets are not interface-bound"

Rejected as a general claim. The client supports isolated per-path sockets and fails when required Linux device binding cannot be established. Silent post-bind blackholes are the remaining recovery issue.

### "All paths are blindly duplicated during recovery"

Rejected. Hard-ineligible and sufficiently lossy paths are excluded, and harmful duplicates are normally pruned. The narrower verified issue is the intentional fallback that keeps one eligible backup when every candidate is considered harmful.

## Final Recommendation

Implement Phase 1 and Phase 2 before adding more recovery policy complexity.

The highest-value architectural improvement is to isolate TUN I/O, control traffic, repair traffic, and primary data backpressure from each other. After that, harden session identity and return-schedule synchronization. Only then tune full-recovery duplication, because policy tuning cannot compensate for blocked control loops, stale sockets, silent TUN task failure, or queue-induced packet drops.
