#!/bin/sh
set -eu

CONFIG_PATH="${XBOND_CLIENT_CONFIG:-/etc/xbond/client.toml}"
TUN_IF="${XBOND_TUN_IF:-xbond0}"
TUN_SRC="${XBOND_TUN_SRC:-10.250.0.2}"

server_addr="$(awk -F= '
    /^[[:space:]]*server_addr[[:space:]]*=/ {
        value = $2
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", value)
        gsub(/^"|"$/, "", value)
        print value
        exit
    }
' "$CONFIG_PATH")"

server_host="${server_addr%:*}"

case "$server_host" in
    ''|*[!0-9.]*)
        echo "XBond route apply: server_addr is not an IPv4 host; skipping server /32 cleanup" >&2
        ;;
    *)
        # XBond data sockets use SO_BINDTODEVICE per path. A single host /32 pin can
        # force all bound sockets toward one physical route and fight live scheduler
        # anchor changes, so clear stale pins instead of installing a new one.
        ip route delete "$server_host/32" 2>/dev/null || true
        ;;
esac

ip route replace default dev "$TUN_IF" src "$TUN_SRC" metric 1
