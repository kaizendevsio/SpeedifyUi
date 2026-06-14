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
        echo "XBond route apply: server_addr is not an IPv4 host; skipping server /32 route" >&2
        ;;
    *)
        best_default="$(
            ip -4 route show default |
                awk -v tun_if="$TUN_IF" '
                    {
                        dev = ""
                        metric = 1000000
                        for (i = 1; i <= NF; i++) {
                            if ($i == "dev") dev = $(i + 1)
                            if ($i == "metric") metric = $(i + 1)
                        }
                        if (dev != "" && dev != tun_if && dev != "connectify0" && dev != "tailscale0") {
                            print metric "\t" $0
                        }
                    }
                ' |
                sort -n |
                head -n 1 |
                cut -f2-
        )"

        if [ -z "$best_default" ]; then
            echo "XBond route apply: no physical IPv4 default route found for server endpoint" >&2
            exit 1
        fi

        gateway=""
        dev=""
        set -- $best_default
        while [ "$#" -gt 0 ]; do
            case "$1" in
                via)
                    shift
                    gateway="${1:-}"
                    ;;
                dev)
                    shift
                    dev="${1:-}"
                    ;;
            esac
            shift || true
        done

        if [ -z "$dev" ]; then
            echo "XBond route apply: selected default route has no device: $best_default" >&2
            exit 1
        fi

        if [ -n "$gateway" ]; then
            ip route replace "$server_host/32" via "$gateway" dev "$dev" metric 1
        else
            ip route replace "$server_host/32" dev "$dev" metric 1
        fi
        ;;
esac

ip route replace default dev "$TUN_IF" src "$TUN_SRC" metric 1
