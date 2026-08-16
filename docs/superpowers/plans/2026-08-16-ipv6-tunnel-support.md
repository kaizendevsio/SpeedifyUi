# IPv6 Tunnel Support Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Revision 2 (2026-08-16).** Reviewed by Codex `gpt-5.6-sol` (xhigh); the full review is at
> `docs/superpowers/reviews/2026-08-16-ipv6-tunnel-support-codex-review.md`. All three release-blocking
> findings were verified against the code and are incorporated: (1) the server has **four** IPv4 gates,
> not one — including the server TUN-read at `main.rs:3628` whose omission would have discarded every
> IPv6 reply; (2) `deploy-xbond-paired.ps1` never installs the NAT scripts it restarts; (3) the
> fail-open story in revision 1 assumed v4 rollback machinery that does not exist (`xbond-client-rollback.sh`
> only deletes scoped `/32` routes; the watchdog only restarts the service; `xbond/deploy/README.md:64`
> states "No automatic rollback"). Phase B is redesigned around explicit route ownership.

**Goal:** Carry IPv6 traffic through the uLink tunnel end-to-end (client TUN → encrypted UDP-over-IPv4 transport → server TUN → IPv6 internet), without ever leaving the router's IPv6 in a worse state than today's direct-egress behavior.

**Architecture:** The xbond protocol seals raw bytes; IPv4 is enforced at six version-nibble gates (two client, four server). Phase A widens all six, adds ULA addressing, installs NAT66 on the server, and fixes the paired-deploy gap so the NAT artifacts actually ship. Phase B introduces an explicit IPv6 route-ownership state machine on the client: health-gated acquisition, transactional apply, write-once snapshots, and guaranteed release on stop or sustained failure. Phase C (LAN distribution) remains out of scope.

**Transport note:** The path transport (adapter → server UDP on `45.77.241.247:8444`) stays IPv4. Only the payload becomes dual-stack. No wire-format change.

**Deploy rule:** Shared Rust code, service templates, and routing scripts change on both ends — everything ships in one work item via `deploy-xbond-paired.ps1` (per `AGENTS.md`). Never leave client and server gate behavior mismatched.

---

## Decision Record

1. **NAT66 masquerade, not NPTv6.** The Vultr server has one RA-assigned `/64` on `enp1s0`; no delegated prefix exists to route. ULA + masquerade mirrors the v4 design (`10.250.0.0/30` + MASQUERADE).
2. **ULA `fd8c:4342:dff7:1::/64`** (from the randomly generated RFC 4193 `/48` `fd8c:4342:dff7::/48`; revision 1's vanity "ulink" prefix violated the pseudo-random Global-ID requirement). Server `fd8c:4342:dff7:1::1/64`, client `fd8c:4342:dff7:1::2/64`.
3. **Fail-open via explicit route ownership (redesigned).** Revision 1 claimed parity with a v4 rollback that does not exist. Instead, Phase B builds the ownership mechanism explicitly: the v6 default via `xbond0` is acquired only after the tunnel proves v6-healthy, is recorded in a write-once ownership file, and is released (with RA state restored) by `ExecStopPost`, by apply-failure traps, and by the watchdog on sustained v6 failure. Fail-open windows geolocate v6 to the Philippines; accepted.
4. **Geolocation flip accepted.** v6 through the tunnel egresses from the tunnel server (currently Singapore), like v4. Interacts with the TikTok/currency issue; moving the exit is a separate decision.
5. **Phase C (LAN RA/DHCPv6) out of scope** — `eth0` has no IPv6 today, so Phases A+B affect router-originated v6 only.
6. **Traffic bypass stays IPv4-only** this milestone; the editor gains a hint. Scoped-route diagnostics stay v4.
7. **`non_ipv4_packets_dropped` keeps its name**, semantics narrow to "neither v4 nor v6"; the server error text that says "not an IPv4 packet" (near `xbond-server/src/main.rs:3209`) is reworded. Documented in `AGENTS.md`.
8. **v6 health is measured, not assumed** (review finding): the client watchdog gains a v6 egress probe through the tunnel, and route ownership is conditioned on it. Without this, NAT66 or the server's v6 default could fail while everything stays green.

---

## File Structure

**Modify (Rust):**
- `xbond/crates/xbond-core/src/tun.rs`, `lib.rs` — add + export `is_ip_packet`.
- `xbond/crates/xbond-client/src/main.rs` — gates at `3723`, `4310`.
- `xbond/crates/xbond-server/src/main.rs` — gates at `3169`, `3217` (repair-recovered), `3254` (FEC-recovered), `3628` (server TUN read / return path); error text near `3209`.

**Modify (deploy):**
- `deploy-xbond-paired.ps1` — install `xbond-server-nat-apply.sh`, `xbond-server-nat-rollback.sh`, and `xbond-server-nat.service` before restarting the NAT service (today it restarts an old installed copy it never updates), and verify installed rules afterwards.
- `xbond/deploy/systemd/xbond-client.service` — ULA on `xbond0`; `ExecStopPost` v6 release.
- `xbond/deploy/systemd/xbond-server.service` — ULA on `xbonds0`.
- `xbond/deploy/scripts/xbond-server-nat-apply.sh` / `-rollback.sh` — NAT66 with interface-scoped, conntrack-matched rules; WAN `accept_ra=2` before enabling forwarding; sysctl state save/restore.
- `xbond/deploy/scripts/xbond-client-route-apply.sh` / `-rollback.sh` — v6 ownership acquire/release.
- New: `xbond/deploy/scripts/xbond-client-v6-own.sh` — the ownership state machine (acquire/release/status), kept separate from the v4 script so `set -eu` failure domains stay independent.

**Modify (app):**
- `XNetwork/Services/XBondClientWatchdogService.cs` — v6 egress probe + ownership release on sustained v6 failure.
- `XNetwork/Services/XBondMssClampService.cs` — per-family clamp with independent check/mutate.
- `XNetwork/Components/Pages/Settings.razor` — bypass destinations hint.

---

## Phase A — Dual-stack payload, NAT66, deploy wiring

### Task A1: Widen all six payload gates (TDD)

- [ ] **Step 1: Core tests** — `is_ip_packet` accepts nibble 4 and 6, rejects others/empty (`cargo test -p xbond-core ip_packet` red, then green):

```rust
pub fn is_ip_packet(packet: &[u8]) -> bool {
    matches!(packet.first().map(|byte| byte >> 4), Some(4) | Some(6))
}
```

- [ ] **Step 2: Swap all six call sites** — client `3723` (TUN read; rename event `non-ipv4-packet-skipped` → `non-ip-packet-skipped`) and `4310` (return validation); server `3169` (payload), `3217` (repair-recovered), `3254` (FEC-recovered), `3628` (server TUN read). Reword the drop log near server `3209`. Grep-gate: `grep -n "is_ipv4_packet(" crates/xbond-{client,server}` must return zero payload call sites afterwards (socket-level `SocketAddr::is_ipv4` uses stay).
- [ ] **Step 3: Bidirectional + recovery tests** (review finding: a nibble-only unit test cannot catch a missed call site). Extend the existing client/server loop tests to send an IPv6 payload (`0x60…`) end-to-end and assert it is forwarded, and extend the FEC recovery test to recover an IPv6 payload. Run `cargo test` (workspace).
- [ ] **Step 4: Commit** `feat: carry ipv6 payloads through the tunnel`

### Task A2: ULA addressing + paired-deploy NAT wiring

- [ ] **Step 1:** systemd units: add `/usr/sbin/ip -6 addr replace fd8c:4342:dff7:1::2/64 dev xbond0` (client) and `…::1/64 dev xbonds0` (server) inside the existing ExecStartPost retry loops.
- [ ] **Step 2 (review blocker):** `deploy-xbond-paired.ps1` server section — before `systemctl restart xbond-server-nat.service`, add:

```bash
install -m 0755 xbond/deploy/scripts/xbond-server-nat-apply.sh /usr/local/sbin/xbond-server-nat-apply
install -m 0755 xbond/deploy/scripts/xbond-server-nat-rollback.sh /usr/local/sbin/xbond-server-nat-rollback
install -m 0644 xbond/deploy/systemd/xbond-server-nat.service /etc/systemd/system/xbond-server-nat.service
```

  and after the restart, verify: `ip6tables -t nat -C POSTROUTING -s fd8c:4342:dff7:1::/64 -o enp1s0 -j MASQUERADE`.
- [ ] **Step 3: Commit** `feat: ship tunnel ula addressing and nat artifacts in paired deploy`

### Task A3: NAT66 done safely

- [ ] **Step 1 (review finding — RA loss):** in `xbond-server-nat-apply.sh`, **before** enabling forwarding: persist `net.ipv6.conf.enp1s0.accept_ra=2` (append to the server's `/etc/sysctl.d/90-xbond.conf` via the repo copy, so it survives reboots), apply it, *then* `sysctl -w net.ipv6.conf.all.forwarding=1`. Without `accept_ra=2`, enabling forwarding stops RA acceptance and the server's own v6 default eventually expires, taking NAT66 down later.
- [ ] **Step 2:** interface-scoped, conntrack-matched rules (review finding: revision 1's rules were broader than their v4 counterparts and accepted ULA spoof from any interface):

```bash
ULA_NET="fd8c:4342:dff7:1::/64"
ip6tables -C FORWARD -i "$TUN_IF" -s "$ULA_NET" -o "$WAN_IF" -j ACCEPT 2>/dev/null || ip6tables -I FORWARD -i "$TUN_IF" -s "$ULA_NET" -o "$WAN_IF" -j ACCEPT
ip6tables -C FORWARD -i "$WAN_IF" -o "$TUN_IF" -d "$ULA_NET" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT 2>/dev/null || ip6tables -I FORWARD -i "$WAN_IF" -o "$TUN_IF" -d "$ULA_NET" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
ip6tables -t nat -C POSTROUTING -s "$ULA_NET" -o "$WAN_IF" -j MASQUERADE 2>/dev/null || ip6tables -t nat -A POSTROUTING -s "$ULA_NET" -o "$WAN_IF" -j MASQUERADE
```

  `RELATED` carries ICMPv6 Packet-Too-Big, which PMTUD needs. Rollback deletes these rules **and** restores the saved prior values of both sysctls.
- [ ] **Step 3: Commit** `feat: nat66 for the tunnel ula`

### Task A4: Phase A live validation (paired deploy)

- [ ] Deploy via `deploy-xbond-paired.ps1`.
- [ ] Router: `ping -6 -I xbond0 fd8c:4342:dff7:1::1 -c 5` → 5/5.
- [ ] **Test route, not `--interface`** (review finding: binding to `xbond0` creates no global route): `sudo ip -6 route add 2606:4700:4700::1111/128 via fd8c:4342:dff7:1::1 dev xbond0` (trap-cleaned), then `curl -6 https://[2606:4700:4700::1111]/cdn-cgi/trace --resolve …` or `ping -6 2606:4700:4700::1111` → replies from the server's v6 path. `tracepath6 2606:4700:4700::1111` to sanity-check PMTU. Delete the test route.
- [ ] v4 regression check: tunnel ping, `curl -4` egress, app routes 200.
- [ ] Record in `AGENTS.md`. **Stop and reassess before Phase B.**

## Phase B — IPv6 route ownership (redesigned per review)

The ownership state machine, all in `xbond-client-v6-own.sh` (idempotent subcommands `acquire`, `release`, `status`):

- **Snapshot location:** `/var/lib/xbond/v6-ownership.json` — NOT `/run/xbond`, which is the unit's `RuntimeDirectory` and is wiped when the service stops, exactly when rollback needs it (review finding).
- **Write-once:** `acquire` refuses to overwrite an existing ownership file (a restart must not replace the only good snapshot after RA defaults have already been suppressed). The snapshot stores, per physical adapter: the original `accept_ra_defrtr` value and the RA-learned default routes filtered to `proto ra` only, with their `expires` noted as advisory (replay uses fresh RA where possible; the snapshot is the fallback).
- **Transactional apply:** every mutation step appends to the snapshot before executing; a `trap` on failure replays completed steps in reverse. No `set -eu` half-state (review finding).
- **Acquire is health-gated:** only runs after the v6 tunnel probe (below) has passed; simply having a TUN device is not readiness (review finding).
- **Release:** removes the `xbond0` v6 default, restores each adapter's original `accept_ra_defrtr`, replays/relearns RA defaults, deletes the ownership file. Wired into `ExecStopPost` in `xbond-client.service`, the apply-failure trap, and the watchdog.

### Task B1: Ownership script + unit wiring

- [ ] Implement `xbond-client-v6-own.sh` per above; `acquire` sets `ip -6 route replace default dev xbond0 metric 50` and per-adapter `accept_ra_defrtr=0` (filtered to physical adapters that currently hold `proto ra` defaults — never `lo`, `xbond*`, `tailscale*`, `eth0`). Install it in both deploy paths. Add `ExecStopPost=/usr/local/sbin/xbond-client-v6-own release` to the client unit. Bounded shell tests via `status` output assertions on the router in a maintenance window. Commit `feat: ipv6 route ownership state machine`.

### Task B2: v6 health probe + watchdog integration (TDD, app)

- [ ] `XBondClientWatchdogService` gains a v6 egress probe (ping `2606:4700:4700::1111` through the tunnel, same cadence/thresholds pattern as existing checks; settings-gated, default on only when ownership is active). On sustained v6 failure with v4 healthy: invoke `release` (not a service restart, which would immediately reacquire — review finding). Evaluate logic as a pure static with tests, mirroring `Evaluate`. Commit `feat: watchdog releases ipv6 ownership on sustained v6 failure`.

### Task B3: MSS clamp corrected (TDD, app)

- [ ] Review finding: the current clamp is `FORWARD -o xbond0` only, and router-originated traffic traverses `OUTPUT`; also 1360 is a v4-appropriate MSS — the v6 ceiling for a 1400 MTU is **1340**. Change `XBondMssClampService` to manage four rules independently (iptables/ip6tables × FORWARD/OUTPUT), using `--clamp-mss-to-pmtu` instead of fixed values, with per-rule presence checks so enable/disable never double-appends or half-applies (tests via the injected runner). Commit `feat: family-correct tcp mss clamping for the tunnel`.

### Task B4: Bypass hint + Phase B validation

- [ ] `Settings.razor` destinations hint: `IPv4 addresses/CIDRs only. IPv6 follows the tunnel.`
- [ ] Paired deploy. Verify: `curl -6 https://ipwho.is/` exits via the server; `ip -6 route get 2606:4700:4700::1111` → `dev xbond0`; `systemctl stop xbond-client` → v6 restored direct within seconds (ExecStopPost), then restart reacquires after health gate; watchdog-forced release drill; v4 untouched; bounded netem pass on one v6 scenario.
- [ ] Version bump + changelog + `AGENTS.md` (including geolocation flip and this plan's revision history).

## Explicitly Out of Scope

LAN RA/DHCPv6 (Phase C); IPv6 path transport; v6-aware bypass rules and scoped-route diagnostics; moving the tunnel exit region.

## Risks

| Risk | Mitigation |
|---|---|
| v6 blackhole: TUN default outlives a dead-but-running client | Watchdog v6 probe → `release` (B2); ExecStopPost covers clean stops |
| RA defaults expire while suppressed, then apply fails | Transactional trap replays; write-once snapshot in `/var/lib/xbond` survives service stop |
| Server loses its own v6 default after forwarding=1 | `accept_ra=2` on WAN persisted **before** forwarding (A3) |
| Missed nibble gate ships | Grep-gate in A1 + bidirectional/FEC e2e tests; single paired deploy |
| PMTU for v6 in 1400 tunnel | `--clamp-mss-to-pmtu` both chains (B3); conntrack RELATED passes ICMPv6 PTB (A3); `tracepath6` in validation |
| ULA collision | Random RFC 4193 prefix (Decision 2) |
