# XBond Canary Deployment

These files are for the disabled-by-default XBond canary tunnel. They do not replace Speedify unless routing/NAT is added separately.

## Router

Install as root on `xeon-network`:

```bash
install -m 0755 /tmp/xbond-client /usr/local/bin/xbond-client
install -d -m 0750 /etc/xbond
install -m 0644 /tmp/client.toml /etc/xbond/client.toml
install -m 0600 /tmp/client.env /etc/xbond/client.env
install -m 0644 xbond-client-canary.service /etc/systemd/system/xbond-client.service
systemctl daemon-reload
systemctl disable --now xbond-client.service
```

`/etc/xbond/client.env` must define `XBOND_PSK`. Do not commit that value.

The service needs `CAP_NET_ADMIN` for `/dev/net/tun` and `CAP_NET_RAW` for `SO_BINDTODEVICE`.

## Server

Install as root on `xeon-speedify-vultr-01`:

```bash
install -m 0755 /tmp/xbond-server /usr/local/bin/xbond-server
install -d -m 0750 /etc/xbond
install -m 0600 /tmp/server.env /etc/xbond/server.env
install -m 0644 xbond-server-canary.service /etc/systemd/system/xbond-server-canary.service
systemctl daemon-reload
systemctl enable --now xbond-server-canary.service
```

The server unit listens on `8444/udp` and decapsulates only when started with an explicit TUN option. The committed unit keeps the current lab heartbeat behavior and does not install NAT or routes.

## Canary Limits

- `xbond-client canary-tunnel` opens a TUN and encapsulates IPv4 packets, but it does not change default routes.
- `xbond-server --tun-name <name>` can write first-arrival IPv4 payloads to a TUN, but it does not configure NAT.
- FEC is represented in status as a canary stub. Duplicate mode is implemented; parity generation is not production-ready.
- Keep Speedify as primary until a separate lab route and rollback script are tested.
