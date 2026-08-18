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


def build_match_lines(rule, mark, index):
    destinations = parse_destinations(rule.get("destinations"))
    domains = parse_domains(rule.get("domains"))
    ports = parse_ports(rule.get("ports"))
    protocol = str(rule.get("protocol") or "any").strip().lower()
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
                f"{dest_clause}ip protocol {proto} {proto} dport {nft_set(ports)} meta mark set 0x{mark:x}"
                for proto in protocols
            )
        elif protocol == "any":
            lines.append(f"{dest_clause}meta mark set 0x{mark:x}")
        else:
            lines.append(f"{dest_clause}ip protocol {protocol} meta mark set 0x{mark:x}")
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
        match_lines = build_match_lines(rule, mark, index)
        domains = parse_domains(rule.get("domains"))
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
    nft_lines.extend(["  }", "}"])

    for _, route, mark, table, priority, _, _, _ in resolved:
        route_args = ["ip", "-4", "route", "replace", "default"]
        if route["gateway"]:
            route_args.extend(["via", route["gateway"]])
        route_args.extend(["dev", route["dev"], "table", str(table)])
        run(route_args)
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
