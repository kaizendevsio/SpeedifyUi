# XBond Deployment

These files install the boot-enabled XBond tunnel. XBond is IPv4-only in this milestone; IPv6 traffic must remain disabled or routed outside XBond until IPv6 encapsulation is implemented.

## Paired Runtime Deployment

When changing Rust protocol, scheduler, status schema, routing, service templates, or shared client/server runtime code, deploy both ends together from the Windows workstation:

```powershell
.\deploy-xbond-paired.ps1
```

The paired deploy script updates `xeon-speedify-vultr-01` first, rebuilds and restarts `xbond-server.service` plus `xbond-server-nat.service`, then updates `xeon-network`, runs the app deploy, rebuilds and restarts `xbond-client.service`, and verifies app routes, the XBond default route, tunnel ping, and internet ping. Use the normal router `deploy.sh` only for app-only changes that do not affect XBond Rust runtime behavior.

## Router

Install as root on `xeon-network`:

```bash
install -m 0755 /tmp/xbond-client /usr/local/bin/xbond-client
install -d -m 0750 /etc/xbond
install -m 0644 /tmp/client.toml /etc/xbond/client.toml
install -m 0600 /tmp/client.env /etc/xbond/client.env
install -m 0644 xbond-client.service /etc/systemd/system/xbond-client.service
install -m 0755 scripts/xbond-client-route-apply.sh /usr/local/sbin/xbond-client-route-apply
install -m 0755 scripts/xbond-client-rollback.sh /usr/local/sbin/xbond-client-rollback
systemctl daemon-reload
systemctl disable --now speedify.service speedify-sharing.service
systemctl enable --now xbond-client.service
```

`/etc/xbond/client.env` must define `XBOND_PSK`. Do not commit that value.
Keep `/etc/xbond/client.env` mode `0600`, but `/etc/xbond` and `/run/xbond` can be searchable/readable so the unprivileged XNetwork UI can read non-secret config/status.

The service needs `CAP_NET_ADMIN` for `/dev/net/tun` and `CAP_NET_RAW` for `SO_BINDTODEVICE`.
The XBond client unit conflicts with `speedify.service` and `speedify-sharing.service`, removes any stale `xbond0` default route before socket startup, pins the XBond server IPv4 endpoint to a physical route, reapplies `10.250.0.2/30` to `xbond0`, and installs the IPv4 default route through `xbond0` after each service start. It also reads the XNetwork Cudy management URL from the non-secret runtime settings file and pins that local management host to a physical route before installing the XBond default route. The unit includes the current Cudy management host as a non-secret local-bypass fallback so service restarts keep local management off `xbond0` even if the app settings file cannot be read at that instant. XBond data sockets still bind directly to their physical interfaces; the pre-start physical route prevents recursive routing during cold restarts.
Set `XBOND_LOCAL_BYPASS_HOSTS` or `XBOND_LOCAL_MANAGEMENT_HOSTS` in `/etc/xbond/client.env` for extra IPv4 local-management hosts or CIDRs that must never route through `xbond0`. Optional `XBOND_LOCAL_BYPASS_DEV` and `XBOND_LOCAL_BYPASS_GATEWAY` force those bypass routes through a specific physical interface/gateway.
Use `xbond-client-rollback [target-ip]` to remove one scoped `/32` route, or run it without arguments to remove all `/32` routes on `xbond0`.

## Server

Install as root on the XBond VPS:

```bash
install -m 0755 /tmp/xbond-server /usr/local/bin/xbond-server
install -d -m 0750 /etc/xbond
install -m 0600 /tmp/server.env /etc/xbond/server.env
install -m 0644 xbond-server.service /etc/systemd/system/xbond-server.service
install -m 0644 xbond-server-nat.service /etc/systemd/system/xbond-server-nat.service
install -m 0755 scripts/xbond-server-nat-apply.sh /usr/local/sbin/xbond-server-nat-apply
install -m 0755 scripts/xbond-server-nat-rollback.sh /usr/local/sbin/xbond-server-nat-rollback
systemctl daemon-reload
systemctl enable --now xbond-server.service
systemctl enable --now xbond-server-nat.service
```

The server unit listens on `8444/udp`, opens `xbonds0`, and can send return packets back to the latest client path peers.
The XBond server unit reapplies `10.250.0.1/30` to `xbonds0` after each service start.
The NAT unit enables IPv4 forwarding and idempotently installs forwarding/MASQUERADE rules for `10.250.0.0/30` through `enp1s0`. Override `XBOND_WAN_IF`, `XBOND_TUN_IF`, or `XBOND_CLIENT_CIDR` in a systemd drop-in if those names change.
Run `systemctl stop xbond-server-nat.service` to remove the NAT rules through `ExecStop`.

## XBond Limits

- `xbond-client tunnel` opens a TUN and encapsulates IPv4 packets.
- XNetwork keeps manual `/32` route diagnostics, while `xbond-client.service` owns the IPv4 default route through `xbond0` in this branch. No automatic rollback to Speedify is implemented.
- Each `xbond-client tunnel` process uses a fresh runtime session id by default so service restarts are not treated as duplicate old packets by the server. Use `--session-id` only for deterministic diagnostics.
- `xbond-server --tun-name <name>` writes first-arrival IPv4 payloads to a TUN and reads return packets from the server TUN for encapsulation back to the client.
- `AnchorFec` uses XBond XOR parity blocks and can recover one missing packet per two-packet block when the paired packet and parity arrive.
- The tunnel scheduler recomputes roles continuously and refuses paths that are down, fail socket binding/connectivity, repeatedly fail sends, or report full loss.
