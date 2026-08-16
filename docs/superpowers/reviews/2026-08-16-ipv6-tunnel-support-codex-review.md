The plan is not implementation-ready. Three release-blocking issues would prevent working or safe IPv6: incomplete Rust gate coverage, NAT scripts not being deployed, and a rollback path that is described but does not exist.

## Findings

- **[Critical] The server has four IPv4 gates, not one.** The plan calls `main.rs:3169` the “server TUN read” and proposes changing only that call ([plan:88](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:88)). In reality:

  - [server main.rs:3169](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-server/src/main.rs:3169) validates ordinary client-to-server payloads.
  - [server main.rs:3217](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-server/src/main.rs:3217) validates recovered data/duplicate payloads.
  - [server main.rs:3254](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-server/src/main.rs:3254) validates payloads reconstructed from FEC blocks.
  - [server main.rs:3628](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-server/src/main.rs:3628) is the actual server TUN-read/return-path gate.

  Leaving `3628` unchanged discards every IPv6 internet reply, so the proposed `curl -6` cannot succeed. Leaving `3217`/`3254` unchanged silently discards IPv6 packets recovered by FEC. All four server calls plus both client calls at [client main.rs:3723](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-client/src/main.rs:3723) and `4310` must change. The error text at server line 3209 should also stop saying “not an IPv4 packet.” The proposed nibble-only unit test would not catch any missed call site; add bidirectional and FEC-recovery IPv6 tests.

- **[Critical] `deploy-xbond-paired.ps1` does not install the NAT scripts.** The plan relies on paired deployment ([plan:13](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:13)), but the server deployment only installs the binary, server unit, and sysctl file before restarting `xbond-server-nat.service` ([deploy-xbond-paired.ps1:39](/C:/Users/Xeon/RiderProjects/SpeedifyUi/deploy-xbond-paired.ps1:39)). It never installs:

  - `xbond-server-nat-apply.sh`
  - `xbond-server-nat-rollback.sh`
  - `xbond-server-nat.service`

  The manual instructions do install them ([deploy README:48](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/README.md:48)). As written, deployment would restart the old IPv4-only installed helper, leaving NAT66 absent. The paired script must install these artifacts and verify the actual installed rules.

- **[Critical] The claimed fail-open rollback mechanism does not exist.** The plan says the “v4 watchdog rolls back” and expects `systemctl stop xbond-client` to restore IPv6 within its cadence ([plan:23](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:23), [plan:141](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:141)). Actual behavior is different:

  - The client unit has startup helpers but no `ExecStop` or `ExecStopPost` rollback ([xbond-client.service:14](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/systemd/xbond-client.service:14)).
  - The current rollback helper only deletes scoped IPv4 `/32` routes ([xbond-client-rollback.sh:6](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/scripts/xbond-client-rollback.sh:6)).
  - The watchdog calls `RestartAsync`; it never invokes route rollback ([XBondClientWatchdogService.cs:247](/C:/Users/Xeon/RiderProjects/SpeedifyUi/XNetwork/Services/XBondClientWatchdogService.cs:247), `332`).
  - The deployment documentation explicitly says there is no automatic rollback ([README:64](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/README.md:64)).

  If the TUN disappears, its default route normally disappears too, but `accept_ra_defrtr=0` remains. Existing RA routes can expire, after which IPv6 stays blackholed. An alive-but-nonfunctional client is worse: the TUN default remains indefinitely. The design needs an explicit route-ownership state machine: transactional apply, `ExecStopPost` rollback, rollback on apply failure, and route acquisition only after the dataplane is proven healthy. The watchdog must release the IPv6 route on sustained IPv6 failure, not merely restart and immediately reacquire it.

- **[High] Enabling server IPv6 forwarding can destroy the server’s RA-learned IPv6 default route.** The plan explicitly says the Vultr `/64` is RA-assigned, then sets `net.ipv6.conf.all.forwarding=1` ([plan:21](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:21), [plan:112](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:112)). Linux stops accepting RAs when forwarding is enabled unless the WAN interface has `accept_ra=2`. Consequently the current default route may expire, taking NAT66 down later. This behavior is documented by the [Linux kernel IPv6 sysctl documentation](https://www.kernel.org/doc/html/latest/networking/ip-sysctl.html).

  Before enabling forwarding, either configure `net.ipv6.conf.enp1s0.accept_ra=2` persistently or install a static supported IPv6 address/default route. The plan must verify that state and restore prior sysctl values during teardown.

- **[High] Phase B mutation and snapshot semantics are unsafe.** The route helper runs under `set -eu` ([route-apply:2](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/scripts/xbond-client-route-apply.sh:2)). The proposed sequence can therefore suppress RA on several interfaces, fail halfway, and leave partial state. Further restarts can overwrite the only good snapshot after RA defaults have disappeared. Restoring every interface to `accept_ra_defrtr=1` also overwrites legitimate pre-existing settings.

  The plan should require:

  - Atomic, write-once snapshots of both routes and each original sysctl value.
  - A trap that restores state if any apply step fails.
  - Filtering to actual physical `proto ra` defaults rather than every default.
  - No overwriting of an existing ownership snapshot during restart.
  - Handling route `expires` values; replaying textual RA routes can recreate stale gateways with stale lifetimes. IPv6 route expiry is part of `ip-route` semantics ([ip-route(8)](https://man7.org/linux/man-pages/man8/ip-route.8.html)).
  - A defined adapter-enumeration rule and coordination with NetworkManager.
  - A readiness check stronger than “TUN device exists.”

  Also note that `/run/xbond` is the unit’s `RuntimeDirectory` ([xbond-client.service:20](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/systemd/xbond-client.service:20)); a later external watchdog cannot safely depend on a snapshot there after the service has stopped.

- **[High] The MSS-clamp phase does not provide the PMTU guarantee claimed by the risk table.** The current rule is `mangle/FORWARD -o xbond0` ([XBondMssClampService.cs:195](/C:/Users/Xeon/RiderProjects/SpeedifyUi/XNetwork/Services/XBondMssClampService.cs:195)). Phases A/B cover router-originated traffic, which traverses `OUTPUT`, not `FORWARD`, so the rule is not used. It is also disabled by default ([appsettings.json:72](/C:/Users/Xeon/RiderProjects/SpeedifyUi/XNetwork/appsettings.json:72)).

  If reused for future forwarded IPv6 traffic, the configured MSS of 1360 is wrong for a 1400-byte IPv6 path: the ordinary IPv6 TCP MSS ceiling is 1340. Prefer `--clamp-mss-to-pmtu` or separate family-appropriate values. Enable/disable must check and mutate each family independently; otherwise “v4 present, v6 missing” can append a duplicate v4 rule and partial failures can leave inconsistent state.

  NAT66 should still be tested with IPv6 UDP and `tracepath6`/large-packet cases. MSS clamping does not protect UDP or server-to-client oversized packets.

- **[High] Neither the app nor runtime health checks detect an IPv6-only outage.** The server unit probes only IPv4 targets ([xbond-server.service:11](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/systemd/xbond-server.service:11)); the dashboard internet check pings only `8.8.8.8` ([ConnectionHealthService.cs:12](/C:/Users/Xeon/RiderProjects/SpeedifyUi/XNetwork/Services/ConnectionHealthService.cs:12)); and the watchdog evaluates transport heartbeat health rather than NAT66/IPv6 egress ([XBondClientWatchdogService.cs:456](/C:/Users/Xeon/RiderProjects/SpeedifyUi/XNetwork/Services/XBondClientWatchdogService.cs:456)). NAT66 or the server’s IPv6 default could fail while the UI remains green and rollback never occurs. Phase B needs a separate IPv6 egress/route-owner status and should incorporate it into fail-open decisions.

- **[Medium] The proposed IPv6 firewall rules are broader than their IPv4 counterparts.** Existing rules scope both input and output interfaces ([server NAT apply:22](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/deploy/scripts/xbond-server-nat-apply.sh:22)); the proposed IPv6 rules omit `-i "$TUN_IF"` and `-o "$WAN_IF"`. Mirror the existing interface scoping to avoid accepting spoofed ULA traffic from another interface.

  The proposed `-m state --state RELATED,ESTABLISHED` is valid for IPv6; `state` is a supported subset of `conntrack`, and `RELATED` includes ICMP errors such as Packet Too Big ([iptables-extensions(8)](https://man7.org/linux/man-pages/man8/iptables-extensions.8.html)). Using `-m conntrack --ctstate` would nevertheless match the existing rule and avoid needless divergence. Rollback must also address the forwarding/RA sysctl state, not just delete rules.

- **[Medium] Phase A’s internet validation lacks a route.** Phase A intentionally leaves the IPv6 default untouched ([plan:130](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:130)), but expects `curl -6 --interface xbond0` to reach a global address ([plan:128](/C:/Users/Xeon/RiderProjects/SpeedifyUi/docs/superpowers/plans/2026-08-16-ipv6-tunnel-support.md:128)). Binding a socket to `xbond0` does not create a global route; at that stage the interface only has the connected ULA `/64`. Use a temporary, trap-cleaned `/128` or default route through `xbond0` for the test.

- **[Low] The selected ULA is memorable but not RFC 4193-compliant.** RFC 4193 requires the 40-bit Global ID to be pseudo-random; encoding “ulink” increases collision risk if networks ever merge or interconnect ([RFC 4193](https://www.rfc-editor.org/rfc/rfc4193.html)). A generated ULA `/48` with a `/64` tunnel subnet would be safer. The fixed `/64`, NAT66 model, and `::1`/`::2` addressing are otherwise reasonable.

## Parts that are sound

- The transport and protocol are payload-agnostic. The TUN uses `IFF_NO_PI` and yields raw IP packets ([tun.rs:44](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-core/src/tun.rs:44)); sealing operates on opaque byte slices ([protocol.rs:177](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-core/src/protocol.rs:177)); scheduling distinguishes packets by length, not IP header fields ([scheduler.rs:578](/C:/Users/Xeon/RiderProjects/SpeedifyUi/xbond/crates/xbond-core/src/scheduler.rs:578)). No wire-format change is needed.
- The client’s IPv4-only server resolution, bind-source validation, path probes, and UDP sockets are intentional because the outer transport remains IPv4; they do not prevent IPv6 payloads.
- ULA plus NAT66 is a defensible solution when no prefix is delegated. Masquerading and conntrack can carry ICMPv6 errors correctly once server RA behavior and firewall scoping are fixed.
- Adding the ULA addresses inside the existing systemd retry loops is appropriate, and current service capabilities are sufficient.
- Keeping `non_ipv4_packets_dropped` for schema compatibility is acceptable if every gate is changed and its corrected semantics are documented.
- Deferring LAN RA/DHCPv6 and IPv6 traffic bypass is a sensible scope boundary. The current app’s physical-provider lookups remaining IPv4-preferred is also consistent with the IPv4 outer transport.

Verdict: Phase A’s architecture is basically right, but its Rust call-site inventory and deployment wiring must be corrected. Phase B needs redesign around explicit route ownership, health-gated takeover, and guaranteed rollback before it is safe to implement. No files were modified.