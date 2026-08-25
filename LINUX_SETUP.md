# Linux Setup Guide for uLink

This guide covers the privileges the uLink dashboard (`XNetwork`) needs on the router, so it can apply network changes without an interactive password prompt.

A web request cannot answer a `sudo` password prompt. Any privileged action therefore has to be either passwordless for the service account or refused.

## What actually needs privileges

The dashboard shells out for these, all as separate helper scripts installed by `deploy.sh` into `/usr/local/sbin/`:

| Helper | Purpose | Needs |
| --- | --- | --- |
| `xnetwork-traffic-bypass-apply` | Programs the nftables bypass table, policy routes and fwmark rules | `nft`, `ip route`, `ip rule` |
| `xnetwork-starlink-lan-access-apply` | Routes the Starlink management host through the dish's adapter | `ip route`, `ip rule` |
| `xnetwork-router-resolver-apply` | Pins the router's own resolver to the local dnsmasq | `tailscale`, NetworkManager drop-in, `/etc/resolv.conf` |

Beyond the helpers, the app may restart its own units (`xnetwork`, `xnetwork-dns`, `xbond-client`), read interface state via `ip`/`nmcli`, and run `nethogs` for per-process throughput.

`xbond-client` runs as its own systemd service and holds `CAP_NET_ADMIN` for the TUN device; it does not go through the dashboard's sudo rules.

## Configuring passwordless sudo

The service account is whichever user runs `xnetwork.service` — on this router that is `xeon-network`. Confirm before editing:

```bash
systemctl show xnetwork -p User --value
```

Then create a dedicated sudoers file (never edit `/etc/sudoers` directly):

```bash
sudo visudo -f /etc/sudoers.d/xnetwork
```

Grant only the helpers and the specific commands needed. Prefer naming the helper scripts over granting blanket `ip`/`nft` access, since the helpers validate their own inputs:

```
# Replace xeon-network with the account from the command above.
xeon-network ALL=(ALL) NOPASSWD: /usr/local/sbin/xnetwork-traffic-bypass-apply
xeon-network ALL=(ALL) NOPASSWD: /usr/local/sbin/xnetwork-starlink-lan-access-apply
xeon-network ALL=(ALL) NOPASSWD: /usr/local/sbin/xnetwork-router-resolver-apply

# Service control for the app's own units.
xeon-network ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart xnetwork-dns.service
xeon-network ALL=(ALL) NOPASSWD: /usr/bin/systemctl restart xbond-client.service

# Reboot, if the dashboard exposes it.
xeon-network ALL=(ALL) NOPASSWD: /sbin/reboot
```

Set permissions and validate. A malformed sudoers file can lock you out of `sudo`, so always check before logging out:

```bash
sudo chmod 0440 /etc/sudoers.d/xnetwork
sudo visudo -c
```

## Verifying

```bash
# List what the service account is allowed to do without a password.
sudo -u xeon-network sudo -l

# Each helper should run without prompting. `status` is read-only and safe.
sudo -u xeon-network sudo -n /usr/local/sbin/xnetwork-traffic-bypass-apply status
```

`deploy.sh` itself checks for passwordless sudo with `sudo -n true` and warns rather than failing when it is missing, so a silent skip in the deploy output usually means these rules are absent.

## Capabilities instead of sudo

`nethogs` needs raw socket access for per-process throughput. Granting capabilities avoids a sudo rule:

```bash
sudo setcap cap_net_admin,cap_net_raw+eip /usr/sbin/nethogs
```

If per-process attribution shows as unavailable in the dashboard, this is usually why.

## Notes

- `NetworkMonitorService` exits unless the app is on Linux, monitoring is enabled, and `NetworkMonitor:WhitelistedLinks` is non-empty. Its link bounce (`ip link set <iface> down/up`) also needs privileges.
- Policy routes are removed by the kernel whenever their interface goes down, which is why the bypass helper is re-run periodically rather than once at startup. Do not treat a missing route as a permissions problem without checking the interface first.
- Keep the granted set narrow. These rules let the service account reconfigure the router's networking; anything broader than the helpers above is worth questioning.
