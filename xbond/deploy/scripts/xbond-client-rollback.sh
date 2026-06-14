#!/bin/sh
set -eu

TUN_IF="${XBOND_TUN_IF:-xbond0}"

if [ "$#" -gt 0 ] && [ -n "$1" ]; then
    ip route del "$1/32" dev "$TUN_IF" 2>/dev/null || true
    exit 0
fi

ip route show dev "$TUN_IF" | awk '{print $1}' | while read -r route; do
    case "$route" in
        */32)
            ip route del "$route" dev "$TUN_IF" 2>/dev/null || true
            ;;
    esac
done
