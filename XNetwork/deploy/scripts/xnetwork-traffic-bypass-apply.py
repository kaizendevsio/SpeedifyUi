#!/usr/bin/env python3
import ipaddress
import json
import re
import subprocess
import sys
from pathlib import Path

TABLE_NAME = "xnetwork_bypass"
MARK_BASE = 0x120000
ROUTE_TABLE_BASE = 12100
PRIORITY_BASE = 12100
MAX_RULES = 64
UNSAFE_IFACE_PREFIXES = ("xbond", "connectify", "tailscale", "tun", "wg")
UNSAFE_IFACES = {"lo"}
IFACE_RE = re.compile(r"^[A-Za-z0-9_.:@-]{1,64}$")


def run(args, *, input_text=None, check=True):
    result = subprocess.run(
        args,
        input=input_text,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if check and result.returncode != 0:
        detail = (result.stderr or result.stdout or "").strip()
        raise RuntimeError(f"{' '.join(args)} failed: {detail}")
    return result


def unsafe_iface(name):
    lowered = name.lower()
    return name in UNSAFE_IFACES or any(lowered.startswith(prefix) for prefix in UNSAFE_IFACE_PREFIXES)


def read_defaults():
    result = run(["ip", "-j", "-4", "route", "show", "default"])
    routes = json.loads(result.stdout or "[]")
    defaults = []
    for route in routes:
        dev = str(route.get("dev") or "").strip()
        gateway = str(route.get("gateway") or "").strip()
        if not dev or unsafe_iface(dev):
            continue
        metric = route.get("metric", 0)
        try:
            metric = int(metric)
        except (TypeError, ValueError):
            metric = 0
        defaults.append({"dev": dev, "gateway": gateway, "metric": metric})
    return sorted(defaults, key=lambda item: item["metric"])


def parse_ports(values):
    ports = []
    for value in values or []:
        value = str(value).strip()
        if not value:
            continue
        if "-" in value:
            start, end = [part.strip() for part in value.split("-", 1)]
            start_int = int(start)
            end_int = int(end)
            if start_int < 1 or end_int > 65535 or start_int > end_int:
                raise ValueError(f"invalid port range {value}")
            ports.append(f"{start_int}-{end_int}")
        else:
            port = int(value)
            if port < 1 or port > 65535:
                raise ValueError(f"invalid port {value}")
            ports.append(str(port))
    return ports


def parse_destinations(values):
    destinations = []
    for value in values or []:
        value = str(value).strip()
        if not value:
            continue
        if "/" in value:
            destinations.append(str(ipaddress.ip_network(value, strict=False)))
        else:
            destinations.append(str(ipaddress.ip_address(value)))
    return destinations


def nft_set(values):
    if len(values) == 1:
        return values[0]
    return "{ " + ", ".join(values) + " }"


def route_for_rule(rule, defaults):
    mode = str(rule.get("egressMode") or "auto").strip().lower()
    if mode == "interface":
        dev = str(rule.get("interfaceName") or "").strip()
        if not dev or not IFACE_RE.match(dev) or unsafe_iface(dev):
            raise ValueError(f"rule {rule_name(rule)} selected unsafe or invalid interface {dev!r}")
        candidates = [item for item in defaults if item["dev"] == dev]
        if not candidates:
            raise ValueError(f"rule {rule_name(rule)} selected {dev}, but it has no IPv4 default route")
        return candidates[0]
    if not defaults:
        raise ValueError(f"rule {rule_name(rule)} needs a physical IPv4 default route, but none was found")
    return defaults[0]


def rule_name(rule):
    return str(rule.get("displayName") or rule.get("id") or "unnamed")


def build_match_lines(rule, mark):
    destinations = parse_destinations(rule.get("destinations"))
    ports = parse_ports(rule.get("ports"))
    protocol = str(rule.get("protocol") or "any").strip().lower()
    if protocol not in {"any", "tcp", "udp"}:
        raise ValueError(f"rule {rule_name(rule)} has invalid protocol {protocol!r}")
    if not destinations and not ports:
        raise ValueError(f"rule {rule_name(rule)} must include destinations or ports")

    dest_clause = f"ip daddr {nft_set(destinations)} " if destinations else ""
    if ports:
        protocols = ["tcp", "udp"] if protocol == "any" else [protocol]
        return [
            f"{dest_clause}ip protocol {proto} {proto} dport {nft_set(ports)} meta mark set 0x{mark:x}"
            for proto in protocols
        ]

    if protocol == "any":
        return [f"{dest_clause}meta mark set 0x{mark:x}"]
    return [f"{dest_clause}ip protocol {protocol} meta mark set 0x{mark:x}"]


def clear_rules():
    run(["nft", "delete", "table", "inet", TABLE_NAME], check=False)
    for offset in range(1, MAX_RULES + 1):
        priority = PRIORITY_BASE + offset
        table = ROUTE_TABLE_BASE + offset
        while run(["ip", "-4", "rule", "del", "priority", str(priority)], check=False).returncode == 0:
            pass
        run(["ip", "-4", "route", "flush", "table", str(table)], check=False)


def apply(path):
    # A router that has never saved a rule has no file yet; that means "no bypass rules",
    # which is a valid state to apply, not an error.
    config_path = Path(path)
    config = json.loads(config_path.read_text()) if config_path.exists() else {"rules": []}
    rules = [rule for rule in config.get("rules", []) if rule.get("enabled", True)]
    if len(rules) > MAX_RULES:
        raise ValueError(f"too many enabled bypass rules: {len(rules)} > {MAX_RULES}")

    defaults = read_defaults()
    resolved = []
    for index, rule in enumerate(rules, start=1):
        route = route_for_rule(rule, defaults)
        mark = MARK_BASE + index
        table = ROUTE_TABLE_BASE + index
        priority = PRIORITY_BASE + index
        match_lines = build_match_lines(rule, mark)
        resolved.append((rule, route, mark, table, priority, match_lines))

    clear_rules()
    if not resolved:
        print("Traffic bypass rules cleared.")
        return

    nft_lines = [
        f"table inet {TABLE_NAME} {{",
        "  chain prerouting {",
        "    type filter hook prerouting priority mangle; policy accept;",
    ]
    for _, _, _, _, _, match_lines in resolved:
        nft_lines.extend(f"    {line}" for line in match_lines)
    nft_lines.extend([
        "  }",
        "  chain output {",
        "    type route hook output priority mangle; policy accept;",
    ])
    for _, _, _, _, _, match_lines in resolved:
        nft_lines.extend(f"    {line}" for line in match_lines)
    nft_lines.extend(["  }", "}"])

    for _, route, mark, table, priority, _ in resolved:
        route_args = ["ip", "-4", "route", "replace", "default"]
        if route["gateway"]:
            route_args.extend(["via", route["gateway"]])
        route_args.extend(["dev", route["dev"], "table", str(table)])
        run(route_args)
        run(["ip", "-4", "rule", "add", "fwmark", f"0x{mark:x}/0xffffffff", "table", str(table), "priority", str(priority)])

    run(["nft", "-f", "-"], input_text="\n".join(nft_lines) + "\n")
    print(f"Traffic bypass rules applied: {len(resolved)} enabled.")


def status():
    nft = run(["nft", "list", "table", "inet", TABLE_NAME], check=False)
    if nft.stdout.strip():
        print(nft.stdout.strip())
    if nft.stderr.strip():
        print(nft.stderr.strip(), file=sys.stderr)

    rules = run(["ip", "-4", "rule", "show"], check=False)
    if rules.stdout.strip():
        print(rules.stdout.strip())


def main():
    if len(sys.argv) < 2 or sys.argv[1] not in {"apply", "clear", "status"}:
        print("usage: xnetwork-traffic-bypass-apply apply <settings.json> | clear | status", file=sys.stderr)
        return 2
    try:
        if sys.argv[1] == "clear":
            clear_rules()
            print("Traffic bypass rules cleared.")
        elif sys.argv[1] == "status":
            status()
        else:
            if len(sys.argv) != 3:
                raise ValueError("apply requires settings path")
            apply(sys.argv[2])
        return 0
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
