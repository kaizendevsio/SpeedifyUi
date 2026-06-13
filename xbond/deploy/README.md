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

The server unit listens on `8444/udp`, opens `xbonds0`, and can send return packets back to the latest client path peers. It still does not install NAT or routes.

## Canary Limits

- `xbond-client canary-tunnel` opens a TUN and encapsulates IPv4 packets, but it does not change default routes.
- `xbond-server --tun-name <name>` writes first-arrival IPv4 payloads to a TUN and reads return packets from the server TUN for encapsulation back to the client.
- `AnchorFec` uses canary XOR parity blocks and can recover one missing packet per two-packet block when the paired packet and parity arrive.
- Keep Speedify as primary until a separate lab route and rollback script are tested.
