#!/bin/sh
set -eu

WAN_IF="${XBOND_WAN_IF:-enp1s0}"
TUN_IF="${XBOND_TUN_IF:-xbonds0}"
CLIENT_CIDR="${XBOND_CLIENT_CIDR:-10.250.0.0/30}"

sysctl -w net.ipv4.ip_forward=1 >/dev/null

ensure_filter_rule() {
    if ! iptables -C FORWARD "$@" 2>/dev/null; then
        iptables -I FORWARD "$@"
    fi
}

ensure_nat_rule() {
    if ! iptables -t nat -C POSTROUTING "$@" 2>/dev/null; then
        iptables -t nat -A POSTROUTING "$@"
    fi
}

ensure_filter_rule -s "$CLIENT_CIDR" -i "$TUN_IF" -o "$WAN_IF" -j ACCEPT
ensure_filter_rule -d "$CLIENT_CIDR" -i "$WAN_IF" -o "$TUN_IF" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
ensure_nat_rule -s "$CLIENT_CIDR" -o "$WAN_IF" -j MASQUERADE
