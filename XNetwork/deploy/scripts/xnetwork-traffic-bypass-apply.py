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
DOMAIN_RE = re.compile(
    r"^(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))+$"
)
# Generated dnsmasq snippet: the XNetwork DNS instance includes this file, and dnsmasq
# adds every answer for the listed domains straight into the matching nftables set.
DNSMASQ_CONF_PATH = Path("/etc/xnetwork/dnsmasq-bypass-domains.conf")
DNSMASQ_SERVICE = "xnetwork-dns.service"
# Answers expire so an address that TikTok stops using does not stay bypassed forever.
SET_TIMEOUT = "1h"


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


def get_key(mapping, name, default=None):
    """Reads a key regardless of case.

    The app serialises this file with .NET's default PascalCase ("Rules",
    "Destinations"), while this script was written against camelCase. The mismatch made
    every rule invisible here: `config.get("rules")` returned nothing, so an apply
    reported success while programming no rules at all. Matching case-insensitively keeps
    both the existing files and any future naming working.
    """
    if not isinstance(mapping, dict):
        return default
    if name in mapping:
        return mapping[name]
    lowered = name.lower()
    for key, value in mapping.items():
        if isinstance(key, str) and key.lower() == lowered:
            return value
    return default


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


def parse_domains(values):
    domains = []
    for value in values or []:
        value = str(value).strip().lstrip(".").lower()
        if not value:
            continue
        if len(value) > 253 or not DOMAIN_RE.match(value):
            raise ValueError(f"invalid domain {value!r}")
        # A literal address belongs in destinations. dnsmasq would accept it as a domain
        # name and simply never match it, so reject it loudly instead.
        try:
            ipaddress.ip_address(value)
        except ValueError:
            pass
        else:
            raise ValueError(f"{value!r} is an IP address; put it in destinations, not domains")
        if value not in domains:
            domains.append(value)
    return domains


def domain_set_name(index):
    return f"bypass_dom_{index}"


def nft_set(values):
    if len(values) == 1:
        return values[0]
    return "{ " + ", ".join(values) + " }"


def route_for_rule(rule, defaults):
    mode = str(get_key(rule, "egressMode") or "auto").strip().lower()
    if mode == "block":
        # Matched traffic is routed into a blackhole instead of out an adapter. Exists to
        # cut off hardcoded in-app DNS endpoints so apps fall back to system DNS, where
        # domain-based bypass can see them.
        return {"blackhole": True, "dev": "", "gateway": "", "metric": 0}
    if mode == "interface":
        dev = str(get_key(rule, "interfaceName") or "").strip()
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
    return str(get_key(rule, "displayName") or get_key(rule, "id") or "unnamed")


def build_match_lines(rule, mark, index):
    destinations = parse_destinations(get_key(rule, "destinations"))
    domains = parse_domains(get_key(rule, "domains"))
    ports = parse_ports(get_key(rule, "ports"))
    protocol = str(get_key(rule, "protocol") or "any").strip().lower()
    if protocol not in {"any", "tcp", "udp"}:
        raise ValueError(f"rule {rule_name(rule)} has invalid protocol {protocol!r}")
    if not destinations and not ports and not domains:
        raise ValueError(f"rule {rule_name(rule)} must include destinations, domains, or ports")

    # Literal destinations and the DNS-populated set are alternatives, so each needs its
    # own rule line; a single line would require both to match.
    dest_clauses = []
    if destinations:
        dest_clauses.append(f"ip daddr {nft_set(destinations)} ")
    if domains:
        dest_clauses.append(f"ip daddr @{domain_set_name(index)} ")
    if not dest_clauses:
        dest_clauses.append("")

    lines = []
    for dest_clause in dest_clauses:
        if ports:
            protocols = ["tcp", "udp"] if protocol == "any" else [protocol]
            lines.extend(
                f"{dest_clause}ip protocol {proto} {proto} dport {nft_set(ports)} counter meta mark set 0x{mark:x}"
                for proto in protocols
            )
        elif protocol == "any":
            lines.append(f"{dest_clause}counter meta mark set 0x{mark:x}")
        else:
            lines.append(f"{dest_clause}ip protocol {protocol} counter meta mark set 0x{mark:x}")
    return lines


def write_dnsmasq_domains(resolved):
    """Regenerates the dnsmasq snippet and restarts the resolver only when it changed.

    Restarting drops in-flight DNS for a moment, so an unchanged apply must not do it.
    """
    lines = [
        "# Generated by xnetwork-traffic-bypass-apply. Do not edit.",
        "# Each nftset line makes dnsmasq add live answers for those domains to the",
        "# matching nftables set, which the bypass rules match on.",
    ]
    for rule, _, _, _, _, _, domains, index in resolved:
        if not domains:
            continue
        selector = "".join(f"/{domain}" for domain in domains)
        lines.append(f"# {rule_name(rule)}")
        lines.append(f"nftset={selector}/4#inet#{TABLE_NAME}#{domain_set_name(index)}")
    body = "\n".join(lines) + "\n"

    previous = DNSMASQ_CONF_PATH.read_text() if DNSMASQ_CONF_PATH.exists() else None
    if previous == body:
        return False

    DNSMASQ_CONF_PATH.parent.mkdir(parents=True, exist_ok=True)
    DNSMASQ_CONF_PATH.write_text(body)
    return True


def restart_dns_service():
    active = run(["systemctl", "is-active", DNSMASQ_SERVICE], check=False)
    if active.stdout.strip() != "active":
        # The resolver is not deployed or is intentionally stopped; domain rules simply
        # stay unpopulated rather than failing the whole apply.
        return
    run(["systemctl", "restart", DNSMASQ_SERVICE], check=False)


def install_route(table, route):
    """Installs the single default route that a rule's marked traffic should follow."""
    if route.get("blackhole"):
        run(["ip", "-4", "route", "replace", "blackhole", "default", "table", str(table)])
        return
    args = ["ip", "-4", "route", "replace", "default"]
    if route["gateway"]:
        args.extend(["via", route["gateway"]])
    args.extend(["dev", route["dev"], "table", str(table)])
    run(args)


def route_is_current(table, route):
    """True when the table already holds exactly the default route we want."""
    text = (run(["ip", "-4", "route", "show", "table", str(table)], check=False).stdout or "").strip()
    if not text:
        return False
    # Trailing space so "dev enx1" cannot match "dev enx10".
    first = text.splitlines()[0].strip() + " "
    if route.get("blackhole"):
        return first.startswith("blackhole default")
    if not first.startswith("default "):
        return False
    if route["gateway"] and f"via {route['gateway']} " not in first:
        return False
    return f"dev {route['dev']} " in first


def fwmark_rule_present(priority, table, mark):
    """True when the fwmark policy rule for this bypass rule is already installed.

    Checked because `ip rule add` is not idempotent -- repairing without this would stack
    a duplicate rule onto the table on every pass.
    """
    text = run(["ip", "-4", "rule", "show"], check=False).stdout or ""
    for line in text.splitlines():
        line = line.strip()
        if line.startswith(f"{priority}:") and f"0x{mark:x}" in line and f"lookup {table}" in line:
            return True
    return False


def repair(path):
    """Reinstalls only the policy routes and fwmark rules.

    The kernel deletes every route that references a device when that device goes down,
    and a DHCP lease renewal counts. That silently empties the bypass route tables while
    the nft marking and the fwmark rules survive, so matched traffic finds nothing in its
    table, falls through to main, and goes straight back down the tunnel. The bypass looks
    like it switched itself off, with the wrong exit country as the only symptom.

    Deliberately does NOT touch the nft table, its sets, or dnsmasq. `apply` deletes the
    table and recreates the sets EMPTY, then restarts the resolver to refill them from
    live DNS answers, so running apply on a timer would discard every cached address and
    break the very bypass it was meant to protect.
    """
    config_path = Path(path)
    config = json.loads(config_path.read_text()) if config_path.exists() else {"rules": []}
    rules = [rule for rule in get_key(config, "rules", []) or [] if get_key(rule, "enabled", True)]
    if not rules:
        print("No enabled traffic bypass rules to repair.")
        return

    # With no nft table nothing marks packets, so routes alone would achieve nothing.
    # That is a full-apply job; say so and let the caller escalate.
    if not (run(["nft", "list", "table", "inet", TABLE_NAME], check=False).stdout or "").strip():
        raise RuntimeError("nftables bypass table is missing; run apply instead of repair")

    defaults = read_defaults()
    repaired = []
    problems = []
    for index, rule in enumerate(rules, start=1):
        table = ROUTE_TABLE_BASE + index
        mark = MARK_BASE + index
        priority = PRIORITY_BASE + index
        try:
            route = route_for_rule(rule, defaults)
        except ValueError as exc:
            # One unroutable rule must not stop the others being repaired.
            problems.append(str(exc))
            continue
        if not route_is_current(table, route):
            install_route(table, route)
            repaired.append(
                f"{rule_name(rule)}: route -> {'blackhole' if route.get('blackhole') else route['dev']}")
        if not fwmark_rule_present(priority, table, mark):
            run(["ip", "-4", "rule", "add", "fwmark", f"0x{mark:x}/0xffffffff",
                 "table", str(table), "priority", str(priority)])
            repaired.append(f"{rule_name(rule)}: fwmark rule {priority}")

    for problem in problems:
        print(problem, file=sys.stderr)
    if repaired:
        print(f"Traffic bypass routes repaired: {len(repaired)} change(s): " + "; ".join(repaired))
    else:
        print("Traffic bypass routes already correct.")


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
    rules = [rule for rule in get_key(config, "rules", []) or [] if get_key(rule, "enabled", True)]
    if len(rules) > MAX_RULES:
        raise ValueError(f"too many enabled bypass rules: {len(rules)} > {MAX_RULES}")

    defaults = read_defaults()
    resolved = []
    for index, rule in enumerate(rules, start=1):
        route = route_for_rule(rule, defaults)
        mark = MARK_BASE + index
        table = ROUTE_TABLE_BASE + index
        priority = PRIORITY_BASE + index
        match_lines = build_match_lines(rule, mark, index)
        domains = parse_domains(get_key(rule, "domains"))
        resolved.append((rule, route, mark, table, priority, match_lines, domains, index))

    clear_rules()
    if not resolved:
        write_dnsmasq_domains(resolved)
        restart_dns_service()
        print("Traffic bypass rules cleared.")
        return

    nft_lines = [f"table inet {TABLE_NAME} {{"]
    # Sets are declared before the chains that reference them, and are what dnsmasq
    # populates from live DNS answers.
    for _, _, _, _, _, _, domains, index in resolved:
        if not domains:
            continue
        nft_lines.extend([
            f"  set {domain_set_name(index)} {{",
            "    type ipv4_addr",
            f"    timeout {SET_TIMEOUT}",
            "  }",
        ])
    nft_lines.extend([
        "  chain prerouting {",
        "    type filter hook prerouting priority mangle; policy accept;",
    ])
    for _, _, _, _, _, match_lines, _, _ in resolved:
        nft_lines.extend(f"    {line}" for line in match_lines)
    nft_lines.extend([
        "  }",
        "  chain output {",
        "    type route hook output priority mangle; policy accept;",
    ])
    for _, _, _, _, _, match_lines, _, _ in resolved:
        nft_lines.extend(f"    {line}" for line in match_lines)
    # Source NAT for the bypass path. Without this, a LAN client's packet is marked,
    # routed out a physical adapter, and leaves still carrying its private 192.168.x
    # source, so nothing can ever reply and every bypassed connection dies silently. The
    # host's own masquerade rule only covers traffic leaving via the tunnel device.
    nft_lines.extend([
        "  }",
        "  chain postrouting {",
        "    type nat hook postrouting priority srcnat; policy accept;",
    ])
    for _, _, mark, _, _, _, _, _ in resolved:
        nft_lines.append(f"    meta mark 0x{mark:x} counter masquerade")
    nft_lines.extend(["  }", "}"])

    for _, route, mark, table, priority, _, _, _ in resolved:
        install_route(table, route)
        run(["ip", "-4", "rule", "add", "fwmark", f"0x{mark:x}/0xffffffff", "table", str(table), "priority", str(priority)])

    run(["nft", "-f", "-"], input_text="\n".join(nft_lines) + "\n")

    # The sets were just recreated empty, so dnsmasq has to be restarted to repopulate
    # them even when its own config text is unchanged.
    domain_rules = [item for item in resolved if item[6]]
    write_dnsmasq_domains(resolved)
    if domain_rules:
        restart_dns_service()

    summary = f"Traffic bypass rules applied: {len(resolved)} enabled."
    if domain_rules:
        summary += f" {len(domain_rules)} domain-based (populated from DNS)."
    print(summary)


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
    if len(sys.argv) < 2 or sys.argv[1] not in {"apply", "repair", "clear", "status"}:
        print("usage: xnetwork-traffic-bypass-apply apply <settings.json> | repair <settings.json> | clear | status", file=sys.stderr)
        return 2
    try:
        if sys.argv[1] == "clear":
            clear_rules()
            print("Traffic bypass rules cleared.")
        elif sys.argv[1] == "status":
            status()
        elif sys.argv[1] == "repair":
            if len(sys.argv) != 3:
                raise ValueError("repair requires settings path")
            repair(sys.argv[2])
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
