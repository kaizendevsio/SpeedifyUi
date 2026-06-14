#!/bin/sh
set -eu

WAN_IF="${XBOND_WAN_IF:-enp1s0}"
TUN_IF="${XBOND_TUN_IF:-xbonds0}"
CLIENT_CIDR="${XBOND_CLIENT_CIDR:-10.250.0.0/30}"

while iptables -D FORWARD -s "$CLIENT_CIDR" -i "$TUN_IF" -o "$WAN_IF" -j ACCEPT 2>/dev/null; do
    :
done

while iptables -D FORWARD -d "$CLIENT_CIDR" -i "$WAN_IF" -o "$TUN_IF" -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT 2>/dev/null; do
    :
done

while iptables -t nat -D POSTROUTING -s "$CLIENT_CIDR" -o "$WAN_IF" -j MASQUERADE 2>/dev/null; do
    :
done
