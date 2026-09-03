#!/usr/bin/env python3
"""Apply uLink direct/tunnel egress policy without touching traffic-bypass rules."""

from __future__ import annotations

import argparse
import ipaddress
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import tomllib


MARK_BASE = 0x130000
MARK_MASK = 0xFFFFFF
TABLE_BASE = 13000
RULE_PRIORITY_BASE = 13000
NONE_MARK = 0x13FFFF
NONE_TABLE = 13999
NONE_RULE_PRIORITY = 13999
NFT_TABLE = "ulink_egress"
DEFAULT_CONFIG = "/etc/xbond/client.toml"
DEFAULT_STATE = "/run/xbond/egress-state.json"
DEFAULT_LOCK = "/run/xbond/egress.lock"
INTERFACE_RE = re.compile(r"^[A-Za-z0-9_.:-]{1,15}$")


class EgressError(RuntimeError):
    pass


def run(args: list[str], *, input_text: str | None = None, check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(args, input=input_text, text=True, capture_output=True, check=check)


def validate_path_id(path_id: int) -> int:
    if not 1 <= path_id <= 998:
        raise EgressError("path ID must be between 1 and 998")
    return path_id


def validate_interface(name: str, lan_interface: str) -> str:
    if not INTERFACE_RE.fullmatch(name):
        raise EgressError(f"unsafe interface name: {name!r}")
    if name == lan_interface or name == "lo" or name.startswith(("xbond", "tailscale", "docker", "br-", "veth")):
        raise EgressError(f"interface is not an eligible physical WAN: {name}")
    return name


def mark_for(path_id: int) -> int:
    return MARK_BASE + validate_path_id(path_id)


def table_for(path_id: int) -> int:
    return TABLE_BASE + validate_path_id(path_id)


def load_config(path: str) -> dict:
    with open(path, "rb") as handle:
        return tomllib.load(handle)


def write_state(payload: dict) -> None:
    state_path = pathlib.Path(os.environ.get("ULINK_EGRESS_STATE", DEFAULT_STATE))
    state_path.parent.mkdir(parents=True, exist_ok=True)
    temporary = state_path.with_suffix(".tmp")
    temporary.write_text(json.dumps(payload, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, state_path)


def read_state() -> dict:
    state_path = pathlib.Path(os.environ.get("ULINK_EGRESS_STATE", DEFAULT_STATE))
    try:
        payload = json.loads(state_path.read_text(encoding="utf-8"))
    except (FileNotFoundError, OSError, json.JSONDecodeError) as error:
        raise EgressError(f"cannot read current egress state from {state_path}: {error}") from error
    if not isinstance(payload, dict):
        raise EgressError(f"invalid egress state in {state_path}")
    return payload


def json_command(args: list[str]) -> list[dict]:
    completed = run(args)
    try:
        value = json.loads(completed.stdout or "[]")
    except json.JSONDecodeError as error:
        raise EgressError(f"invalid JSON from {' '.join(args)}: {error}") from error
    if not isinstance(value, list):
        raise EgressError(f"unexpected JSON from {' '.join(args)}")
    return value


def default_route(interface: str) -> dict:
    routes = json_command(["ip", "-j", "-4", "route", "show", "table", "main", "default", "dev", interface])
    # iproute2 omits fields already constrained by a show filter, so `dev` is not
    # guaranteed to be repeated in this JSON. The command itself scoped the result.
    candidates = [route for route in routes if route.get("gateway")]
    if not candidates:
        raise EgressError(f"no IPv4 default gateway is available on {interface}")
    return min(candidates, key=lambda route: int(route.get("metric", 0)))


def primary_address(interface: str) -> tuple[str, str]:
    links = json_command(["ip", "-j", "-4", "addr", "show", "dev", interface])
    for link in links:
        for address in link.get("addr_info", []):
            if address.get("family") != "inet" or address.get("scope") != "global":
                continue
            local = str(address["local"])
            prefix = int(address["prefixlen"])
            network = str(ipaddress.ip_interface(f"{local}/{prefix}").network)
            return local, network
    raise EgressError(f"no global IPv4 address is available on {interface}")


def replace_rule(mark: int, table: int, priority: int) -> None:
    while run(
        ["ip", "-4", "rule", "del", "fwmark", f"0x{mark:x}/0x{MARK_MASK:x}", "table", str(table), "priority", str(priority)],
        check=False,
    ).returncode == 0:
        pass
    run(["ip", "-4", "rule", "add", "fwmark", f"0x{mark:x}/0x{MARK_MASK:x}", "table", str(table), "priority", str(priority)])


def prepare_direct(path_id: int, interface: str, lan_interface: str) -> tuple[int, int, str, str]:
    validate_interface(interface, lan_interface)
    route = default_route(interface)
    source, network = primary_address(interface)
    gateway = str(route["gateway"])
    table = table_for(path_id)
    mark = mark_for(path_id)
    run(["ip", "-4", "route", "flush", "table", str(table)], check=False)
    run(["ip", "-4", "route", "replace", "table", str(table), network, "dev", interface, "src", source, "scope", "link"])
    run(["ip", "-4", "route", "replace", "table", str(table), "default", "via", gateway, "dev", interface, "src", source])
    replace_rule(mark, table, RULE_PRIORITY_BASE + path_id)
    probe = run(["ip", "-4", "route", "get", "1.1.1.1", "mark", f"0x{mark:x}"], check=False)
    if probe.returncode != 0 or f"dev {interface}" not in probe.stdout:
        raise EgressError(f"prepared route table {table} did not resolve through {interface}")
    return mark, table, gateway, source


def prepare_tunnel(tun_interface: str) -> tuple[int, int]:
    validate_interface_token(tun_interface)
    mark = MARK_BASE
    table = TABLE_BASE
    run(["ip", "-4", "route", "flush", "table", str(table)], check=False)
    run(["ip", "-4", "route", "replace", "table", str(table), "default", "dev", tun_interface, "src", "10.250.0.2"])
    replace_rule(mark, table, RULE_PRIORITY_BASE)
    return mark, table


def prepare_none() -> tuple[int, int]:
    run(["ip", "-4", "route", "flush", "table", str(NONE_TABLE)], check=False)
    run(["ip", "-4", "route", "add", "unreachable", "default", "table", str(NONE_TABLE)])
    replace_rule(NONE_MARK, NONE_TABLE, NONE_RULE_PRIORITY)
    return NONE_MARK, NONE_TABLE


def validate_interface_token(name: str) -> str:
    if not INTERFACE_RE.fullmatch(name):
        raise EgressError(f"unsafe interface name: {name!r}")
    return name


def ensure_nft_table(lan_interface: str, lan_cidr: str, active_mark: int) -> None:
    validate_interface_token(lan_interface)
    ipaddress.ip_network(lan_cidr, strict=False)
    exists = run(["nft", "list", "table", "inet", NFT_TABLE], check=False).returncode == 0
    if not exists:
        script = f"""table inet {NFT_TABLE} {{
  chain select_egress {{
    type filter hook prerouting priority -140; policy accept;
  }}
  chain direct_nat {{
    type nat hook postrouting priority srcnat; policy accept;
    meta mark 0x130001-0x1303e6 counter masquerade
  }}
  chain direct_forward {{
    type filter hook forward priority filter; policy accept;
    iifname "{lan_interface}" meta mark 0x130001-0x1303e6 counter accept
    oifname "{lan_interface}" ct state established,related counter accept
  }}
}}
"""
        run(["nft", "-f", "-"], input_text=script)
    switch_nft_mark(lan_interface, lan_cidr, active_mark)


def switch_nft_mark(lan_interface: str, lan_cidr: str, active_mark: int) -> None:
    script = f"""flush chain inet {NFT_TABLE} select_egress
add rule inet {NFT_TABLE} select_egress iifname \"{lan_interface}\" meta mark != 0 return
add rule inet {NFT_TABLE} select_egress iifname \"{lan_interface}\" ct mark & 0xff0000 == 0x130000 meta mark set ct mark
add rule inet {NFT_TABLE} select_egress iifname \"{lan_interface}\" meta mark != 0 return
add rule inet {NFT_TABLE} select_egress iifname \"{lan_interface}\" ip saddr {lan_cidr} meta mark set 0x{active_mark:x} ct mark set meta mark
"""
    run(["nft", "-f", "-"], input_text=script)


def replace_main_default_direct(interface: str, gateway: str, source: str, tun_interface: str) -> None:
    run(["ip", "-4", "route", "del", "unreachable", "default", "metric", "1"], check=False)
    while run(["ip", "-4", "route", "del", "default", "dev", tun_interface], check=False).returncode == 0:
        pass
    run(["ip", "-4", "route", "replace", "default", "via", gateway, "dev", interface, "src", source, "metric", "1"])


def replace_main_default_tunnel(tun_interface: str) -> None:
    run(["ip", "-4", "route", "del", "unreachable", "default", "metric", "1"], check=False)
    run(["ip", "-4", "route", "replace", "default", "dev", tun_interface, "src", "10.250.0.2", "metric", "1"])


def replace_main_default_none(tun_interface: str) -> None:
    while run(["ip", "-4", "route", "del", "default", "dev", tun_interface], check=False).returncode == 0:
        pass
    run(["ip", "-4", "route", "replace", "unreachable", "default", "metric", "1"])


def flush_failed_connections(path_id: int) -> None:
    mark = mark_for(path_id)
    result = run(["conntrack", "-D", "--mark", f"0x{mark:x}/0x{MARK_MASK:x}"], check=False)
    if result.returncode not in (0, 1):
        raise EgressError(result.stderr.strip() or "conntrack failed to clear failed-path sessions")


def select_kernel_physical(config: dict, lan_interface: str) -> tuple[int, str] | None:
    candidates: list[tuple[int, int, str]] = []
    for path in config.get("paths", []):
        if not path.get("enabled", True):
            continue
        try:
            path_id = validate_path_id(int(path["id"]))
            interface = validate_interface(str(path["interface_name"]), lan_interface)
            route = default_route(interface)
            primary_address(interface)
        except (KeyError, ValueError, EgressError):
            continue
        candidates.append((int(route.get("metric", 0)), path_id, interface))
    if not candidates:
        return None
    _, path_id, interface = min(candidates)
    return path_id, interface


def switch(args: argparse.Namespace) -> None:
    lan_interface = os.environ.get("ULINK_LAN_IF", "eth0")
    lan_cidr = os.environ.get("ULINK_LAN_CIDR", "192.168.145.0/24")
    tun_interface = os.environ.get("XBOND_TUN_IF", "xbond0")
    if args.flush_failed_path_id is not None and shutil.which("conntrack") is None:
        raise EgressError("conntrack is required to clear sessions from a hard-failed WAN")
    if args.egress == "tunnel":
        mark, _ = prepare_tunnel(tun_interface)
        ensure_nft_table(lan_interface, lan_cidr, mark)
        replace_main_default_tunnel(tun_interface)
    elif args.egress == "none":
        mark, _ = prepare_none()
        ensure_nft_table(lan_interface, lan_cidr, mark)
        replace_main_default_none(tun_interface)
    else:
        if args.path_id is None or not args.interface:
            raise EgressError("direct egress requires --path-id and --interface")
        mark, _, gateway, source = prepare_direct(args.path_id, args.interface, lan_interface)
        ensure_nft_table(lan_interface, lan_cidr, mark)
        replace_main_default_direct(args.interface, gateway, source, tun_interface)
    if args.flush_failed_path_id is not None:
        flush_failed_connections(args.flush_failed_path_id)
    write_state(
        {
            "egress": args.egress,
            "path_id": args.path_id,
            "interface": args.interface,
            "lan_interface": lan_interface,
            "lan_cidr": lan_cidr,
            "tun_interface": tun_interface,
        }
    )


def reconcile() -> None:
    apply_saved_state(read_state())


def apply_saved_state(state: dict) -> None:
    for environment_name, state_name in (
        ("ULINK_LAN_IF", "lan_interface"),
        ("ULINK_LAN_CIDR", "lan_cidr"),
        ("XBOND_TUN_IF", "tun_interface"),
    ):
        value = state.get(state_name)
        if isinstance(value, str) and value:
            os.environ[environment_name] = value
    namespace = argparse.Namespace(
        egress=state.get("egress"),
        path_id=state.get("path_id"),
        interface=state.get("interface"),
        flush_failed_path_id=None,
    )
    if namespace.egress not in ("tunnel", "direct", "none"):
        raise EgressError("saved egress state has an invalid target")
    switch(namespace)


def switch_with_rollback(args: argparse.Namespace) -> None:
    try:
        previous = read_state()
    except EgressError:
        previous = None
    try:
        switch(args)
    except Exception as error:
        if previous is None:
            raise
        try:
            apply_saved_state(previous)
        except Exception as rollback_error:
            raise EgressError(
                f"egress switch failed ({error}); rollback also failed ({rollback_error})"
            ) from error
        raise EgressError(f"egress switch failed and was rolled back: {error}") from error


def bootstrap(config_path: str, *, only_if_direct: bool) -> None:
    config = load_config(config_path)
    mode = str(config.get("traffic_mode", "tunnel")).lower()
    if mode not in ("direct-failover", "adaptive"):
        if only_if_direct:
            return
        namespace = argparse.Namespace(egress="tunnel", path_id=None, interface=None, flush_failed_path_id=None)
        switch_with_rollback(namespace)
        return
    lan_interface = os.environ.get("ULINK_LAN_IF", "eth0")
    selected = select_kernel_physical(config, lan_interface)
    if selected is None:
        raise EgressError("no configured physical WAN has a usable kernel default route")
    path_id, interface = selected
    namespace = argparse.Namespace(egress="direct", path_id=path_id, interface=interface, flush_failed_path_id=None)
    switch_with_rollback(namespace)


def status() -> None:
    payload = {
        "rules": run(["ip", "-j", "-4", "rule", "show"], check=False).stdout,
        "nft": run(["nft", "-j", "list", "table", "inet", NFT_TABLE], check=False).stdout,
    }
    print(json.dumps(payload))


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    switch_parser = subparsers.add_parser("switch")
    switch_parser.add_argument("--egress", choices=("tunnel", "direct", "none"), required=True)
    switch_parser.add_argument("--path-id", type=int)
    switch_parser.add_argument("--interface")
    switch_parser.add_argument("--flush-failed-path-id", type=int)
    bootstrap_parser = subparsers.add_parser("bootstrap")
    bootstrap_parser.add_argument("--config", default=DEFAULT_CONFIG)
    failsafe_parser = subparsers.add_parser("failsafe")
    failsafe_parser.add_argument("--config", default=DEFAULT_CONFIG)
    subparsers.add_parser("reconcile")
    subparsers.add_parser("status")
    args = parser.parse_args()
    try:
        import fcntl

        lock_path = pathlib.Path(os.environ.get("ULINK_EGRESS_LOCK", DEFAULT_LOCK))
        lock_path.parent.mkdir(parents=True, exist_ok=True)
        with lock_path.open("w", encoding="utf-8") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            if args.command == "switch":
                switch_with_rollback(args)
            elif args.command == "bootstrap":
                bootstrap(args.config, only_if_direct=False)
            elif args.command == "failsafe":
                bootstrap(args.config, only_if_direct=True)
            elif args.command == "reconcile":
                reconcile()
            else:
                status()
        return 0
    except (EgressError, OSError, subprocess.CalledProcessError, ValueError) as error:
        print(f"uLink egress error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
