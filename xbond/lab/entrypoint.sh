#!/bin/sh
set -eu

mkdir -p /dev/net /run/netns "${LAB_RESULTS_DIR:-/results}"
if [ ! -e /dev/net/tun ]; then
    mknod /dev/net/tun c 10 200
    chmod 0666 /dev/net/tun
fi

exec python3 /opt/xbond/lab/lab.py "$@"
