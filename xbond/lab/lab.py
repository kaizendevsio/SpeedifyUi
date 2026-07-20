#!/usr/bin/env python3
"""Privileged, repeatable local XBond network-condition lab.

The lab executes the real xbond-client and xbond-server binaries in Linux
network namespaces. It intentionally does not mock XBond protocol behavior.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime as dt
import hashlib
import json
import os
import pathlib
import platform
import re
import shlex
import signal
import statistics
import subprocess
import sys
import time
import traceback
from dataclasses import dataclass, field
from typing import Any, Callable

import jsonschema


CLIENT_NS = "xbl-client"
SERVER_NS = "xbl-server"
ROUTER_NAMES = ("xbl-r1", "xbl-r2", "xbl-r3")
ALL_NAMESPACES = (CLIENT_NS, SERVER_NS, *ROUTER_NAMES)
SERVER_VIP = "10.255.0.1"
SERVER_PORT = 8444
CLIENT_TUN_IP = "10.250.0.2"
SERVER_TUN_IP = "10.250.0.1"
RESULTS_DIR = pathlib.Path(os.environ.get("LAB_RESULTS_DIR", "/results"))
RUN_ROOT = pathlib.Path("/run/xbond-lab")
LOG_ROOT = pathlib.Path("/tmp/xbond-lab")
PSK = os.environ.get("XBOND_PSK", "xbond-local-lab-key")


def utc_now() -> str:
    return dt.datetime.now(dt.timezone.utc).isoformat()


def run(
    command: list[str],
    *,
    check: bool = True,
    capture: bool = True,
    timeout: float | None = 60,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        command,
        check=check,
        text=True,
        capture_output=capture,
        timeout=timeout,
        env=env,
    )


def ns_cmd(namespace: str, *command: str) -> list[str]:
    return ["ip", "netns", "exec", namespace, *command]


def ns_run(
    namespace: str,
    command: list[str],
    *,
    check: bool = True,
    timeout: float | None = 60,
    env: dict[str, str] | None = None,
) -> subprocess.CompletedProcess[str]:
    return run(ns_cmd(namespace, *command), check=check, timeout=timeout, env=env)


def json_file(path: pathlib.Path) -> dict[str, Any] | None:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError, OSError):
        return None


def wait_for(predicate: Callable[[], bool], timeout: float, description: str) -> None:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(0.1)
    raise TimeoutError(f"timed out waiting for {description}")


def flatten(value: Any, prefix: str = "") -> dict[str, Any]:
    output: dict[str, Any] = {}
    if isinstance(value, dict):
        for key, nested in value.items():
            path = f"{prefix}.{key}" if prefix else key
            output.update(flatten(nested, path))
    elif isinstance(value, list):
        for index, nested in enumerate(value):
            output.update(flatten(nested, f"{prefix}[{index}]"))
    else:
        output[prefix] = value
    return output


def telemetry_subset(status: dict[str, Any] | None) -> dict[str, Any]:
    if not status:
        return {}
    needles = (
        "queue",
        "repair",
        "rebind",
        "socket_generation",
        "reorder",
        "late",
        "drop",
        "tun_",
        "retrans",
        "fec",
        "duplicate_usefulness",
        "pmtu",
        "emsgsize",
        "control_progress",
        "socket_ifindex",
        "schedule_generation",
        "oldest_age",
        "write_latency",
        "tun_write_micros",
    )
    return {
        key: value
        for key, value in flatten(status).items()
        if any(needle in key.lower() for needle in needles)
    }


def process_group_exists(process_group: int) -> bool:
    try:
        os.killpg(process_group, 0)
        return True
    except (ProcessLookupError, PermissionError):
        return False


def reap_terminated_children() -> None:
    while True:
        try:
            pid, _ = os.waitpid(-1, os.WNOHANG)
        except ChildProcessError:
            return
        if pid == 0:
            return


def namespace_pids(namespace: str) -> list[int]:
    completed = run(["ip", "netns", "pids", namespace], check=False)
    return sorted(
        int(value)
        for value in completed.stdout.split()
        if value.isdigit()
    )


def namespace_process_pids(namespace: str, process_name: str) -> list[int]:
    matches = []
    for pid in namespace_pids(namespace):
        try:
            name = pathlib.Path(f"/proc/{pid}/comm").read_text(
                encoding="utf-8"
            ).strip()
        except OSError:
            continue
        if name == process_name:
            matches.append(pid)
    return matches


def parse_timestamped_ping(
    output: str,
    command: list[str],
    *,
    restore_wall_time: float,
    steady_window_start_wall_time: float,
    steady_window_seconds: float,
    sample_interval: float,
    stopped_wall_time: float,
) -> dict[str, Any]:
    result = parse_ping(output, command)
    timestamps = [
        float(match.group(1))
        for line in output.splitlines()
        if (
            match := re.match(
                r"^\[([0-9]+(?:\.[0-9]+)?)\]\s+\d+\s+bytes\s+from\s+",
                line,
            )
        )
    ]
    post_restore = [stamp for stamp in timestamps if stamp >= restore_wall_time]
    first_success_seconds = (
        max(0.0, post_restore[0] - restore_wall_time)
        if post_restore
        else None
    )
    steady_window_end_wall_time = min(
        stopped_wall_time,
        steady_window_start_wall_time + steady_window_seconds,
    )
    steady_window_replies = [
        stamp
        for stamp in timestamps
        if steady_window_start_wall_time
        <= stamp
        <= steady_window_end_wall_time
    ]
    expected_post_restore = max(
        1,
        int(
            (
                steady_window_end_wall_time
                - steady_window_start_wall_time
            )
            / sample_interval
        )
        + 1,
    )
    post_restore_loss = max(
        0.0,
        100.0
        * (expected_post_restore - len(steady_window_replies))
        / expected_post_restore,
    )
    recent = post_restore[-10:]
    recent_gaps = [
        later - earlier for earlier, later in zip(recent, recent[1:])
    ]
    sustained_recovery = (
        len(recent) == 10
        and max(recent_gaps, default=0.0) <= max(0.75, sample_interval * 3)
        and stopped_wall_time - recent[-1] <= max(1.0, sample_interval * 5)
    )
    result.update(
        {
            "reply_timestamps": timestamps,
            "post_restore_reply_count": len(steady_window_replies),
            "post_restore_expected_count": expected_post_restore,
            "post_restore_loss_percent": round(post_restore_loss, 3),
            "steady_window_seconds": steady_window_seconds,
            "time_to_first_success_seconds": (
                round(first_success_seconds, 3)
                if first_success_seconds is not None
                else None
            ),
            "sustained_recovery": sustained_recovery,
        }
    )
    return result


def parse_ping(output: str, command: list[str]) -> dict[str, Any]:
    packets = re.search(
        r"(\d+) packets transmitted, (\d+) received, .*?([\d.]+)% packet loss",
        output,
    )
    timing = re.search(
        r"(?:rtt|round-trip) min/avg/max/(?:mdev|stddev) = "
        r"([\d.]+)/([\d.]+)/([\d.]+)/([\d.]+) ms",
        output,
    )
    result: dict[str, Any] = {"command": command, "raw": output[-4000:]}
    if packets:
        result.update(
            {
                "transmitted": int(packets.group(1)),
                "received": int(packets.group(2)),
                "loss_percent": float(packets.group(3)),
            }
        )
    if timing:
        result.update(
            {
                "min_ms": float(timing.group(1)),
                "avg_ms": float(timing.group(2)),
                "max_ms": float(timing.group(3)),
                "jitter_ms": float(timing.group(4)),
            }
        )
    return result


def ping(
    namespace: str,
    target: str,
    *,
    count: int = 8,
    interface: str | None = None,
    deadline: int = 20,
    size: int | None = None,
    do_not_fragment: bool = False,
    interval: float | None = None,
) -> dict[str, Any]:
    command = ["ping", "-n", "-c", str(count), "-w", str(deadline)]
    if interval is not None:
        command += ["-i", str(interval)]
    if interface:
        command += ["-I", interface]
    if size is not None:
        command += ["-s", str(size)]
    if do_not_fragment:
        command += ["-M", "do"]
    command.append(target)
    completed = ns_run(namespace, command, check=False, timeout=deadline + 5)
    combined = "\n".join(part for part in (completed.stdout, completed.stderr) if part)
    result = parse_ping(combined, command)
    result["exit_code"] = completed.returncode
    result["target"] = target
    result["interface"] = interface
    return result


def iperf(
    reverse: bool = False,
    *,
    seconds: int = 5,
    parallel: int = 1,
    omit: int = 1,
) -> dict[str, Any]:
    command = [
        "iperf3",
        "-c",
        SERVER_TUN_IP,
        "-J",
        "-t",
        str(seconds),
        "-O",
        str(omit),
        "-P",
        str(parallel),
    ]
    if reverse:
        command.append("-R")
    try:
        completed = ns_run(
            CLIENT_NS,
            command,
            check=False,
            timeout=seconds + omit + 20,
        )
    except subprocess.TimeoutExpired as exc:
        return {
            "direction": "download" if reverse else "upload",
            "command": command,
            "exit_code": -1,
            "timed_out": True,
            "valid_complete_json": False,
            "error": f"iperf3 exceeded its {seconds + omit + 20}s deadline",
            "mbps": 0.0,
        }

    return parse_iperf_output(
        completed.stdout,
        completed.stderr,
        reverse=reverse,
        command=command,
        return_code=completed.returncode,
    )


def tcp_retransmits(namespace: str) -> int | None:
    completed = ns_run(
        namespace,
        ["sh", "-lc", "nstat -az 2>/dev/null | awk '$1 == \"TcpRetransSegs\" {print $2}'"],
        check=False,
    )
    value = completed.stdout.strip()
    return int(value) if value.isdigit() else None


def counter_delta(before: int | None, after: int | None) -> int | None:
    if before is None or after is None:
        return None
    return max(0, after - before)


def trend_summary(
    timestamps: list[float],
    values: list[float],
    *,
    edge_samples: int = 5,
) -> dict[str, float | int | None]:
    if not values or len(values) != len(timestamps):
        return {
            "sample_count": len(values),
            "baseline": None,
            "end": None,
            "peak": None,
            "end_growth": None,
            "peak_growth": None,
            "slope_per_minute": None,
        }
    width = min(edge_samples, len(values))
    baseline = float(statistics.median(values[:width]))
    end = float(statistics.median(values[-width:]))
    peak = float(max(values))
    mean_time = statistics.fmean(timestamps)
    mean_value = statistics.fmean(values)
    denominator = sum((stamp - mean_time) ** 2 for stamp in timestamps)
    slope_per_second = (
        sum(
            (stamp - mean_time) * (value - mean_value)
            for stamp, value in zip(timestamps, values, strict=True)
        )
        / denominator
        if denominator > 0
        else 0.0
    )
    return {
        "sample_count": len(values),
        "baseline": baseline,
        "end": end,
        "peak": peak,
        "end_growth": end - baseline,
        "peak_growth": peak - baseline,
        "slope_per_minute": slope_per_second * 60,
    }


def per_metric_trends(
    samples: list[dict[str, Any]],
    predicate: Callable[[str], bool],
) -> tuple[dict[str, dict[str, float | int | None]], bool]:
    metric_keys = sorted(
        {
            f"{side}:{key}"
            for sample in samples
            for side in ("client_status", "server_status")
            for key, value in sample[side].items()
            if predicate(key) and isinstance(value, (int, float))
        }
    )
    trends: dict[str, dict[str, float | int | None]] = {}
    complete = bool(samples) and bool(metric_keys)
    for metric_key in metric_keys:
        side, key = metric_key.split(":", 1)
        values = [
            float(sample[side][key])
            for sample in samples
            if isinstance(sample[side].get(key), (int, float))
        ]
        timestamps = [
            float(sample["at_seconds"])
            for sample in samples
            if isinstance(sample[side].get(key), (int, float))
        ]
        if len(values) != len(samples):
            complete = False
        trends[metric_key] = trend_summary(timestamps, values)
    return trends, complete


def counter_growth_by_key(
    samples: list[dict[str, Any]],
    predicate: Callable[[str], bool],
) -> tuple[dict[str, float], bool]:
    if not samples:
        return {}, False
    first = samples[0]
    last = samples[-1]
    metric_keys = sorted(
        {
            f"{side}:{key}"
            for sample in samples
            for side in ("client_status", "server_status")
            for key, value in sample[side].items()
            if predicate(key) and isinstance(value, (int, float))
        }
    )
    growth: dict[str, float] = {}
    complete = bool(metric_keys)
    for metric_key in metric_keys:
        side, key = metric_key.split(":", 1)
        before = first[side].get(key)
        after = last[side].get(key)
        if not isinstance(before, (int, float)) or not isinstance(
            after, (int, float)
        ):
            complete = False
            continue
        growth[metric_key] = max(0.0, float(after) - float(before))
    return growth, complete


def sha256_file(path: pathlib.Path) -> str | None:
    try:
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        return digest.hexdigest()
    except OSError:
        return None


def parse_iperf_output(
    stdout: str,
    stderr: str,
    *,
    reverse: bool,
    command: list[str],
    return_code: int | None,
) -> dict[str, Any]:
    result: dict[str, Any] = {
        "direction": "download" if reverse else "upload",
        "exit_code": return_code,
        "command": command,
        "valid_complete_json": False,
        "mbps": 0.0,
    }
    try:
        payload = json.loads(stdout)
    except json.JSONDecodeError as error:
        result["error"] = (
            f"iperf3 did not emit complete valid JSON: {error}; "
            f"{(stderr or stdout)[-4000:]}"
        )
        return result

    summary_name = "sum_received" if reverse else "sum_sent"
    summary = payload.get("end", {}).get(summary_name)
    bits_per_second = summary.get("bits_per_second") if isinstance(summary, dict) else None
    reported_error = payload.get("error")
    if (
        return_code != 0
        or not isinstance(bits_per_second, (int, float))
        or reported_error
    ):
        result["error"] = (
            str(reported_error)
            if reported_error
            else (
                f"iperf3 exited with {return_code} or omitted "
                f"end.{summary_name}.bits_per_second"
            )
        )
        result["raw"] = payload
        return result

    result.update(
        {
            "valid_complete_json": True,
            "mbps": float(bits_per_second) / 1_000_000,
            "retransmits": payload.get("end", {})
            .get("sum_sent", {})
            .get("retransmits"),
            "raw": payload,
        }
    )
    return result


def nested_number(value: dict[str, Any] | None, *keys: str) -> float:
    current: Any = value or {}
    for key in keys:
        if not isinstance(current, dict):
            return 0.0
        current = current.get(key)
    return float(current) if isinstance(current, (int, float)) else 0.0


def session_evidence(log_text: str) -> dict[str, Any]:
    events = []
    for line in log_text.splitlines():
        with contextlib.suppress(json.JSONDecodeError):
            payload = json.loads(line)
            event = payload.get("event")
            if event in {
                "tunnel-started",
                "session-open-sent",
                "session-accepted",
                "schedule-accepted",
            }:
                events.append(payload)
    session_ids = list(
        dict.fromkeys(
            int(item["session_id"])
            for item in events
            if isinstance(item.get("session_id"), int)
        )
    )
    return {
        "events": events[-20:],
        "session_ids": session_ids,
        "session_id": session_ids[-1] if session_ids else None,
        "session_accepted": any(item.get("event") == "session-accepted" for item in events),
        "schedule_accepted": any(item.get("event") == "schedule-accepted" for item in events),
    }


def process_metrics(process: subprocess.Popen[str] | None) -> dict[str, Any]:
    if process is None:
        return {"running": False}
    running = process.poll() is None
    result: dict[str, Any] = {"pid": process.pid, "running": running}
    completed = run(
        ["ps", "-p", str(process.pid), "-o", "pcpu=,rss=,etime=,stat="],
        check=False,
    )
    fields = completed.stdout.split()
    if len(fields) >= 4:
        with contextlib.suppress(ValueError):
            result["cpu_percent"] = float(fields[0])
            result["rss_kib"] = int(fields[1])
        result["elapsed"] = fields[2]
        result["state"] = fields[3]
    return result


@dataclass
class LabResult:
    scenario: str
    started_at: str = field(default_factory=utc_now)
    monotonic_start: float = field(default_factory=time.monotonic)
    status: str = "error"
    reason: str | None = None
    metrics: dict[str, Any] = field(default_factory=dict)
    thresholds: dict[str, Any] = field(default_factory=dict)
    notes: list[str] = field(default_factory=list)
    errors: list[str] = field(default_factory=list)
    cleanup: dict[str, Any] = field(default_factory=dict)
    provenance: dict[str, Any] = field(default_factory=dict)

    def finish(self) -> dict[str, Any]:
        return {
            "schema_version": 1,
            "scenario": self.scenario,
            "status": self.status,
            "reason": self.reason,
            "started_at": self.started_at,
            "finished_at": utc_now(),
            "duration_seconds": round(time.monotonic() - self.monotonic_start, 3),
            "metrics": self.metrics,
            "thresholds": self.thresholds,
            "cleanup": self.cleanup,
            "provenance": self.provenance,
            "notes": self.notes,
            "errors": self.errors,
        }


class XBondLab:
    def __init__(self, scenario: str, result: LabResult):
        self.scenario = scenario
        self.result = result
        self.client: subprocess.Popen[str] | None = None
        self.server: subprocess.Popen[str] | None = None
        self.iperf_server: subprocess.Popen[str] | None = None
        self.direct_probe_server: subprocess.Popen[str] | None = None
        self.extra_processes: list[subprocess.Popen[str]] = []
        self.log_handles: list[Any] = []
        self.client_status = RUN_ROOT / "client-status.json"
        self.server_status = RUN_ROOT / "server-status.json"
        self.client_config = RUN_ROOT / "client.toml"
        self.process_pids: list[int] = []
        self.process_groups: set[int] = set()

    def track_process(self, process: subprocess.Popen[str]) -> subprocess.Popen[str]:
        self.extra_processes.append(process)
        self._record_process(process)
        return process

    def _record_process(self, process: subprocess.Popen[str]) -> None:
        self.process_pids.append(process.pid)
        with contextlib.suppress(ProcessLookupError):
            self.process_groups.add(os.getpgid(process.pid))

    def stop_process(
        self,
        process: subprocess.Popen[str] | None,
        *,
        signal_number: signal.Signals = signal.SIGTERM,
        timeout: float = 5,
    ) -> None:
        if process is None:
            return
        process_group = None
        with contextlib.suppress(ProcessLookupError):
            process_group = os.getpgid(process.pid)
        if process_group is None:
            process_group = process.pid if process.pid in self.process_groups else None
        if process_group is not None and process_group_exists(process_group):
            with contextlib.suppress(ProcessLookupError):
                os.killpg(process_group, signal_number)
        elif process.poll() is None:
            process.send_signal(signal_number)
        try:
            process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            if process_group is not None and process_group_exists(process_group):
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(process_group, signal.SIGKILL)
            elif process.poll() is None:
                process.kill()
            with contextlib.suppress(subprocess.TimeoutExpired):
                process.wait(timeout=3)

    def clean_existing(self) -> None:
        for namespace in ALL_NAMESPACES:
            run(["ip", "netns", "del", namespace], check=False)
        RUN_ROOT.mkdir(parents=True, exist_ok=True)
        LOG_ROOT.mkdir(parents=True, exist_ok=True)
        for path in (self.client_status, self.server_status, self.client_config):
            path.unlink(missing_ok=True)

    def setup_topology(self) -> None:
        self.clean_existing()
        for namespace in ALL_NAMESPACES:
            run(["ip", "netns", "add", namespace])
            ns_run(namespace, ["ip", "link", "set", "lo", "up"])
            ns_run(namespace, ["sysctl", "-qw", "net.ipv4.conf.all.rp_filter=0"])
            ns_run(namespace, ["sysctl", "-qw", "net.ipv4.conf.default.rp_filter=0"])

        for index, router in enumerate(ROUTER_NAMES, start=1):
            client_if = f"cpath{index}"
            router_client_if = f"r{index}c"
            server_if = f"spath{index}"
            router_server_if = f"r{index}s"

            run(["ip", "link", "add", client_if, "type", "veth", "peer", "name", router_client_if])
            run(["ip", "link", "set", client_if, "netns", CLIENT_NS])
            run(["ip", "link", "set", router_client_if, "netns", router])
            run(["ip", "link", "add", server_if, "type", "veth", "peer", "name", router_server_if])
            run(["ip", "link", "set", server_if, "netns", SERVER_NS])
            run(["ip", "link", "set", router_server_if, "netns", router])

            client_ip = f"10.{index}.0.2"
            client_gateway = f"10.{index}.0.1"
            server_ip = f"10.{10 + index}.0.2"
            server_gateway = f"10.{10 + index}.0.1"
            table = str(100 + index)

            ns_run(CLIENT_NS, ["ip", "addr", "add", f"{client_ip}/24", "dev", client_if])
            ns_run(CLIENT_NS, ["ip", "link", "set", client_if, "up"])
            ns_run(router, ["ip", "addr", "add", f"{client_gateway}/24", "dev", router_client_if])
            ns_run(router, ["ip", "link", "set", router_client_if, "up"])
            ns_run(SERVER_NS, ["ip", "addr", "add", f"{server_ip}/24", "dev", server_if])
            ns_run(SERVER_NS, ["ip", "link", "set", server_if, "up"])
            ns_run(router, ["ip", "addr", "add", f"{server_gateway}/24", "dev", router_server_if])
            ns_run(router, ["ip", "link", "set", router_server_if, "up"])
            ns_run(router, ["sysctl", "-qw", "net.ipv4.ip_forward=1"])

            ns_run(
                CLIENT_NS,
                ["ip", "route", "add", f"{SERVER_VIP}/32", "via", client_gateway, "dev", client_if, "table", table],
            )
            ns_run(
                CLIENT_NS,
                [
                    "ip",
                    "route",
                    "add",
                    f"{server_ip}/32",
                    "via",
                    client_gateway,
                    "dev",
                    client_if,
                    "table",
                    table,
                ],
            )
            ns_run(
                CLIENT_NS,
                ["ip", "route", "add", f"{server_ip}/32", "via", client_gateway, "dev", client_if],
            )
            ns_run(
                CLIENT_NS,
                ["ip", "route", "add", "1.1.1.1/32", "via", client_gateway, "dev", client_if, "table", table],
            )
            ns_run(CLIENT_NS, ["ip", "rule", "add", "from", f"{client_ip}/32", "lookup", table])
            ns_run(
                SERVER_NS,
                ["ip", "route", "add", f"10.{index}.0.0/24", "via", server_gateway, "dev", server_if],
            )
            ns_run(router, ["ip", "route", "add", f"{SERVER_VIP}/32", "via", server_ip, "dev", router_server_if])
            ns_run(
                router,
                [
                    "iptables",
                    "-t",
                    "nat",
                    "-A",
                    "PREROUTING",
                    "-p",
                    "tcp",
                    "-d",
                    "1.1.1.1",
                    "--dport",
                    "443",
                    "-j",
                    "DNAT",
                    "--to-destination",
                    f"{server_ip}:443",
                ],
            )

        ns_run(SERVER_NS, ["ip", "addr", "add", f"{SERVER_VIP}/32", "dev", "lo"])

    def write_config(
        self,
        *,
        path_count: int = 3,
        queue_capacity: int = 2048,
        inbound_capacity: int = 4096,
        mode: str = "anchor-duplicate-1",
        policy: str = "balanced",
    ) -> None:
        paths = []
        for index in range(1, path_count + 1):
            paths.append(
                "\n".join(
                    [
                        "[[paths]]",
                        f"id = {index}",
                        f'name = "Lab path {index}"',
                        f'interface_name = "cpath{index}"',
                        f'bind_addr = "10.{index}.0.2:0"',
                        "enabled = true",
                    ]
                )
            )
        content = "\n".join(
            [
                "enabled = true",
                f'server_addr = "{SERVER_VIP}:{SERVER_PORT}"',
                f'mode = "{mode}"',
                f'redundancy_policy = "{policy}"',
                f"max_active_backups = {max(0, path_count - 1)}",
                "realtime_deadline_ms = 500",
                "interactive_packet_threshold_bytes = 768",
                "duplicate_loss_threshold = 0.02",
                "backup_loss_disable_threshold = 0.35",
                "reorder_hold_ms = 25",
                f"tun_queue_capacity = {queue_capacity}",
                f"inbound_queue_capacity = {inbound_capacity}",
                "udp_socket_buffer_bytes = 4194304",
                "recovery_enabled = true",
                "recovery_enter_degraded_ticks = 3",
                "recovery_exit_clean_ticks = 20",
                "recovery_path_loss_exclude_threshold = 0.85",
                f'runtime_status_path = "{self.client_status}"',
                "",
                *paths,
                "",
            ]
        )
        self.client_config.write_text(content, encoding="utf-8")

    def _start(
        self,
        namespace: str,
        command: list[str],
        log_name: str,
        *,
        env_overrides: dict[str, str] | None = None,
    ) -> subprocess.Popen[str]:
        env = os.environ.copy()
        env["XBOND_PSK"] = PSK
        if env_overrides:
            env.update(env_overrides)
        log_path = LOG_ROOT / f"{self.scenario}-{log_name}.log"
        handle = log_path.open("w", encoding="utf-8")
        self.log_handles.append(handle)
        process = subprocess.Popen(
            ns_cmd(namespace, *command),
            stdout=handle,
            stderr=subprocess.STDOUT,
            text=True,
            env=env,
            start_new_session=True,
        )
        self._record_process(process)
        return process

    def start_runtime(
        self,
        *,
        path_count: int = 3,
        queue_capacity: int = 2048,
        inbound_capacity: int = 4096,
        tun_mtu: int = 1400,
        client_env: dict[str, str] | None = None,
        client_extra_args: list[str] | None = None,
        server_extra_args: list[str] | None = None,
        client_log_name: str = "client",
        server_log_name: str = "server",
        direct_probe_log_name: str = "direct-probe",
    ) -> None:
        self.write_config(
            path_count=path_count,
            queue_capacity=queue_capacity,
            inbound_capacity=inbound_capacity,
        )
        self.server = self._start(
            SERVER_NS,
            [
                "/opt/xbond/bin/xbond-server",
                "--bind",
                f"{SERVER_VIP}:{SERVER_PORT}",
                "--tun-name",
                "xbonds0",
                "--tun-mtu",
                str(tun_mtu),
                "--status-path",
                str(self.server_status),
                "--tun-queue-capacity",
                str(queue_capacity),
                "--inbound-queue-capacity",
                str(inbound_capacity),
                "--server-health-enabled",
                "false",
                "--json-events",
                *(server_extra_args or []),
            ],
            server_log_name,
        )
        self.direct_probe_server = self._start(
            SERVER_NS,
            ["python3", "-m", "http.server", "443", "--bind", "0.0.0.0"],
            direct_probe_log_name,
        )
        wait_for(
            lambda: ns_run(SERVER_NS, ["ip", "link", "show", "xbonds0"], check=False).returncode == 0,
            15,
            "server TUN",
        )
        ns_run(SERVER_NS, ["ip", "addr", "replace", f"{SERVER_TUN_IP}/30", "dev", "xbonds0"])
        ns_run(SERVER_NS, ["ip", "link", "set", "xbonds0", "mtu", str(tun_mtu), "up"])

        self.client = self._start(
            CLIENT_NS,
            [
                "/opt/xbond/bin/xbond-client",
                "tunnel",
                "--config",
                str(self.client_config),
                "--tun-name",
                "xbond0",
                "--tun-mtu",
                str(tun_mtu),
                "--control-socket",
                str(RUN_ROOT / "client-control.sock"),
                "--json-events",
                *(client_extra_args or []),
            ],
            client_log_name,
            env_overrides=client_env,
        )
        wait_for(
            lambda: ns_run(CLIENT_NS, ["ip", "link", "show", "xbond0"], check=False).returncode == 0,
            15,
            "client TUN",
        )
        ns_run(CLIENT_NS, ["ip", "addr", "replace", f"{CLIENT_TUN_IP}/30", "dev", "xbond0"])
        ns_run(CLIENT_NS, ["ip", "link", "set", "xbond0", "mtu", str(tun_mtu), "up"])
        wait_for(lambda: self.client_status.exists(), 15, "client status")
        wait_for(lambda: self.server_status.exists(), 15, "server status")
        wait_for(
            lambda: (
                (json_file(self.client_status) or {}).get("anchor_path_id") is not None
                and not (json_file(self.server_status) or {}).get("schedule_required", True)
            ),
            20,
            "authenticated session and acknowledged return schedule",
        )

        self.iperf_server = self._start(
            SERVER_NS,
            ["iperf3", "-s", "-B", SERVER_TUN_IP, "-1"],
            "iperf",
        )

    def restart_server(
        self,
        *,
        log_name: str = "server-restart",
        tun_mtu: int = 1400,
        queue_capacity: int = 2048,
        inbound_capacity: int = 4096,
        server_extra_args: list[str] | None = None,
    ) -> int | None:
        previous_return_code = None
        if self.server:
            self.stop_process(self.server)
            previous_return_code = self.server.poll()

        self.server_status.unlink(missing_ok=True)
        self.server = self._start(
            SERVER_NS,
            [
                "/opt/xbond/bin/xbond-server",
                "--bind",
                f"{SERVER_VIP}:{SERVER_PORT}",
                "--tun-name",
                "xbonds0",
                "--tun-mtu",
                str(tun_mtu),
                "--status-path",
                str(self.server_status),
                "--tun-queue-capacity",
                str(queue_capacity),
                "--inbound-queue-capacity",
                str(inbound_capacity),
                "--server-health-enabled",
                "false",
                "--json-events",
                *(server_extra_args or []),
            ],
            log_name,
        )
        wait_for(
            lambda: ns_run(
                SERVER_NS, ["ip", "link", "show", "xbonds0"], check=False
            ).returncode
            == 0,
            15,
            "restarted server TUN",
        )
        ns_run(
            SERVER_NS,
            ["ip", "addr", "replace", f"{SERVER_TUN_IP}/30", "dev", "xbonds0"],
        )
        ns_run(
            SERVER_NS,
            ["ip", "link", "set", "xbonds0", "mtu", str(tun_mtu), "up"],
        )
        wait_for(lambda: self.server_status.exists(), 15, "restarted server status")
        return previous_return_code

    def restart_iperf_server(self) -> None:
        self.stop_process(self.iperf_server, timeout=2)
        self.iperf_server = self._start(
            SERVER_NS,
            ["iperf3", "-s", "-B", SERVER_TUN_IP, "-1"],
            f"iperf-{time.time_ns()}",
        )
        time.sleep(0.2)

    def start_iperf_server(
        self,
        *,
        bind_addr: str = SERVER_TUN_IP,
        port: int = 5201,
        log_name: str | None = None,
    ) -> subprocess.Popen[str]:
        process = self._start(
            SERVER_NS,
            ["iperf3", "-s", "-B", bind_addr, "-p", str(port), "-1"],
            log_name or f"iperf-{port}-{time.time_ns()}",
        )
        self.extra_processes.append(process)
        time.sleep(0.2)
        return process

    def sample_runtime(self) -> dict[str, Any]:
        client_status = json_file(self.client_status) or {}
        server_status = json_file(self.server_status) or {}
        schedule = client_status.get("schedule") or {}
        scheduled_path_ids = sorted(
            {
                int(path_id)
                for key in ("data_path_ids", "duplicate_path_ids", "fec_path_ids")
                for path_id in schedule.get(key, [])
                if isinstance(path_id, int)
            }
        )
        return {
            "at_monotonic": time.monotonic(),
            "client_process": process_metrics(self.client),
            "server_process": process_metrics(self.server),
            "client_status_mtime_ns": self.client_status.stat().st_mtime_ns
            if self.client_status.exists()
            else None,
            "server_status_mtime_ns": self.server_status.stat().st_mtime_ns
            if self.server_status.exists()
            else None,
            "tunnel_last_success_age_ms": nested_number(
                client_status, "tunnel", "last_success_age_ms"
            ),
            "tunnel_success_rate": nested_number(
                client_status, "tunnel", "success_rate"
            ),
            "server_control_progress_at_micros": nested_number(
                server_status, "control_plane", "last_control_progress_at_micros"
            ),
            "recovery_active": bool(
                (client_status.get("recovery") or {}).get("active")
            ),
            "scheduled_path_ids": scheduled_path_ids,
            "path_health": [
                {
                    "path_id": path.get("path_id"),
                    "interface_up": path.get("interface_up"),
                    "in_cooldown": path.get("in_cooldown"),
                    "loss_rate": path.get("loss_rate"),
                    "rtt_ms": path.get("rtt_ms"),
                    "stale_ack_ms": path.get("stale_ack_ms"),
                    "send_failure_streak": path.get("send_failure_streak"),
                }
                for path in client_status.get("paths", [])
            ],
            "client_telemetry": telemetry_subset(client_status),
            "server_telemetry": telemetry_subset(server_status),
        }

    def restart_client(
        self,
        *,
        log_name: str = "client-restart",
        tun_mtu: int = 1400,
        env_overrides: dict[str, str] | None = None,
    ) -> int | None:
        previous_return_code = None
        if self.client:
            self.stop_process(self.client)
            previous_return_code = self.client.poll()

        self.client_status.unlink(missing_ok=True)
        (RUN_ROOT / "client-control.sock").unlink(missing_ok=True)
        self.client = self._start(
            CLIENT_NS,
            [
                "/opt/xbond/bin/xbond-client",
                "tunnel",
                "--config",
                str(self.client_config),
                "--tun-name",
                "xbond0",
                "--tun-mtu",
                str(tun_mtu),
                "--control-socket",
                str(RUN_ROOT / "client-control.sock"),
                "--json-events",
            ],
            log_name,
            env_overrides=env_overrides,
        )
        wait_for(
            lambda: ns_run(CLIENT_NS, ["ip", "link", "show", "xbond0"], check=False).returncode == 0,
            15,
            "restarted client TUN",
        )
        ns_run(CLIENT_NS, ["ip", "addr", "replace", f"{CLIENT_TUN_IP}/30", "dev", "xbond0"])
        ns_run(CLIENT_NS, ["ip", "link", "set", "xbond0", "mtu", str(tun_mtu), "up"])
        wait_for(
            lambda: (
                (json_file(self.client_status) or {}).get("anchor_path_id") is not None
                and not (json_file(self.server_status) or {}).get("schedule_required", True)
            ),
            20,
            "fresh session and acknowledged return schedule",
        )
        return previous_return_code

    def restart_client_supervised(
        self,
        *,
        log_name: str = "client-supervisor",
        tun_mtu: int = 1400,
    ) -> int | None:
        previous_return_code = None
        if self.client:
            self.stop_process(self.client)
            previous_return_code = self.client.poll()

        self.client_status.unlink(missing_ok=True)
        (RUN_ROOT / "client-control.sock").unlink(missing_ok=True)
        client_command = [
            "/opt/xbond/bin/xbond-client",
            "tunnel",
            "--config",
            str(self.client_config),
            "--tun-name",
            "xbond0",
            "--tun-mtu",
            str(tun_mtu),
            "--control-socket",
            str(RUN_ROOT / "client-control.sock"),
            "--json-events",
        ]
        shell_script = "\n".join(
            [
                "restart_count=0",
                "while true; do",
                '  echo "LAB_SUPERVISOR_START count=$restart_count"',
                f"  {shlex.join(client_command)} &",
                "  child=$!",
                "  for _ in $(seq 1 300); do",
                "    if ip link show xbond0 >/dev/null 2>&1; then",
                f"      ip addr replace {CLIENT_TUN_IP}/30 dev xbond0",
                f"      ip link set xbond0 mtu {tun_mtu} up",
                "      break",
                "    fi",
                "    sleep 0.05",
                "  done",
                '  wait "$child"',
                "  rc=$?",
                '  echo "LAB_SUPERVISOR_EXIT count=$restart_count exit_code=$rc"',
                '  if [ "$rc" -eq 0 ]; then exit 0; fi',
                "  restart_count=$((restart_count + 1))",
                '  echo "LAB_SUPERVISOR_RESTART count=$restart_count"',
                "  sleep 0.25",
                "done",
            ]
        )
        self.client = self._start(
            CLIENT_NS,
            ["sh", "-lc", shell_script],
            log_name,
        )
        wait_for(
            lambda: (
                (json_file(self.client_status) or {}).get("anchor_path_id")
                is not None
                and not (json_file(self.server_status) or {}).get(
                    "schedule_required", True
                )
            ),
            20,
            "supervised client session and acknowledged return schedule",
        )
        return previous_return_code

    def client_supervisor_restart_count(self, log_name: str) -> int:
        matches = re.findall(
            r"LAB_SUPERVISOR_RESTART count=(\d+)",
            log_tail(self.scenario, log_name, limit=200000),
        )
        return max((int(value) for value in matches), default=0)

    def collect_runtime_metrics(self) -> dict[str, Any]:
        client_status = json_file(self.client_status)
        server_status = json_file(self.server_status)
        return {
            "processes": {
                "client": process_metrics(self.client),
                "server": process_metrics(self.server),
            },
            "tcp_retransmits": {
                "client": tcp_retransmits(CLIENT_NS),
                "server": tcp_retransmits(SERVER_NS),
            },
            "client_status": client_status,
            "server_status": server_status,
            "telemetry": {
                "client": telemetry_subset(client_status),
                "server": telemetry_subset(server_status),
            },
        }

    def collect_failure_diagnostics(self) -> dict[str, Any]:
        diagnostics: dict[str, Any] = {
            "runtime": self.collect_runtime_metrics(),
            "logs": {},
        }
        for log_path in sorted(LOG_ROOT.glob(f"{self.scenario}-*.log")):
            try:
                lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
                diagnostics["logs"][log_path.name] = lines[-200:]
            except OSError as error:
                diagnostics["logs"][log_path.name] = [f"Could not read log: {error}"]
        return diagnostics

    def physical_ping(self, path: int, count: int = 3) -> dict[str, Any]:
        return ping(
            CLIENT_NS,
            f"10.{10 + path}.0.2",
            count=count,
            interface=f"cpath{path}",
            deadline=max(10, count * 3),
        )

    def tunnel_ping(self, count: int = 8) -> dict[str, Any]:
        return ping(
            CLIENT_NS,
            SERVER_TUN_IP,
            count=count,
            interface="xbond0",
            deadline=max(20, count * 3),
        )

    def apply_netem(self, path: int, specification: str, *, both_directions: bool = True) -> None:
        router = ROUTER_NAMES[path - 1]
        interfaces = [f"r{path}c"]
        if both_directions:
            interfaces.append(f"r{path}s")
        for interface in interfaces:
            ns_run(
                router,
                ["tc", "qdisc", "replace", "dev", interface, "root", "netem", *shlex.split(specification)],
            )

    def clear_netem(self) -> None:
        for path, router in enumerate(ROUTER_NAMES, start=1):
            for interface in (f"r{path}c", f"r{path}s"):
                ns_run(router, ["tc", "qdisc", "del", "dev", interface, "root"], check=False)
            ns_run(router, ["iptables", "-F"], check=False)

    def qdisc_snapshot(self) -> dict[str, Any]:
        output: dict[str, Any] = {}
        for path, router in enumerate(ROUTER_NAMES, start=1):
            for interface in (f"r{path}c", f"r{path}s"):
                completed = ns_run(router, ["tc", "-s", "qdisc", "show", "dev", interface], check=False)
                output[f"{router}/{interface}"] = completed.stdout.strip()
        return output

    def stop_runtime(self) -> None:
        processes = (
            self.iperf_server,
            self.direct_probe_server,
            self.client,
            self.server,
            *self.extra_processes,
        )
        stopped: set[int] = set()
        for process in processes:
            if process is None or process.pid in stopped:
                continue
            stopped.add(process.pid)
            self.stop_process(process)
        self.iperf_server = None
        self.direct_probe_server = None
        self.client = None
        self.server = None
        self.extra_processes.clear()
        for handle in self.log_handles:
            handle.close()
        self.log_handles.clear()

    def cleanup(self) -> dict[str, Any]:
        self.clear_netem()
        qdisc_after_clear = self.qdisc_snapshot()
        qdisc_remaining = sorted(
            name
            for name, output in qdisc_after_clear.items()
            if "netem" in output.lower()
        )
        self.stop_runtime()
        for process_group in self.process_groups:
            if process_group_exists(process_group):
                with contextlib.suppress(ProcessLookupError):
                    os.killpg(process_group, signal.SIGKILL)
        cleanup_deadline = time.monotonic() + 3
        while time.monotonic() < cleanup_deadline:
            reap_terminated_children()
            if not any(
                process_group_exists(process_group)
                for process_group in self.process_groups
            ):
                break
            time.sleep(0.05)
        namespace_pids_remaining = {}
        for namespace in ALL_NAMESPACES:
            pids = namespace_pids(namespace)
            if pids:
                for pid in pids:
                    with contextlib.suppress(ProcessLookupError):
                        os.kill(pid, signal.SIGKILL)
                time.sleep(0.1)
                pids = namespace_pids(namespace)
            if pids:
                namespace_pids_remaining[namespace] = pids
        for namespace in ALL_NAMESPACES:
            run(["ip", "netns", "del", namespace], check=False)
        remaining_namespaces = [
            name
            for name in run(["ip", "netns", "list"], check=False).stdout.split()
            if name.startswith("xbl-")
        ]
        remaining_processes = [
            pid for pid in self.process_pids if pathlib.Path(f"/proc/{pid}").exists()
        ]
        remaining_process_groups = sorted(
            process_group
            for process_group in self.process_groups
            if process_group_exists(process_group)
        )
        return {
            "namespaces_remaining": sorted(set(remaining_namespaces)),
            "processes_remaining": remaining_processes,
            "process_groups_remaining": remaining_process_groups,
            "namespace_pids_remaining": namespace_pids_remaining,
            "qdisc_state_removed": not qdisc_remaining,
            "qdisc_remaining": qdisc_remaining,
        }


def basic_throughput(lab: XBondLab, *, seconds: int = 4) -> dict[str, Any]:
    lab.restart_iperf_server()
    upload = iperf(seconds=seconds)
    lab.restart_iperf_server()
    download = iperf(reverse=True, seconds=seconds)
    return {"upload": upload, "download": download}


def native_path_throughput(
    lab: XBondLab, path: int = 1, *, seconds: int = 4
) -> dict[str, Any]:
    target = f"10.{10 + path}.0.2"
    source = f"10.{path}.0.2"
    port = 5300 + path
    results: dict[str, Any] = {}
    for reverse in (False, True):
        lab.start_iperf_server(bind_addr=target, port=port)
        command = [
            "iperf3",
            "-c",
            target,
            "-B",
            source,
            "-p",
            str(port),
            "-J",
            "-t",
            str(seconds),
            "-O",
            "1",
        ]
        if reverse:
            command.append("-R")
        completed = ns_run(
            CLIENT_NS, command, check=False, timeout=seconds + 20
        )
        item = parse_iperf_output(
            completed.stdout,
            completed.stderr,
            reverse=reverse,
            command=command,
            return_code=completed.returncode,
        )
        results["download" if reverse else "upload"] = item
    return results


def throughput_ratio(tunnel: dict[str, Any], native: dict[str, Any]) -> float:
    tunnel_floor = min(
        tunnel["upload"].get("mbps", 0.0),
        tunnel["download"].get("mbps", 0.0),
    )
    native_floor = min(
        native["upload"].get("mbps", 0.0),
        native["download"].get("mbps", 0.0),
    )
    return tunnel_floor / native_floor if native_floor > 0 else 0.0


def concurrent_bidirectional_throughput(
    lab: XBondLab,
    *,
    seconds: int = 12,
    parallel: int = 4,
    sample_interval: float = 0.5,
) -> tuple[dict[str, Any], list[dict[str, Any]], dict[str, Any]]:
    ports = {"upload": 5201, "download": 5202}
    for name, port in ports.items():
        lab.start_iperf_server(port=port, log_name=f"iperf-{name}-server")

    commands: dict[str, list[str]] = {}
    processes: dict[str, subprocess.Popen[str]] = {}
    for name, reverse in (("upload", False), ("download", True)):
        command = [
            "iperf3",
            "-c",
            SERVER_TUN_IP,
            "-p",
            str(ports[name]),
            "-J",
            "-t",
            str(seconds),
            "-O",
            "1",
            "-P",
            str(parallel),
        ]
        if reverse:
            command.append("-R")
        commands[name] = command
        process = subprocess.Popen(
            ns_cmd(CLIENT_NS, *command),
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            start_new_session=True,
        )
        lab.track_process(process)
        processes[name] = process

    ping_command = [
        "ping",
        "-n",
        "-i",
        "0.2",
        "-c",
        str(max(10, seconds * 5)),
        "-w",
        str(seconds + 5),
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    ping_process = subprocess.Popen(
        ns_cmd(CLIENT_NS, *ping_command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(ping_process)

    samples: list[dict[str, Any]] = []
    # Under the deliberate tiny-queue fault, TCP teardown can take much
    # longer than the requested data interval. Let iperf finish and emit valid
    # JSON instead of terminating a run that still carried measurable data.
    deadline = time.monotonic() + seconds + 45
    while any(process.poll() is None for process in processes.values()):
        samples.append(lab.sample_runtime())
        if time.monotonic() >= deadline:
            for process in processes.values():
                if process.poll() is None:
                    lab.stop_process(process)
            break
        time.sleep(sample_interval)

    outputs: dict[str, Any] = {}
    for name, process in processes.items():
        stdout, stderr = process.communicate(timeout=5)
        outputs[name] = parse_iperf_output(
            stdout,
            stderr,
            reverse=name == "download",
            command=commands[name],
            return_code=process.returncode,
        )
    if ping_process.poll() is None:
        with contextlib.suppress(subprocess.TimeoutExpired):
            ping_process.wait(timeout=5)
    if ping_process.poll() is None:
        lab.stop_process(ping_process, signal_number=signal.SIGINT)
    ping_stdout, ping_stderr = ping_process.communicate(timeout=5)
    ping_output = "\n".join(
        part for part in (ping_stdout, ping_stderr) if part
    )
    ping_result = parse_ping(ping_output, ping_command)
    ping_result.update(
        {
            "exit_code": ping_process.returncode,
            "target": SERVER_TUN_IP,
            "interface": "xbond0",
            "ran_concurrently": True,
        }
    )
    return outputs, samples, ping_result


def capture_outer_packets(
    lab: XBondLab,
    action: Callable[[], dict[str, Any]],
    *,
    seconds: int = 8,
) -> tuple[dict[str, Any], dict[str, Any]]:
    command = ns_cmd(
        CLIENT_NS,
        "timeout",
        str(seconds),
        "tcpdump",
        "-l",
        "-nn",
        "-vv",
        "-i",
        "cpath1",
        "host",
        SERVER_VIP,
    )
    capture = subprocess.Popen(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(capture)
    time.sleep(0.3)
    action_result = action()
    if capture.poll() is None:
        lab.stop_process(capture)
    stdout, stderr = capture.communicate(timeout=5)
    packet_lines = [
        line for line in stdout.splitlines() if re.match(r"^\s*\d{2}:\d{2}:", line)
    ]
    fragment_lines = [
        line
        for line in packet_lines
        if re.search(r"\bfrag\b|offset [1-9]|\[.*\+.*\]", line)
    ]
    return action_result, {
        "command": command,
        "captured_packets": len(packet_lines),
        "fragmented_packets": len(fragment_lines),
        "fragment_lines": fragment_lines[-50:],
        "stderr": stderr[-2000:],
    }


def scenario_topology_smoke(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    physical = [lab.physical_ping(index) for index in range(1, 4)]
    tunnel = lab.tunnel_ping(count=4)
    result.metrics.update(
        {
            "physical_paths": physical,
            "tunnel_ping": tunnel,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {"physical_loss_percent_max": 0, "tunnel_loss_percent_max": 0}
    result.status = (
        "pass"
        if all(item.get("loss_percent") == 0 for item in physical)
        and tunnel.get("loss_percent") == 0
        else "fail"
    )


def scenario_healthy_single(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.apply_netem(1, "rate 200mbit")
    native = native_path_throughput(lab)
    lab.start_runtime(path_count=1)
    tunnel = lab.tunnel_ping()
    throughput = basic_throughput(lab)
    ratio = throughput_ratio(throughput, native)
    runtime = lab.collect_runtime_metrics()
    process_metrics_present = all(
        isinstance(runtime["processes"][side].get("cpu_percent"), (int, float))
        and isinstance(runtime["processes"][side].get("rss_kib"), int)
        and runtime["processes"][side]["rss_kib"] > 0
        for side in ("client", "server")
    )
    result.metrics.update(
        {
            "native_path_throughput": native,
            "tunnel_ping": tunnel,
            "throughput": throughput,
            "tunnel_to_native_floor_ratio": ratio,
            "process_metrics_present": process_metrics_present,
            "runtime": runtime,
        }
    )
    result.thresholds = {
        "tunnel_loss_percent_max": 1,
        "tunnel_avg_rtt_ms_max": 100,
        "upload_mbps_min": 25,
        "download_mbps_min": 25,
        "tunnel_to_native_floor_ratio_min": 0.80,
        "process_cpu_and_rss_metrics_required": True,
    }
    result.status = (
        "pass"
        if tunnel.get("loss_percent", 100) <= 1
        and tunnel.get("avg_ms", 9999) <= 100
        and throughput["upload"].get("mbps", 0) >= 25
        and throughput["download"].get("mbps", 0) >= 25
        and ratio >= 0.80
        and process_metrics_present
        else "fail"
    )


def scenario_anchor_bad_backup(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.apply_netem(1, "rate 200mbit")
    stable_anchor_baseline = native_path_throughput(lab)
    lab.start_runtime()
    lab.apply_netem(2, "delay 250ms 80ms distribution normal loss 25%")
    lab.apply_netem(3, "delay 450ms 150ms distribution normal loss 45%")
    time.sleep(8)
    tunnel = lab.tunnel_ping(count=12)
    throughput = basic_throughput(lab)
    runtime = lab.collect_runtime_metrics()
    status = runtime["client_status"] or {}
    anchor_path_id = status.get("anchor_path_id")
    backup_paths = {
        path.get("path_id"): path for path in status.get("paths", [])
        if path.get("path_id") in (2, 3)
    }
    baseline_floor = min(
        stable_anchor_baseline["upload"].get("mbps", 0),
        stable_anchor_baseline["download"].get("mbps", 0),
    )
    impaired_floor = min(
        throughput["upload"].get("mbps", 0),
        throughput["download"].get("mbps", 0),
    )
    retained_ratio = impaired_floor / baseline_floor if baseline_floor > 0 else 0
    result.metrics.update(
        {
            "stable_anchor_native_baseline": stable_anchor_baseline,
            "tunnel_ping": tunnel,
            "throughput": throughput,
            "throughput_retained_ratio": retained_ratio,
            "anchor_path_id": anchor_path_id,
            "backup_paths": backup_paths,
            "qdisc": lab.qdisc_snapshot(),
            "runtime": runtime,
        }
    )
    result.thresholds = {
        "tunnel_loss_percent_max": 5,
        "tunnel_avg_rtt_ms_max": 180,
        "upload_mbps_min": 10,
        "download_mbps_min": 10,
        "throughput_retained_ratio_min": 0.90,
        "required_anchor_path_id": 1,
        "bad_backup_must_not_be_anchor": True,
    }
    result.status = (
        "pass"
        if tunnel.get("loss_percent", 100) <= 5
        and tunnel.get("avg_ms", 9999) <= 180
        and min(
            throughput["upload"].get("mbps", 0),
            throughput["download"].get("mbps", 0),
        )
        >= 10
        and retained_ratio >= 0.90
        and anchor_path_id == 1
        and all(
            path.get("role_reason") != "Selected as stable anchor by hysteresis scheduler."
            for path in backup_paths.values()
        )
        else "fail"
    )


def scenario_all_intermittent(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    baseline = basic_throughput(lab)
    retransmit_before = tcp_retransmits(CLIENT_NS)
    impairments = (
        "delay 90ms 70ms distribution normal loss 12% 40% reorder 8% 35%",
        "delay 140ms 100ms distribution normal loss 18% 45% reorder 12% 40%",
        "delay 220ms 160ms distribution normal loss 22% 50% reorder 15% 45%",
    )
    impairment_started = time.monotonic()
    for path, impairment in enumerate(impairments, start=1):
        lab.apply_netem(path, impairment)
    recovery_entry_seconds: float | None = None
    usable_tunnel_seconds: float | None = None
    recovery_observations: list[dict[str, Any]] = []
    next_probe_at = 0.0
    observation_deadline = impairment_started + 15
    while time.monotonic() < observation_deadline:
        now = time.monotonic()
        status = json_file(lab.client_status) or {}
        recovery_active = bool((status.get("recovery") or {}).get("active"))
        if recovery_active and recovery_entry_seconds is None:
            recovery_entry_seconds = now - impairment_started
        observation: dict[str, Any] = {
            "at_seconds": round(now - impairment_started, 3),
            "recovery_active": recovery_active,
        }
        if usable_tunnel_seconds is None and now >= next_probe_at:
            probe = ping(
                CLIENT_NS,
                SERVER_TUN_IP,
                count=1,
                interface="xbond0",
                deadline=2,
            )
            observation["tunnel_probe"] = probe
            if probe.get("received", 0) >= 1:
                usable_tunnel_seconds = time.monotonic() - impairment_started
            next_probe_at = time.monotonic() + 0.5
        recovery_observations.append(observation)
        if recovery_entry_seconds is not None and usable_tunnel_seconds is not None:
            break
        time.sleep(0.2)
    remaining_settle = 10 - (time.monotonic() - impairment_started)
    if remaining_settle > 0:
        time.sleep(remaining_settle)
    tunnel = lab.tunnel_ping(count=20)
    # High-delay recovery needs enough time to leave TCP slow start and measure
    # steady-state tunnel capacity instead of only the initial congestion window.
    throughput = basic_throughput(lab, seconds=20)
    runtime = lab.collect_runtime_metrics()
    recovery_active = bool(
        (runtime["client_status"] or {}).get("recovery", {}).get("active")
    )
    retransmit_delta = counter_delta(
        retransmit_before, tcp_retransmits(CLIENT_NS)
    )
    baseline_floor = min(
        baseline["upload"].get("mbps", 0),
        baseline["download"].get("mbps", 0),
    )
    impaired_floor = min(
        throughput["upload"].get("mbps", 0),
        throughput["download"].get("mbps", 0),
    )
    retained_ratio = impaired_floor / baseline_floor if baseline_floor > 0 else 0
    result.metrics.update(
        {
            "healthy_tunnel_baseline": baseline,
            "tunnel_ping": tunnel,
            "throughput": throughput,
            "throughput_retained_ratio": retained_ratio,
            "tcp_retransmit_delta": retransmit_delta,
            "recovery_active": recovery_active,
            "recovery_entry_seconds": recovery_entry_seconds,
            "usable_tunnel_seconds": usable_tunnel_seconds,
            "recovery_observations": recovery_observations,
            "qdisc": lab.qdisc_snapshot(),
            "runtime": runtime,
        }
    )
    result.thresholds = {
        "packet_survival_received_min": 18,
        "tunnel_loss_percent_max": 10,
        "upload_mbps_min": 1,
        "download_mbps_min": 1,
        "throughput_retained_ratio_min": 0.03,
        "tcp_retransmit_delta_max": 5000,
        "recovery_must_be_active": True,
        "recovery_entry_seconds_max": 10,
        "usable_tunnel_seconds_max": 10,
        "processes_must_remain_running": True,
    }
    runtime = result.metrics["runtime"]["processes"]
    result.status = (
        "pass"
        if tunnel.get("received", 0) >= 18
        and tunnel.get("loss_percent", 100) <= 10
        and throughput["upload"].get("mbps", 0) >= 1
        and throughput["download"].get("mbps", 0) >= 1
        and retained_ratio >= 0.03
        and retransmit_delta is not None
        and retransmit_delta <= 5000
        and recovery_active
        and recovery_entry_seconds is not None
        and recovery_entry_seconds <= 10
        and usable_tunnel_seconds is not None
        and usable_tunnel_seconds <= 10
        and runtime["client"].get("running")
        and runtime["server"].get("running")
        else "fail"
    )


def scenario_heavy_bidirectional(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    retransmit_before = {
        "client": tcp_retransmits(CLIENT_NS),
        "server": tcp_retransmits(SERVER_NS),
    }
    sample_before = lab.sample_runtime()
    throughput, samples, ping_during = concurrent_bidirectional_throughput(lab)
    sample_after = lab.sample_runtime()
    retransmit_after = {
        "client": tcp_retransmits(CLIENT_NS),
        "server": tcp_retransmits(SERVER_NS),
    }
    retransmit_delta = {
        side: counter_delta(retransmit_before[side], retransmit_after[side])
        for side in retransmit_before
    }
    status_progress = (
        sample_after["client_status_mtime_ns"] != sample_before["client_status_mtime_ns"]
        and sample_after["server_status_mtime_ns"] != sample_before["server_status_mtime_ns"]
        and sample_after["server_control_progress_at_micros"]
        > sample_before["server_control_progress_at_micros"]
    )
    peak_cpu = {
        "client": max(
            (
                sample["client_process"].get("cpu_percent", 0)
                for sample in samples
            ),
            default=0,
        ),
        "server": max(
            (
                sample["server_process"].get("cpu_percent", 0)
                for sample in samples
            ),
            default=0,
        ),
    }
    path_health_sample_count = sum(
        1 for sample in samples if sample.get("path_health")
    )
    path_health_observation_ratio = (
        path_health_sample_count / len(samples) if samples else 0.0
    )
    path_health_observed = path_health_observation_ratio >= 0.8
    scheduled_path_health_preserved = path_health_observed and all(
        any(
            path.get("path_id") in sample.get("scheduled_path_ids", [])
            and path.get("interface_up") is True
            and not path.get("in_cooldown", False)
            and isinstance(path.get("loss_rate"), (int, float))
            and path["loss_rate"] < 1.0
            and isinstance(path.get("stale_ack_ms"), (int, float))
            and path["stale_ack_ms"] < 5000
            for path in sample["path_health"]
        )
        for sample in samples
        if sample.get("path_health") and sample.get("scheduled_path_ids")
    )
    result.metrics.update(
        {
            "ping_during_load": ping_during,
            "throughput": throughput,
            "tcp_retransmits_before": retransmit_before,
            "tcp_retransmits_after": retransmit_after,
            "tcp_retransmit_delta": retransmit_delta,
            "runtime_samples": samples,
            "status_and_control_progressed": status_progress,
            "path_health_observed_during_load": path_health_observed,
            "path_health_observation_ratio": path_health_observation_ratio,
            "scheduled_path_health_preserved_during_load": scheduled_path_health_preserved,
            "peak_cpu_percent": peak_cpu,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "ping_received_min": 10,
        "upload_mbps_min": 15,
        "download_mbps_min": 15,
        "status_and_control_must_progress": True,
        "path_health_observation_ratio_min": 0.8,
        "at_least_one_scheduled_path_must_remain_healthy_per_sample": True,
        "tunnel_loss_percent_max": 20,
        "processes_must_remain_running": True,
    }
    processes = result.metrics["runtime"]["processes"]
    result.status = (
        "pass"
        if ping_during.get("received", 0) >= 10
        and ping_during.get("loss_percent", 100) <= 20
        and throughput["upload"].get("mbps", 0) >= 15
        and throughput["download"].get("mbps", 0) >= 15
        and status_progress
        and path_health_observed
        and scheduled_path_health_preserved
        and processes["client"].get("running")
        and processes["server"].get("running")
        else "fail"
    )


def scenario_silent_blackhole(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    lab.restart_client_supervised(log_name="client-blackhole-supervisor")
    supervisor_pid_before = lab.client.pid if lab.client else None
    worker_pids_before = namespace_process_pids(CLIENT_NS, "xbond-client")
    before = json_file(lab.client_status) or {}
    session_before = session_evidence(
        log_tail(lab.scenario, "client-blackhole-supervisor", limit=200000)
    ).get("session_id")
    before_paths = {path.get("path_id"): path for path in before.get("paths", [])}
    before_generation = before_paths.get(2, {}).get("socket_generation", 0)
    router = ROUTER_NAMES[1]
    ns_run(
        router,
        ["iptables", "-I", "FORWARD", "-p", "udp", "--dport", str(SERVER_PORT), "-j", "DROP"],
    )
    ns_run(
        router,
        ["iptables", "-I", "FORWARD", "-p", "udp", "--sport", str(SERVER_PORT), "-j", "DROP"],
    )
    outside_ping = lab.physical_ping(2, count=4)
    time.sleep(22)
    after = json_file(lab.client_status) or {}
    after_paths = {path.get("path_id"): path for path in after.get("paths", [])}
    path = after_paths.get(2, {})
    worker_pids_after_first_rebind = namespace_process_pids(
        CLIENT_NS, "xbond-client"
    )
    first_rebind_succeeded = (
        outside_ping.get("loss_percent") == 0
        and (path.get("socket_generation") or 0) > before_generation
        and supervisor_pid_before == (lab.client.pid if lab.client else None)
        and worker_pids_before == worker_pids_after_first_rebind
        and lab.client_supervisor_restart_count("client-blackhole-supervisor")
        == 0
        and path.get("last_rebind_reason")
        == "silent-udp-blackhole-direct-probe-ok"
    )

    # Keep the confirmed blackhole in place. A persistently bad optional path
    # must be hard-demoted after bounded hot-rebind attempts without restarting
    # the healthy aggregate tunnel, client process, or authenticated session.
    escalation_started = time.monotonic()
    escalation_deadline = escalation_started + 90
    persistent_path_demoted = False
    demoted_path = {}
    while time.monotonic() < escalation_deadline:
        current = json_file(lab.client_status) or {}
        current_paths = {
            item.get("path_id"): item for item in current.get("paths", [])
        }
        demoted_path = current_paths.get(2, {})
        persistent_path_demoted = (
            (demoted_path.get("socket_generation") or 0)
            > (path.get("socket_generation") or 0)
            and demoted_path.get("last_rebind_reason")
            == "persistent-silent-udp-blackhole-path-demoted"
            and (demoted_path.get("send_failure_streak") or 0) >= 2
        )
        if persistent_path_demoted:
            break
        time.sleep(0.5)
    escalation_seconds = round(time.monotonic() - escalation_started, 3)
    for namespace in ROUTER_NAMES:
        ns_run(namespace, ["iptables", "-F"], check=False)
    path_recovered_after_remote_ack = False
    recovered_path = {}
    recovery_deadline = time.monotonic() + 30
    while time.monotonic() < recovery_deadline:
        current = json_file(lab.client_status) or {}
        current_paths = {
            item.get("path_id"): item for item in current.get("paths", [])
        }
        recovered_path = current_paths.get(2, {})
        path_recovered_after_remote_ack = (
            persistent_path_demoted
            and (recovered_path.get("send_failure_streak") or 0) == 0
            and not recovered_path.get("in_cooldown", False)
            and recovered_path.get("demotion_reason") is None
            and (recovered_path.get("stale_ack_ms") or 1_000_000) < 5_000
            and (recovered_path.get("loss_rate") or 1.0) < 1.0
        )
        if path_recovered_after_remote_ack:
            break
        time.sleep(0.25)
    recovered_ping = lab.tunnel_ping(count=8)
    worker_pids_after_recovery = namespace_process_pids(
        CLIENT_NS, "xbond-client"
    )
    supervisor_log = log_tail(
        lab.scenario, "client-blackhole-supervisor", limit=200000
    )
    session_after = session_evidence(supervisor_log).get("session_id")
    exit_codes = [
        int(value)
        for value in re.findall(
            r"LAB_SUPERVISOR_EXIT count=\d+ exit_code=(\d+)",
            supervisor_log,
        )
    ]
    result.metrics.update(
        {
            "outside_tunnel_path_ping": outside_ping,
            "socket_generation_before": before_generation,
            "socket_generation_after": path.get("socket_generation"),
            "rebind_count": path.get("rebind_count"),
            "last_rebind_reason": path.get("last_rebind_reason"),
            "supervisor_pid_before": supervisor_pid_before,
            "supervisor_pid_after": lab.client.pid if lab.client else None,
            "worker_pids_before": worker_pids_before,
            "worker_pids_after_first_rebind": worker_pids_after_first_rebind,
            "worker_pids_after_recovery": worker_pids_after_recovery,
            "supervisor_restart_count": lab.client_supervisor_restart_count(
                "client-blackhole-supervisor"
            ),
            "supervisor_exit_codes": exit_codes,
            "persistent_path_demoted": persistent_path_demoted,
            "demoted_path": demoted_path,
            "path_recovered_after_remote_ack": path_recovered_after_remote_ack,
            "recovered_path": recovered_path,
            "escalation_seconds": escalation_seconds,
            "session_id_before": session_before,
            "session_id_after_path_recovery": session_after,
            "tunnel_ping_after_path_recovery": recovered_ping,
            "client_supervisor_log_tail": supervisor_log[-20000:],
            "server_log_tail": log_tail(lab.scenario, "server", limit=16000),
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "outside_ping_loss_percent_max": 0,
        "socket_generation_must_increase": True,
        "worker_pid_must_not_change_for_first_rebind": True,
        "supervisor_pid_must_not_change": True,
        "required_rebind_reason": "silent-udp-blackhole-direct-probe-ok",
        "persistent_path_must_be_hard_demoted": True,
        "client_supervisor_restart_count_max": 0,
        "worker_pid_must_not_change": True,
        "session_id_must_not_change": True,
        "path_must_recover_after_real_remote_ack": True,
        "escalation_seconds_max": 90,
        "post_recovery_tunnel_loss_percent_max": 5,
    }
    result.status = (
        "pass"
        if first_rebind_succeeded
        and persistent_path_demoted
        and path_recovered_after_remote_ack
        and not exit_codes
        and lab.client_supervisor_restart_count("client-blackhole-supervisor") == 0
        and supervisor_pid_before == (lab.client.pid if lab.client else None)
        and worker_pids_after_recovery == worker_pids_before
        and session_after == session_before
        and recovered_ping.get("loss_percent", 100) <= 5
        else "fail"
    )


def recreate_client_path(lab: XBondLab, path: int) -> None:
    router = ROUTER_NAMES[path - 1]
    client_if = f"cpath{path}"
    router_if = f"r{path}c"
    client_ip = f"10.{path}.0.2"
    gateway = f"10.{path}.0.1"
    table = str(100 + path)
    ns_run(CLIENT_NS, ["ip", "link", "del", client_if], check=False)
    time.sleep(1)
    run(["ip", "link", "add", client_if, "type", "veth", "peer", "name", router_if])
    run(["ip", "link", "set", client_if, "netns", CLIENT_NS])
    run(["ip", "link", "set", router_if, "netns", router])
    ns_run(CLIENT_NS, ["ip", "addr", "add", f"{client_ip}/24", "dev", client_if])
    ns_run(CLIENT_NS, ["ip", "link", "set", client_if, "up"])
    ns_run(router, ["ip", "addr", "add", f"{gateway}/24", "dev", router_if])
    ns_run(router, ["ip", "link", "set", router_if, "up"])
    ns_run(
        CLIENT_NS,
        ["ip", "route", "replace", f"{SERVER_VIP}/32", "via", gateway, "dev", client_if, "table", table],
    )
    ns_run(
        CLIENT_NS,
        [
            "ip",
            "route",
            "replace",
            f"10.{10 + path}.0.2/32",
            "via",
            gateway,
            "dev",
            client_if,
            "table",
            table,
        ],
    )
    ns_run(
        CLIENT_NS,
        ["ip", "route", "replace", f"10.{10 + path}.0.2/32", "via", gateway, "dev", client_if],
    )


def scenario_usb_reenumeration(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    before = json_file(lab.client_status) or {}
    before_path = next((path for path in before.get("paths", []) if path.get("path_id") == 2), {})
    recreate_client_path(lab, 2)
    time.sleep(15)
    after = json_file(lab.client_status) or {}
    after_path = next((path for path in after.get("paths", []) if path.get("path_id") == 2), {})
    physical = lab.physical_ping(2, count=4)
    tunnel = lab.tunnel_ping(count=6)
    result.metrics.update(
        {
            "ifindex_before": before_path.get("socket_ifindex"),
            "ifindex_after": after_path.get("socket_ifindex"),
            "socket_generation_before": before_path.get("socket_generation"),
            "socket_generation_after": after_path.get("socket_generation"),
            "rebind_count": after_path.get("rebind_count"),
            "last_rebind_reason": after_path.get("last_rebind_reason"),
            "path_2_loss_rate_after": after_path.get("loss_rate"),
            "path_2_stale_ack_ms_after": after_path.get("stale_ack_ms"),
            "physical_ping_after": physical,
            "tunnel_ping_after": tunnel,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "socket_generation_must_increase": True,
        "ifindex_must_change": True,
        "path_2_loss_rate_max": 0.25,
        "physical_loss_percent_max": 0,
        "tunnel_loss_percent_max": 5,
    }
    result.status = (
        "pass"
        if (after_path.get("socket_generation") or 0)
        > (before_path.get("socket_generation") or 0)
        and after_path.get("socket_ifindex") is not None
        and after_path.get("socket_ifindex") != before_path.get("socket_ifindex")
        and (after_path.get("loss_rate") or 0) <= 0.25
        and physical.get("loss_percent") == 0
        and tunnel.get("loss_percent", 100) <= 5
        else "fail"
    )


def log_tail(scenario: str, process_name: str, limit: int = 8000) -> str:
    path = LOG_ROOT / f"{scenario}-{process_name}.log"
    try:
        return path.read_text(encoding="utf-8")[-limit:]
    except OSError:
        return ""


def scenario_tun_read_failure(lab: XBondLab, result: LabResult, _: int) -> None:
    flag = "--lab-fail-tun-read-after-packets"

    lab.setup_topology()
    fail_after_packets = 32
    lab.start_runtime(client_extra_args=[flag, str(fail_after_packets)])
    generator = ns_run(
        CLIENT_NS,
        ["ping", "-n", "-f", "-c", "128", SERVER_TUN_IP],
        check=False,
        timeout=20,
    )
    detection_started = time.monotonic()
    wait_for(
        lambda: lab.client is not None and lab.client.poll() is not None,
        20,
        "client to surface the injected TUN read failure",
    )
    return_code = lab.client.poll() if lab.client else None
    detection_seconds = time.monotonic() - detection_started
    client_log = log_tail(lab.scenario, "client")
    fault_marker = (
        "stopped by lab failure injection after 32 packets" in client_log
    )
    result.metrics.update(
        {
            "fault_flag": flag,
            "fail_after_packets": fail_after_packets,
            "traffic_generator_exit_code": generator.returncode,
            "client_return_code": return_code,
            "failure_detection_seconds": detection_seconds,
            "fault_marker_found": fault_marker,
            "client_log_tail": client_log,
            "server_process": process_metrics(lab.server),
        }
    )
    result.thresholds = {
        "client_must_exit_nonzero": True,
        "server_must_remain_running": True,
        "maximum_detection_seconds": 20,
    }
    result.status = (
        "pass"
        if return_code not in (None, 0)
        and fault_marker
        and detection_seconds <= 20
        and result.metrics["server_process"].get("running")
        else "fail"
    )


def scenario_server_process_restart(
    lab: XBondLab, result: LabResult, _: int
) -> None:
    lab.setup_topology()
    lab.start_runtime()
    baseline_ping = lab.tunnel_ping(count=4)
    client_pid_before = lab.client.pid if lab.client else None
    server_pid_before = lab.server.pid if lab.server else None
    session_before = session_evidence(
        log_tail(lab.scenario, "client", limit=200000)
    )

    continuous_command = [
        "ping",
        "-n",
        "-i",
        "0.1",
        "-c",
        "250",
        "-w",
        "30",
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    continuous_ping = subprocess.Popen(
        ns_cmd(CLIENT_NS, *continuous_command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(continuous_ping)
    time.sleep(0.5)

    restart_started = time.monotonic()
    previous_server_return_code = lab.restart_server()
    server_pid_after = lab.server.pid if lab.server else None
    observations: list[dict[str, Any]] = []
    recovered = False
    recovery_seconds: float | None = None
    recovery_deadline = time.monotonic() + 20
    while time.monotonic() < recovery_deadline:
        server_status = json_file(lab.server_status) or {}
        session_after = session_evidence(
            log_tail(lab.scenario, "client", limit=200000)
        )
        session_ids = session_after.get("session_ids") or []
        session_changed = bool(
            session_before.get("session_id") is not None
            and session_ids
            and session_ids[-1] != session_before.get("session_id")
        )
        sample = {
            "at_seconds": round(time.monotonic() - restart_started, 3),
            "server_schedule_required": server_status.get("schedule_required"),
            "client_session_ids": session_ids,
            "client_running": bool(lab.client and lab.client.poll() is None),
            "server_running": bool(lab.server and lab.server.poll() is None),
        }
        observations.append(sample)
        if (
            session_changed
            and server_status.get("schedule_required") is False
            and sample["client_running"]
            and sample["server_running"]
        ):
            recovered = True
            recovery_seconds = time.monotonic() - restart_started
            break
        time.sleep(0.25)

    time.sleep(1 if recovered else 0)
    if continuous_ping.poll() is None:
        lab.stop_process(
            continuous_ping, signal_number=signal.SIGINT, timeout=3
        )
    continuous_stdout, continuous_stderr = continuous_ping.communicate(timeout=5)
    continuous_output = "\n".join(
        part for part in (continuous_stdout, continuous_stderr) if part
    )
    continuous_result = parse_ping(continuous_output, continuous_command)
    continuous_result.update(
        {
            "exit_code": continuous_ping.returncode,
            "target": SERVER_TUN_IP,
            "interface": "xbond0",
        }
    )

    post_restart_ping = ping(
        CLIENT_NS,
        SERVER_TUN_IP,
        count=6,
        interface="xbond0",
        deadline=10,
    )
    restored_throughput: dict[str, Any] = {}
    if recovered and post_restart_ping.get("loss_percent", 100) <= 10:
        restored_throughput = basic_throughput(lab, seconds=3)

    session_after = session_evidence(
        log_tail(lab.scenario, "client", limit=200000)
    )
    client_pid_after = lab.client.pid if lab.client else None
    session_id_changed = bool(
        session_before.get("session_id") is not None
        and session_after.get("session_id") is not None
        and session_after["session_id"] != session_before["session_id"]
    )
    client_pid_unchanged = bool(
        client_pid_before is not None
        and client_pid_after == client_pid_before
        and lab.client
        and lab.client.poll() is None
    )
    server_pid_changed = bool(
        server_pid_before is not None
        and server_pid_after is not None
        and server_pid_after != server_pid_before
    )

    result.metrics.update(
        {
            "baseline_ping": baseline_ping,
            "continuous_ping_during_restart": continuous_result,
            "post_restart_ping": post_restart_ping,
            "restored_throughput": restored_throughput,
            "recovery_observations": observations,
            "recovery_seconds": recovery_seconds,
            "automatic_recovery_observed": recovered,
            "previous_server_return_code": previous_server_return_code,
            "previous_server_stopped": previous_server_return_code is not None,
            "client_pid_before": client_pid_before,
            "client_pid_after": client_pid_after,
            "client_pid_unchanged": client_pid_unchanged,
            "server_pid_before": server_pid_before,
            "server_pid_after": server_pid_after,
            "server_pid_changed": server_pid_changed,
            "session_before": session_before,
            "session_after": session_after,
            "session_id_changed": session_id_changed,
            "client_log_tail": log_tail(lab.scenario, "client", limit=20000),
            "restarted_server_log_tail": log_tail(
                lab.scenario, "server-restart", limit=20000
            ),
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "baseline_tunnel_loss_percent_max": 0,
        "automatic_recovery_seconds_max": 20,
        "client_pid_must_remain_unchanged": True,
        "server_pid_must_change": True,
        "authenticated_session_id_must_change": True,
        "post_restart_tunnel_loss_percent_max": 10,
        "restored_upload_mbps_min": 1,
        "restored_download_mbps_min": 1,
    }
    result.status = (
        "pass"
        if baseline_ping.get("loss_percent", 100) == 0
        and recovered
        and previous_server_return_code is not None
        and recovery_seconds is not None
        and recovery_seconds <= 20
        and client_pid_unchanged
        and server_pid_changed
        and session_id_changed
        and post_restart_ping.get("loss_percent", 100) <= 10
        and restored_throughput.get("upload", {}).get("mbps", 0) >= 1
        and restored_throughput.get("download", {}).get("mbps", 0) >= 1
        else "fail"
    )


def scenario_tun_write_backpressure(lab: XBondLab, result: LabResult, _: int) -> None:
    flag = "--lab-tun-write-delay-ms"

    lab.setup_topology()
    delay_ms = 40
    lab.start_runtime(
        queue_capacity=8,
        inbound_capacity=8,
        client_extra_args=[flag, str(delay_ms)],
    )
    sample_before = lab.sample_runtime()
    command = [
        "ping",
        "-n",
        "-c",
        "16",
        "-i",
        "0.01",
        "-s",
        "1200",
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    load = subprocess.Popen(
        ns_cmd(CLIENT_NS, *command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(load)
    concurrent_ping_command = [
        "ping",
        "-n",
        "-c",
        "40",
        "-i",
        "0.1",
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    concurrent_ping = subprocess.Popen(
        ns_cmd(CLIENT_NS, *concurrent_ping_command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(concurrent_ping)
    samples: list[dict[str, Any]] = []
    observation_deadline = time.monotonic() + 4
    while time.monotonic() < observation_deadline:
        sample = lab.sample_runtime()
        sample["at_seconds"] = round(
            time.monotonic() - result.monotonic_start, 3
        )
        samples.append(sample)
        time.sleep(0.1)
    timed_out = load.poll() is None
    if timed_out:
        lab.stop_process(load)
    stdout, stderr = load.communicate(timeout=5)
    if concurrent_ping.poll() is None:
        lab.stop_process(
            concurrent_ping, signal_number=signal.SIGINT, timeout=3
        )
    ping_stdout, ping_stderr = concurrent_ping.communicate(timeout=5)
    concurrent_ping_result = parse_ping(
        "\n".join(part for part in (ping_stdout, ping_stderr) if part),
        concurrent_ping_command,
    )
    concurrent_ping_result["exit_code"] = concurrent_ping.returncode
    sample_after = lab.sample_runtime()
    ping_after = lab.tunnel_ping(count=6)

    flattened_samples = [sample["client_telemetry"] for sample in samples]
    queue_depths = [
        int(value)
        for sample in flattened_samples
        for key, value in sample.items()
        if key == "process.tun_write_queue_depth"
        and isinstance(value, (int, float))
    ]
    queue_peaks = [
        int(value)
        for sample in flattened_samples
        for key, value in sample.items()
        if key == "process.tun_write_queue_peak_depth"
        and isinstance(value, (int, float))
    ]
    peak_depth = max(queue_depths, default=0)
    peak_recorded_depth = max(queue_peaks, default=0)
    before_telemetry = sample_before["client_telemetry"]
    after_telemetry = sample_after["client_telemetry"]
    tun_write_packets_delta = max(
        0,
        int(after_telemetry.get("process.tun_write_packets", 0))
        - int(before_telemetry.get("process.tun_write_packets", 0)),
    )
    tun_write_queue_micros_delta = max(
        0,
        int(after_telemetry.get("process.tun_write_queue_micros_total", 0))
        - int(before_telemetry.get("process.tun_write_queue_micros_total", 0)),
    )
    client_and_server_alive_during_load = bool(samples) and all(
        sample["client_process"].get("running")
        and sample["server_process"].get("running")
        for sample in samples
    )
    client_status_versions = [
        sample["client_status_mtime_ns"]
        for sample in samples
        if sample["client_status_mtime_ns"] is not None
    ]
    server_status_versions = [
        sample["server_status_mtime_ns"]
        for sample in samples
        if sample["server_status_mtime_ns"] is not None
    ]
    control_progress_values = [
        sample["server_control_progress_at_micros"]
        for sample in samples
        if sample["server_control_progress_at_micros"] > 0
    ]
    heartbeat_ages = [
        sample["tunnel_last_success_age_ms"] for sample in samples
    ]
    heartbeat_success_rates = [
        sample["tunnel_success_rate"] for sample in samples
    ]
    heartbeat_progressed = (
        sum(1 for value in heartbeat_success_rates if value > 0) >= 2
        and max(heartbeat_ages, default=float("inf")) <= 3000
    )
    status_and_control_progressed = (
        len(set(client_status_versions)) >= 2
        and len(set(server_status_versions)) >= 2
        and len(control_progress_values) >= 2
        and max(control_progress_values) > min(control_progress_values)
        and sample_after["client_status_mtime_ns"]
        != sample_before["client_status_mtime_ns"]
        and sample_after["server_status_mtime_ns"]
        != sample_before["server_status_mtime_ns"]
        and sample_after["server_control_progress_at_micros"]
        > sample_before["server_control_progress_at_micros"]
    )
    result.metrics.update(
        {
            "fault_flag": flag,
            "tun_write_delay_ms": delay_ms,
            "load_command": command,
            "load_exit_code": load.returncode,
            "load_outcome": "timeout-under-induced-backpressure"
            if timed_out
            else "completed",
            "load_error": stderr[-4000:],
            "load_output_tail": stdout[-4000:],
            "client_log_tail": log_tail(lab.scenario, "client", limit=12000),
            "server_log_tail": log_tail(lab.scenario, "server", limit=12000),
            "peak_tun_write_queue_depth_sampled": peak_depth,
            "peak_tun_write_queue_depth_recorded": peak_recorded_depth,
            "tun_write_packets_delta": tun_write_packets_delta,
            "tun_write_queue_micros_delta": tun_write_queue_micros_delta,
            "samples": samples,
            "concurrent_tunnel_ping": concurrent_ping_result,
            "ping_after": ping_after,
            "client_and_server_alive_during_load": client_and_server_alive_during_load,
            "status_and_control_progressed_during_load": status_and_control_progressed,
            "tunnel_heartbeat_progressed_during_load": heartbeat_progressed,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "queue_pressure_must_be_observed": True,
        "tun_write_packets_delta_min": 1,
        "tun_write_queue_micros_delta_min": 1,
        "client_and_server_must_remain_running_throughout": True,
        "status_and_control_must_progress_during_load": True,
        "tunnel_heartbeat_success_rate_must_remain_positive": True,
        "tunnel_heartbeat_last_success_age_ms_max": 3000,
        "concurrent_tunnel_ping_received_min": 10,
        "concurrent_tunnel_ping_loss_percent_max": 25,
        "post_pressure_tunnel_loss_percent_max": 10,
        "configured_write_delay_ms": delay_ms,
    }
    processes = result.metrics["runtime"]["processes"]
    result.status = (
        "pass"
        if peak_recorded_depth > 0
        and tun_write_packets_delta >= 1
        and tun_write_queue_micros_delta >= 1
        and client_and_server_alive_during_load
        and status_and_control_progressed
        and heartbeat_progressed
        and concurrent_ping_result.get("received", 0) >= 10
        and concurrent_ping_result.get("loss_percent", 100) <= 25
        and processes["client"].get("running")
        and processes["server"].get("running")
        and ping_after.get("loss_percent", 100) <= 10
        else "fail"
    )


def scenario_server_tun_write_backpressure(
    lab: XBondLab, result: LabResult, _: int
) -> None:
    fault_flag = "--fault-tun-write-delay-ms"
    responsive_delay_ms = 40
    responsive_queue_capacity = 8

    lab.setup_topology()
    lab.start_runtime(
        queue_capacity=responsive_queue_capacity,
        inbound_capacity=32,
        server_extra_args=[fault_flag, str(responsive_delay_ms)],
        client_log_name="client-responsive-pressure",
        server_log_name="server-responsive-pressure",
        direct_probe_log_name="direct-probe-responsive-pressure",
    )
    responsive_before = lab.sample_runtime()
    load_command = [
        "ping",
        "-n",
        "-c",
        "12",
        "-l",
        "4",
        "-i",
        "0.02",
        "-s",
        "1200",
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    moderate_load = subprocess.Popen(
        ns_cmd(CLIENT_NS, *load_command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(moderate_load)
    responsive_samples: list[dict[str, Any]] = []
    observation_deadline = time.monotonic() + 6
    while time.monotonic() < observation_deadline:
        sample = lab.sample_runtime()
        sample["at_seconds"] = round(
            time.monotonic() - result.monotonic_start, 3
        )
        responsive_samples.append(sample)
        if lab.server is None or lab.server.poll() is not None:
            break
        time.sleep(0.1)

    if moderate_load.poll() is None:
        lab.stop_process(
            moderate_load, signal_number=signal.SIGINT, timeout=3
        )
    moderate_stdout, moderate_stderr = moderate_load.communicate(timeout=5)
    concurrent_ping_result = parse_ping(
        "\n".join(
            part for part in (moderate_stdout, moderate_stderr) if part
        ),
        load_command,
    )
    concurrent_ping_result["exit_code"] = moderate_load.returncode
    responsive_after = lab.sample_runtime()

    server_telemetry_samples = [
        sample["server_telemetry"] for sample in responsive_samples
    ]
    queue_depths = [
        int(value)
        for sample in server_telemetry_samples
        for key, value in sample.items()
        if (
            key.endswith("tun_write_queue_depth")
            or key.endswith("tun_write_queue_peak_depth")
        )
        and isinstance(value, (int, float))
    ]
    saturation_failures = [
        int(value)
        for sample in server_telemetry_samples
        for key, value in sample.items()
        if key.endswith("tun_write_queue_saturation_failures")
        and isinstance(value, (int, float))
    ]
    control_progress_values = [
        sample["server_control_progress_at_micros"]
        for sample in responsive_samples
        if sample["server_control_progress_at_micros"] > 0
    ]
    client_status_versions = [
        sample["client_status_mtime_ns"]
        for sample in responsive_samples
        if sample["client_status_mtime_ns"] is not None
    ]
    server_status_versions = [
        sample["server_status_mtime_ns"]
        for sample in responsive_samples
        if sample["server_status_mtime_ns"] is not None
    ]
    heartbeat_ages = [
        sample["tunnel_last_success_age_ms"]
        for sample in responsive_samples
    ]
    successful_heartbeat_samples = sum(
        1 for sample in responsive_samples if sample["tunnel_success_rate"] > 0
    )
    peak_queue_depth = max(queue_depths, default=0)
    responsive_processes_alive = bool(responsive_samples) and all(
        sample["client_process"].get("running")
        and sample["server_process"].get("running")
        for sample in responsive_samples
    )
    control_cadence_responsive = (
        len(set(client_status_versions)) >= 2
        and len(set(server_status_versions)) >= 2
        and len(control_progress_values) >= 2
        and max(control_progress_values) > min(control_progress_values)
        and successful_heartbeat_samples >= 2
        and max(heartbeat_ages, default=0) <= 3000
        and responsive_after["client_status_mtime_ns"]
        != responsive_before["client_status_mtime_ns"]
        and responsive_after["server_status_mtime_ns"]
        != responsive_before["server_status_mtime_ns"]
    )
    responsive_phase = {
        "fault_flag": fault_flag,
        "tun_write_delay_ms": responsive_delay_ms,
        "tun_queue_capacity": responsive_queue_capacity,
        "load_command": load_command,
        "load_exit_code": moderate_load.returncode,
        "load_output_tail": moderate_stdout[-4000:],
        "load_error_tail": moderate_stderr[-4000:],
        "concurrent_tunnel_ping": concurrent_ping_result,
        "samples": responsive_samples,
        "peak_tun_write_queue_depth": peak_queue_depth,
        "peak_tun_write_queue_saturation_failures": max(
            saturation_failures, default=0
        ),
        "successful_heartbeat_samples": successful_heartbeat_samples,
        "queue_remained_bounded": peak_queue_depth
        <= responsive_queue_capacity,
        "client_and_server_alive_during_pressure": responsive_processes_alive,
        "status_control_and_heartbeat_progressed": control_cadence_responsive,
        "client_log_tail": log_tail(
            lab.scenario, "client-responsive-pressure", limit=12000
        ),
        "server_log_tail": log_tail(
            lab.scenario, "server-responsive-pressure", limit=12000
        ),
    }

    lab.stop_runtime()
    lab.setup_topology()

    severe_delay_ms = 500
    severe_queue_capacity = 2
    lab.start_runtime(
        queue_capacity=severe_queue_capacity,
        inbound_capacity=64,
        server_extra_args=[fault_flag, str(severe_delay_ms)],
        client_log_name="client-severe-pressure",
        server_log_name="server-severe-pressure",
        direct_probe_log_name="direct-probe-severe-pressure",
    )
    severe_load_command = [
        "ping",
        "-n",
        "-f",
        "-c",
        "128",
        "-s",
        "1200",
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    severe_load = subprocess.Popen(
        ns_cmd(CLIENT_NS, *severe_load_command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(severe_load)
    severe_started = time.monotonic()
    fail_closed_deadline = severe_started + 5
    while (
        lab.server is not None
        and lab.server.poll() is None
        and time.monotonic() < fail_closed_deadline
    ):
        time.sleep(0.05)
    fail_closed_seconds = time.monotonic() - severe_started
    server_exit_code = lab.server.poll() if lab.server else None
    if severe_load.poll() is None:
        lab.stop_process(severe_load)
    severe_stdout, severe_stderr = severe_load.communicate(timeout=5)
    severe_server_log = log_tail(
        lab.scenario, "server-severe-pressure", limit=20000
    )
    explicit_saturation_failure = (
        "TUN writer remained saturated" in severe_server_log
        and "terminating the tunnel for a clean restart" in severe_server_log
    )
    severe_phase = {
        "tun_write_delay_ms": severe_delay_ms,
        "tun_queue_capacity": severe_queue_capacity,
        "load_command": severe_load_command,
        "load_exit_code": severe_load.returncode,
        "load_output_tail": severe_stdout[-4000:],
        "load_error_tail": severe_stderr[-4000:],
        "server_exit_code": server_exit_code,
        "fail_closed_seconds": fail_closed_seconds,
        "explicit_saturation_failure": explicit_saturation_failure,
        "client_remained_running": bool(
            lab.client and lab.client.poll() is None
        ),
        "client_log_tail": log_tail(
            lab.scenario, "client-severe-pressure", limit=12000
        ),
        "server_log_tail": severe_server_log,
    }

    result.metrics.update(
        {
            "responsive_pressure": responsive_phase,
            "severe_pressure": severe_phase,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "responsive_queue_pressure_must_be_observed": True,
        "responsive_queue_depth_must_not_exceed_capacity": True,
        "status_control_and_heartbeat_must_progress": True,
        "concurrent_tunnel_ping_received_min": 6,
        "concurrent_tunnel_ping_loss_percent_max": 25,
        "client_and_server_must_remain_running_during_responsive_phase": True,
        "severe_pressure_must_fail_closed": True,
        "severe_pressure_fail_closed_seconds_max": 5,
        "severe_pressure_must_not_stop_client": True,
    }
    result.status = (
        "pass"
        if peak_queue_depth > 0
        and responsive_phase["queue_remained_bounded"]
        and responsive_processes_alive
        and control_cadence_responsive
        and concurrent_ping_result.get("received", 0) >= 6
        and concurrent_ping_result.get("loss_percent", 100) <= 25
        and server_exit_code not in (None, 0)
        and fail_closed_seconds <= 5
        and explicit_saturation_failure
        and severe_phase["client_remained_running"]
        else "fail"
    )


def faketime_environment(offset: str) -> dict[str, str]:
    library = "/usr/lib/x86_64-linux-gnu/faketime/libfaketimeMT.so.1"
    return {"LD_PRELOAD": library, "FAKETIME": offset, "FAKETIME_DONT_FAKE_MONOTONIC": "1"}


def scenario_clock_skew(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    fake_env = faketime_environment("-2h")
    normal_epoch = int(
        ns_run(CLIENT_NS, ["date", "+%s"]).stdout.strip()
    )
    fake_epoch = int(
        ns_run(CLIENT_NS, ["date", "+%s"], env={**os.environ, **fake_env}).stdout.strip()
    )
    lab.start_runtime(client_env=fake_env)
    tunnel = lab.tunnel_ping(count=6)
    evidence = session_evidence(log_tail(lab.scenario, "client", limit=65536))
    result.metrics.update(
        {
            "client_clock_offset": "-2h",
            "measured_clock_offset_seconds": fake_epoch - normal_epoch,
            "session_evidence": evidence,
            "tunnel_ping": tunnel,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "measured_clock_offset_seconds_max": -7000,
        "session_and_schedule_must_be_accepted": True,
        "tunnel_loss_percent_max": 0,
    }
    result.status = (
        "pass"
        if fake_epoch - normal_epoch <= -7000
        and evidence["session_accepted"]
        and evidence["schedule_accepted"]
        and tunnel.get("loss_percent") == 0
        else "fail"
    )


def scenario_clock_rollback(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    initial = lab.tunnel_ping(count=4)
    initial_evidence = session_evidence(
        log_tail(lab.scenario, "client", limit=65536)
    )
    normal_epoch = int(ns_run(CLIENT_NS, ["date", "+%s"]).stdout.strip())
    fake_env = faketime_environment("-2h")
    fake_epoch = int(
        ns_run(CLIENT_NS, ["date", "+%s"], env={**os.environ, **fake_env}).stdout.strip()
    )
    previous_exit_code = lab.restart_client(
        log_name="client-rollback",
        env_overrides=fake_env,
    )
    time.sleep(3)
    after = lab.tunnel_ping(count=6)
    evidence = session_evidence(
        log_tail(lab.scenario, "client-rollback", limit=65536)
    )
    old_session_id = initial_evidence.get("session_id")
    new_session_id = evidence.get("session_id")
    fresh_session = (
        isinstance(old_session_id, int)
        and isinstance(new_session_id, int)
        and old_session_id != new_session_id
    )
    result.metrics.update(
        {
            "initial_ping": initial,
            "initial_session_evidence": initial_evidence,
            "client_clock_rollback": "-2h",
            "measured_clock_offset_seconds": fake_epoch - normal_epoch,
            "new_session_evidence": evidence,
            "old_session_id": old_session_id,
            "new_session_id": new_session_id,
            "fresh_random_session_proven": fresh_session,
            "previous_client_exit_code": previous_exit_code,
            "ping_after_restart": after,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "initial_loss_percent_max": 0,
        "post_rollback_loss_percent_max": 0,
        "measured_clock_offset_seconds_max": -7000,
        "old_and_new_session_ids_must_differ": True,
        "new_session_and_schedule_must_be_accepted": True,
    }
    result.status = (
        "pass"
        if initial.get("loss_percent") == 0
        and after.get("loss_percent") == 0
        and fake_epoch - normal_epoch <= -7000
        and initial_evidence["session_accepted"]
        and initial_evidence["schedule_accepted"]
        and fresh_session
        and evidence["session_accepted"]
        and evidence["schedule_accepted"]
        else "fail"
    )


def scenario_stale_return_schedule(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    lab.start_runtime()
    before = lab.tunnel_ping(count=4)
    client_pid = lab.client.pid if lab.client else None
    client_status_before = json_file(lab.client_status) or {}
    duplicate_ids = (
        client_status_before.get("schedule", {}).get("duplicate_path_ids", [])
    )
    stale_backup_path = int(duplicate_ids[0]) if duplicate_ids else 2
    # First isolate only the second scheduled peer. A fresh anchor must keep
    # carrying return traffic even after the backup peer ages out.
    backup_router = ROUTER_NAMES[stale_backup_path - 1]
    ns_run(
        backup_router,
        ["iptables", "-I", "FORWARD", "-p", "udp", "--dport", str(SERVER_PORT), "-j", "DROP"],
    )
    time.sleep(18)
    fresh_anchor_ping = lab.tunnel_ping(count=8)
    fresh_anchor_pid_unchanged = client_pid == (lab.client.pid if lab.client else None)
    ns_run(backup_router, ["iptables", "-F"])
    time.sleep(3)

    # Control and payload are encrypted on the same UDP flow. A network-only
    # lab cannot select schedule-control frames, so use a one-way client->server
    # outage long enough to stale the return schedule, then restore it.
    for router in ROUTER_NAMES:
        ns_run(
            router,
            ["iptables", "-I", "FORWARD", "-p", "udp", "--dport", str(SERVER_PORT), "-j", "DROP"],
        )
    schedule_generation_before_outage = (
        (json_file(lab.server_status) or {}).get("return_schedule") or {}
    ).get("schedule_generation", 0)
    time.sleep(18)
    continuous_interval = 0.2
    continuous_command = [
        "ping",
        "-D",
        "-O",
        "-n",
        "-i",
        str(continuous_interval),
        "-c",
        "300",
        "-w",
        "60",
        "-I",
        "xbond0",
        SERVER_TUN_IP,
    ]
    continuous_ping = subprocess.Popen(
        ns_cmd(CLIENT_NS, *continuous_command),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        start_new_session=True,
    )
    lab.track_process(continuous_ping)
    time.sleep(1)
    restore_wall_time = time.time()
    restore_started = time.monotonic()
    for router in ROUTER_NAMES:
        ns_run(router, ["iptables", "-F"])

    wait_for(
        lambda: (
            (lab.client is not None and lab.client.poll() is not None)
            or (
                not (json_file(lab.server_status) or {}).get("schedule_required", True)
                and (
                    ((json_file(lab.server_status) or {}).get("return_schedule") or {}).get(
                        "schedule_generation", 0
                    )
                    > schedule_generation_before_outage
                )
            )
        ),
        20,
        "return schedule resynchronization or supervised client restart",
    )
    client_exit_code = lab.client.poll() if lab.client else None
    supervisor_restarted = client_exit_code is not None
    if supervisor_restarted:
        client_exit_code = lab.restart_client(log_name="client-schedule-resync")
    schedule_recovery_seconds = round(time.monotonic() - restore_started, 3)
    schedule_recovered_wall_time = time.time()
    # Keep the same ping process running long enough after schedule recovery
    # to measure a fixed 15-second steady-state window plus five seconds of
    # additional evidence for the separate sustained-recovery assertion.
    time.sleep(20)
    stopped_wall_time = time.time()
    if continuous_ping.poll() is None:
        lab.stop_process(
            continuous_ping, signal_number=signal.SIGINT, timeout=3
        )
    continuous_stdout, continuous_stderr = continuous_ping.communicate(
        timeout=5
    )
    continuous_result = parse_timestamped_ping(
        "\n".join(
            part
            for part in (continuous_stdout, continuous_stderr)
            if part
        ),
        continuous_command,
        restore_wall_time=restore_wall_time,
        steady_window_start_wall_time=schedule_recovered_wall_time,
        steady_window_seconds=15,
        sample_interval=continuous_interval,
        stopped_wall_time=stopped_wall_time,
    )
    continuous_result["exit_code"] = continuous_ping.returncode
    after = lab.tunnel_ping(count=8)
    validation_completed_seconds = round(time.monotonic() - restore_started, 3)
    lab.restart_iperf_server()
    download = iperf(reverse=True, seconds=4)
    result.notes.append(
        "The network-only outage expires the authenticated return schedule. The lab accepts either in-process resynchronization or the deployed systemd model: a clean nonzero client exit followed by a fresh session."
    )
    result.metrics.update(
        {
            "ping_before": before,
            "stale_backup_path_id": stale_backup_path,
            "fresh_anchor_with_stale_backup_ping": fresh_anchor_ping,
            "fresh_anchor_client_pid_unchanged": fresh_anchor_pid_unchanged,
            "one_way_control_and_data_loss_seconds": 18,
            "client_exit_code_before_supervisor_restart": client_exit_code,
            "supervisor_restarted_client": supervisor_restarted,
            "continuous_ping_started_before_restore": True,
            "continuous_ping_across_restore": continuous_result,
            "ping_after_restore": after,
            "schedule_recovery_seconds": schedule_recovery_seconds,
            "restore_to_validation_seconds": validation_completed_seconds,
            "download_after_restore": download,
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "post_restore_loss_percent_max": 5,
        "continuous_time_to_first_success_seconds_max": 15,
        "continuous_post_restore_loss_percent_max": 20,
        "continuous_sustained_recovery_required": True,
        "schedule_recovery_seconds_max": 15,
        "post_restore_download_mbps_min": 5,
        "post_restore_download_requires_complete_valid_iperf_json": True,
        "fresh_anchor_stale_backup_loss_percent_max": 10,
        "fresh_anchor_must_not_restart_client": True,
        "fresh_schedule_required": True,
    }
    runtime = result.metrics["runtime"]
    fresh_schedule = (
        runtime["client_status"].get("anchor_path_id") is not None
        and not runtime["server_status"].get("schedule_required", True)
    )
    result.status = (
        "pass"
        if fresh_anchor_ping.get("loss_percent", 100) <= 10
        and fresh_anchor_pid_unchanged
        and after.get("loss_percent", 100) <= 5
        and continuous_result.get("time_to_first_success_seconds") is not None
        and continuous_result["time_to_first_success_seconds"] <= 15
        and continuous_result.get("post_restore_loss_percent", 100) <= 20
        and continuous_result.get("sustained_recovery") is True
        and schedule_recovery_seconds <= 15
        and download.get("exit_code") == 0
        and download.get("valid_complete_json") is True
        and download.get("mbps", 0) >= 5
        and fresh_schedule
        else "fail"
    )


def scenario_queue_saturation(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    # This is still far below the production 2048/4096 capacities, but large
    # enough to exercise sustained pressure without turning the test into a
    # synthetic zero-throughput denial of service.
    lab.start_runtime(queue_capacity=32, inbound_capacity=64)
    retransmit_before = tcp_retransmits(CLIENT_NS)
    server_status_before = json_file(lab.server_status) or {}
    control_before = server_status_before.get("control_plane") or {}
    throughput, pressure_samples, ping_under_pressure = (
        concurrent_bidirectional_throughput(
            lab,
            seconds=10,
            parallel=8,
            sample_interval=0.1,
        )
    )
    throughput_completed = all(
        throughput[direction].get("exit_code") == 0
        and throughput[direction].get("valid_complete_json") is True
        for direction in ("upload", "download")
    )
    runtime_under_pressure = lab.collect_runtime_metrics()
    queue_telemetry = {
        key: value
        for side in ("client", "server")
        for key, value in runtime_under_pressure["telemetry"][side].items()
        if "queue" in key.lower() or "drop" in key.lower()
    }
    pressure_keys = {
        key: value
        for key, value in queue_telemetry.items()
        if isinstance(value, (int, float))
        and value > 0
        and any(
            marker in key.lower()
            for marker in ("queue_full", "queue_drops", "saturated_drops")
        )
    }
    server_status = runtime_under_pressure["server_status"] or {}
    control_after = server_status.get("control_plane") or {}
    required_primary_return_counters_present = all(
        isinstance(control_after.get(key), (int, float))
        for key in (
            "primary_return_queue_full",
            "primary_return_enqueued",
            "all_return_copies_dropped",
            "primary_return_enqueue_deadline_expiries",
        )
    )
    primary_return_queue_full_delta = max(
        0,
        nested_number(
            server_status, "control_plane", "primary_return_queue_full"
        )
        - nested_number(
            {"control_plane": control_before},
            "control_plane",
            "primary_return_queue_full",
        ),
    )
    primary_return_enqueued_delta = max(
        0,
        nested_number(
            server_status, "control_plane", "primary_return_enqueued"
        )
        - nested_number(
            {"control_plane": control_before},
            "control_plane",
            "primary_return_enqueued",
        ),
    )
    all_copies_dropped_delta = max(
        0,
        nested_number(
            server_status, "control_plane", "all_return_copies_dropped"
        )
        - nested_number(
            {"control_plane": control_before},
            "control_plane",
            "all_return_copies_dropped",
        ),
    )
    primary_enqueue_deadline_expiries_delta = max(
        0,
        nested_number(
            server_status,
            "control_plane",
            "primary_return_enqueue_deadline_expiries",
        )
        - nested_number(
            {"control_plane": control_before},
            "control_plane",
            "primary_return_enqueue_deadline_expiries",
        ),
    )
    all_copies_dropped = nested_number(
        server_status, "control_plane", "all_return_copies_dropped"
    )
    client_exit_code = lab.client.poll() if lab.client else None
    supervisor_restarted = client_exit_code is not None
    if supervisor_restarted:
        # The tiny queues above are an explicit fault injector. A production
        # supervisor restart reloads the normal configured capacities.
        lab.write_config(
            path_count=3,
            queue_capacity=2048,
            inbound_capacity=4096,
        )
        client_exit_code = lab.restart_client(log_name="client-queue-recovery")
    recovery_throughput = basic_throughput(lab, seconds=4)
    tunnel = lab.tunnel_ping(count=8)
    runtime_after_recovery = lab.collect_runtime_metrics()
    retransmit_delta = counter_delta(
        retransmit_before, tcp_retransmits(CLIENT_NS)
    )
    result.notes.append(
        "Extreme queue pressure may trigger the intentional fail-closed client exit. The lab emulates systemd supervision and requires the tunnel to recover after pressure is removed."
    )
    result.metrics.update(
        {
            "bidirectional_throughput_under_pressure": throughput,
            "bidirectional_iperf_completed": throughput_completed,
            "ping_concurrent_with_pressure": ping_under_pressure,
            "pressure_samples": pressure_samples,
            "client_exit_code_before_supervisor_restart": client_exit_code,
            "supervisor_restarted_client": supervisor_restarted,
            "throughput_after_recovery": recovery_throughput,
            "tunnel_ping_after_recovery": tunnel,
            "queue_telemetry": queue_telemetry,
            "positive_queue_pressure": pressure_keys,
            "all_return_copies_dropped": all_copies_dropped,
            "primary_return_queue_full_delta": primary_return_queue_full_delta,
            "primary_return_enqueued_delta": primary_return_enqueued_delta,
            "all_return_copies_dropped_delta": all_copies_dropped_delta,
            "primary_return_enqueue_deadline_expiries_delta": primary_enqueue_deadline_expiries_delta,
            "required_primary_return_counters_present": required_primary_return_counters_present,
            "tcp_retransmit_delta": retransmit_delta,
            "runtime_under_pressure": runtime_under_pressure,
            "client_log_tail": log_tail(lab.scenario, "client", limit=12000),
            "server_log_tail": log_tail(lab.scenario, "server", limit=12000),
            "runtime": runtime_after_recovery,
        }
    )
    result.thresholds = {
        "processes_must_recover_running": True,
        "concurrent_bidirectional_load_required": True,
        "both_iperf_directions_require_exit_zero_and_complete_valid_json": True,
        "server_primary_return_counters_required": True,
        "server_primary_return_queue_full_delta_min": 1,
        "server_primary_return_enqueued_delta_min": 1,
        "all_return_copies_dropped_delta_max": 0,
        "under_pressure_upload_mbps_min": 1,
        "under_pressure_download_mbps_min": 1,
        "under_pressure_ping_loss_percent_max": 10,
        "post_recovery_throughput_mbps_min": 5,
        "post_recovery_tunnel_loss_percent_max": 5,
        "tcp_retransmit_delta_max": 20000,
    }
    processes = runtime_after_recovery["processes"]
    result.status = (
        "pass"
        if processes["client"].get("running")
        and processes["server"].get("running")
        and throughput_completed
        and ping_under_pressure.get("ran_concurrently") is True
        and throughput["upload"].get("mbps", 0) >= 1
        and throughput["download"].get("mbps", 0) >= 1
        and ping_under_pressure.get("loss_percent", 100) <= 10
        and required_primary_return_counters_present
        and primary_return_queue_full_delta >= 1
        and primary_return_enqueued_delta >= 1
        and all_copies_dropped_delta == 0
        and min(
            recovery_throughput["upload"].get("mbps", 0),
            recovery_throughput["download"].get("mbps", 0),
        )
        >= 5
        and tunnel.get("loss_percent", 100) <= 5
        and (retransmit_delta is None or retransmit_delta <= 20000)
        else "fail"
    )


def scenario_mtu_sweep(lab: XBondLab, result: LabResult, _: int) -> None:
    lab.setup_topology()
    for path, router in enumerate(ROUTER_NAMES, start=1):
        for namespace, interface in (
            (CLIENT_NS, f"cpath{path}"),
            (router, f"r{path}c"),
            (router, f"r{path}s"),
            (SERVER_NS, f"spath{path}"),
        ):
            ns_run(namespace, ["ip", "link", "set", interface, "mtu", "1500"])
    candidates: list[dict[str, Any]] = []
    for mtu in (1200, 1300, 1400, 1450):
        lab.stop_runtime()
        lab.client_status.unlink(missing_ok=True)
        lab.server_status.unlink(missing_ok=True)
        lab.start_runtime(path_count=1, tun_mtu=mtu)
        client_log_before = log_tail(lab.scenario, "client", limit=200000)
        df_ping, outer_capture = capture_outer_packets(
            lab,
            lambda mtu=mtu: ping(
                CLIENT_NS,
                SERVER_TUN_IP,
                count=8,
                interface="xbond0",
                size=max(64, mtu - 28),
                do_not_fragment=True,
            ),
        )
        throughput = basic_throughput(lab, seconds=3)
        client_log_after = log_tail(lab.scenario, "client", limit=200000)
        pmtu_events = max(
            0,
            client_log_after.count('"event":"path-pmtu-message-too-large"')
            - client_log_before.count('"event":"path-pmtu-message-too-large"'),
        )
        candidates.append(
            {
                "mtu": mtu,
                "physical_path_mtu": 1500,
                "df_ping": df_ping,
                "throughput": throughput,
                "outer_capture": outer_capture,
                "pmtu_error_events": pmtu_events,
            }
        )
    result.metrics["candidates"] = candidates
    result.metrics["runtime"] = lab.collect_runtime_metrics()
    result.thresholds = {
        "required_mtu_values": [1200, 1300, 1400],
        "required_loss_percent_max": 0,
        "required_fragmented_packets_max": 0,
        "required_pmtu_error_events_max": 0,
        "required_throughput_mbps_min": 5,
        "note": "1450 is exploratory because the outer packet can exceed a 1500-byte physical MTU.",
    }
    required = [item for item in candidates if item["mtu"] <= 1400]
    result.status = (
        "pass"
        if all(
            item["df_ping"].get("loss_percent") == 0
            and item["outer_capture"].get("fragmented_packets", 1) == 0
            and item["pmtu_error_events"] == 0
            and min(
                item["throughput"]["upload"].get("mbps", 0),
                item["throughput"]["download"].get("mbps", 0),
            )
            >= 5
            for item in required
        )
        else "fail"
    )


def scenario_soak(lab: XBondLab, result: LabResult, duration: int) -> None:
    lab.setup_topology()
    lab.apply_netem(2, "delay 120ms 60ms distribution normal loss 5%")
    lab.apply_netem(3, "delay 220ms 120ms distribution normal loss 12%")
    lab.start_runtime()
    baseline_throughput = basic_throughput(lab, seconds=3)
    samples: list[dict[str, Any]] = []
    required_duration = max(1800, duration)
    client_pid = lab.client.pid if lab.client else None
    server_pid = lab.server.pid if lab.server else None
    soak_started = time.monotonic()
    deadline = soak_started + required_duration
    iteration = 0
    while time.monotonic() < deadline:
        sample = {
            "at_seconds": round(time.monotonic() - result.monotonic_start, 1),
            "processes": {
                "client": process_metrics(lab.client),
                "server": process_metrics(lab.server),
            },
            "ping": ping(
                CLIENT_NS,
                SERVER_TUN_IP,
                count=10,
                interface="xbond0",
                deadline=5,
                interval=0.1,
            ),
            "client_status": telemetry_subset(json_file(lab.client_status)),
            "server_status": telemetry_subset(json_file(lab.server_status)),
        }
        if iteration % 6 == 0:
            sample["throughput"] = basic_throughput(lab, seconds=3)
        samples.append(sample)
        iteration += 1
        time.sleep(5)
    actual_duration = time.monotonic() - soak_started
    sample_times = [float(sample["at_seconds"]) for sample in samples]
    client_rss = [
        float(sample["processes"]["client"]["rss_kib"])
        for sample in samples
        if isinstance(sample["processes"]["client"].get("rss_kib"), int)
    ]
    server_rss = [
        float(sample["processes"]["server"]["rss_kib"])
        for sample in samples
        if isinstance(sample["processes"]["server"].get("rss_kib"), int)
    ]
    rss_samples_complete = (
        bool(samples)
        and len(client_rss) == len(samples)
        and len(server_rss) == len(samples)
    )
    rss_trends = {
        "client": trend_summary(sample_times, client_rss)
        if rss_samples_complete
        else trend_summary([], []),
        "server": trend_summary(sample_times, server_rss)
        if rss_samples_complete
        else trend_summary([], []),
    }
    steady_state_start = max(1, int(len(samples) * 0.2))
    steady_state_times = sample_times[steady_state_start:]
    rss_steady_state_trends = {
        "client": trend_summary(
            steady_state_times,
            client_rss[steady_state_start:],
        )
        if rss_samples_complete
        else trend_summary([], []),
        "server": trend_summary(
            steady_state_times,
            server_rss[steady_state_start:],
        )
        if rss_samples_complete
        else trend_summary([], []),
    }
    ping_loss = [
        sample["ping"].get("loss_percent", 100) for sample in samples
    ]
    throughput_samples = [
        sample["throughput"]
        for sample in samples
        if "throughput" in sample
    ]
    baseline_complete = all(
        baseline_throughput[direction].get("exit_code") == 0
        and baseline_throughput[direction].get("valid_complete_json") is True
        for direction in ("upload", "download")
    )
    baseline_floor_mbps = min(
        baseline_throughput["upload"].get("mbps", 0),
        baseline_throughput["download"].get("mbps", 0),
    )
    throughput_samples_complete = bool(throughput_samples) and all(
        item[direction].get("exit_code") == 0
        and item[direction].get("valid_complete_json") is True
        for item in throughput_samples
        for direction in ("upload", "download")
    )
    throughput_floor_mbps = [
        min(
            item["upload"].get("mbps", 0),
            item["download"].get("mbps", 0),
        )
        for item in throughput_samples
    ]
    throughput_baseline_ratios = [
        value / baseline_floor_mbps if baseline_floor_mbps > 0 else 0.0
        for value in throughput_floor_mbps
    ]
    qualified_throughput_samples = [
        value
        for value, ratio in zip(
            throughput_floor_mbps,
            throughput_baseline_ratios,
            strict=True,
        )
        if value >= 1 and ratio >= 0.25
    ]
    queue_depth_trends, queue_samples_complete = per_metric_trends(
        samples,
        lambda key: key.endswith("queue_depth")
        or ("sender_lanes[" in key and key.endswith(".depth")),
    )
    queue_age_trends, queue_age_samples_complete = per_metric_trends(
        samples,
        lambda key: key.endswith("oldest_age_ms"),
    )
    client_tun_write_latency_times: list[float] = []
    client_tun_write_latency_samples: list[float] = []
    server_tun_write_latency_times: list[float] = []
    server_tun_write_latency_samples: list[float] = []
    server_tun_write_valid_sample_count = 0
    previous_client_packets: float | None = None
    previous_client_write_total: float | None = None
    for sample in samples:
        client_packets = sample["client_status"].get("process.tun_write_packets")
        client_write_total = sample["client_status"].get(
            "process.tun_write_micros_total"
        )
        server_write_latency = sample["server_status"].get(
            "control_plane.tun_write_latency_micros"
        )
        if not isinstance(client_packets, (int, float)) or not isinstance(
            client_write_total, (int, float)
        ):
            previous_client_packets = None
            previous_client_write_total = None
        else:
            current_packets = float(client_packets)
            current_write_total = float(client_write_total)
            if (
                previous_client_packets is not None
                and previous_client_write_total is not None
            ):
                packet_delta = current_packets - previous_client_packets
                write_delta = current_write_total - previous_client_write_total
                if packet_delta > 0 and write_delta >= 0:
                    client_tun_write_latency_times.append(float(sample["at_seconds"]))
                    client_tun_write_latency_samples.append(write_delta / packet_delta)
            previous_client_packets = current_packets
            previous_client_write_total = current_write_total
        if isinstance(server_write_latency, (int, float)):
            server_tun_write_valid_sample_count += 1
            server_tun_write_latency_times.append(float(sample["at_seconds"]))
            server_tun_write_latency_samples.append(float(server_write_latency))
    client_tun_write_expected_delta_count = max(0, len(samples) - 1)
    client_tun_write_sample_coverage = (
        len(client_tun_write_latency_samples)
        / client_tun_write_expected_delta_count
        if client_tun_write_expected_delta_count > 0
        else 0.0
    )
    server_tun_write_sample_coverage = (
        server_tun_write_valid_sample_count / len(samples) if samples else 0.0
    )
    tun_write_latency_samples_complete = (
        client_tun_write_sample_coverage >= 0.95
        and server_tun_write_sample_coverage >= 0.95
        and len(client_tun_write_latency_samples) >= 2
        and len(server_tun_write_latency_samples) >= 2
    )
    tun_write_latency_trends = {
        "client": trend_summary(
            client_tun_write_latency_times, client_tun_write_latency_samples
        )
        if tun_write_latency_samples_complete
        else trend_summary([], []),
        "server": trend_summary(
            server_tun_write_latency_times, server_tun_write_latency_samples
        )
        if tun_write_latency_samples_complete
        else trend_summary([], []),
    }
    all_sample_pids_unchanged = bool(samples) and all(
        sample["processes"]["client"].get("running")
        and sample["processes"]["server"].get("running")
        and sample["processes"]["client"].get("pid") == client_pid
        and sample["processes"]["server"].get("pid") == server_pid
        for sample in samples
    )
    counter_predicates: dict[str, Callable[[str], bool]] = {
        "harmful_drops": lambda key: any(
            marker in key.lower()
            for marker in (
                "inbound_queue_drops",
                "tun_queue_drops",
                "all_return_copies_dropped",
                "tun_write_queue_saturated_drops",
                "ingress_payload_queue_drops",
                ".data.enqueue_drops",
                ".data.deadline_drops",
                "repair.queue_drops",
            )
        ),
        "repair_misses": lambda key: key.lower().endswith("repair.cache_misses"),
        "late_duplicates": lambda key: key.lower().endswith("late_duplicates"),
        "rebinds": lambda key: key.lower().endswith("rebind_count"),
    }
    counter_growth: dict[str, dict[str, Any]] = {}
    counter_growth_complete = True
    duration_minutes = max(actual_duration / 60, 1 / 60)
    for category, predicate in counter_predicates.items():
        growth, complete = counter_growth_by_key(samples, predicate)
        total = sum(growth.values())
        counter_growth[category] = {
            "by_key": growth,
            "total": total,
            "per_minute": total / duration_minutes,
            "complete": complete,
        }
        counter_growth_complete = counter_growth_complete and complete
    result.metrics.update(
        {
            "configured_duration_seconds": duration,
            "actual_duration_seconds": round(actual_duration, 3),
            "samples": samples,
            "rss_samples_complete": rss_samples_complete,
            "rss_trends": rss_trends,
            "rss_steady_state_start_sample": steady_state_start,
            "rss_steady_state_trends": rss_steady_state_trends,
            "max_ping_loss_percent": max(ping_loss, default=100),
            "baseline_throughput": baseline_throughput,
            "baseline_throughput_complete": baseline_complete,
            "baseline_floor_mbps": baseline_floor_mbps,
            "throughput_sample_count": len(throughput_samples),
            "throughput_samples_complete": throughput_samples_complete,
            "throughput_floor_mbps": throughput_floor_mbps,
            "throughput_baseline_ratios": throughput_baseline_ratios,
            "qualified_throughput_sample_count": len(
                qualified_throughput_samples
            ),
            "queue_samples_complete": queue_samples_complete,
            "queue_depth_trends": queue_depth_trends,
            "queue_age_samples_complete": queue_age_samples_complete,
            "queue_oldest_age_trends": queue_age_trends,
            "counter_growth_complete": counter_growth_complete,
            "counter_growth": counter_growth,
            "tun_write_latency_samples_complete": tun_write_latency_samples_complete,
            "client_tun_write_sample_coverage": client_tun_write_sample_coverage,
            "server_tun_write_sample_coverage": server_tun_write_sample_coverage,
            "tun_write_latency_trends": tun_write_latency_trends,
            "all_sample_pids_unchanged": all_sample_pids_unchanged,
            "client_pid_unchanged": client_pid == (lab.client.pid if lab.client else None),
            "server_pid_unchanged": server_pid == (lab.server.pid if lab.server else None),
            "runtime": lab.collect_runtime_metrics(),
        }
    )
    result.thresholds = {
        "minimum_duration_seconds": 1800,
        "rss_samples_required_for_every_process_sample": True,
        "rss_peak_growth_kib_max": 32768,
        "rss_end_growth_kib_max": 16384,
        "rss_slope_kib_per_minute_max": 128,
        "rss_slope_steady_state_warmup_fraction": 0.2,
        "queue_samples_required": True,
        "each_queue_is_gated_independently": True,
        "queue_end_growth_max": 4,
        "queue_slope_per_minute_max": 0.25,
        "queue_oldest_age_ms_max": 1000,
        "queue_oldest_age_end_growth_ms_max": 250,
        "queue_oldest_age_slope_ms_per_minute_max": 5,
        "tun_write_latency_samples_required": True,
        "tun_write_latency_sample_coverage_min": 0.95,
        "tun_write_latency_micros_max": 100000,
        "tun_write_latency_end_growth_micros_max": 20000,
        "tun_write_latency_slope_micros_per_minute_max": 2000,
        "max_ping_loss_percent": 10,
        "baseline_and_samples_require_complete_valid_iperf_json": True,
        "throughput_floor_mbps_min": 1,
        "throughput_baseline_ratio_min": 0.25,
        "harmful_drop_counter_growth_max": 0,
        "repair_miss_counter_growth_max": 300,
        "repair_miss_growth_per_minute_max": 10,
        "late_duplicate_counter_growth_max": 30000,
        "late_duplicate_growth_per_minute_max": 1000,
        "rebind_counter_growth_max": 3,
        "process_pids_must_not_change_in_any_sample": True,
        "processes_must_remain_running": True,
    }
    processes = result.metrics["runtime"]["processes"]
    result.status = (
        "pass"
        if processes["client"].get("running")
        and processes["server"].get("running")
        and result.metrics["actual_duration_seconds"] >= 1800
        and result.metrics["max_ping_loss_percent"] <= 10
        and len(throughput_samples) > 0
        and baseline_complete
        and baseline_floor_mbps >= 1
        and throughput_samples_complete
        and len(qualified_throughput_samples) == len(throughput_samples)
        and rss_samples_complete
        and all(
            trend["peak_growth"] is not None
            and trend["peak_growth"] <= 32768
            and trend["end_growth"] is not None
            and trend["end_growth"] <= 16384
            for trend in rss_trends.values()
        )
        and all(
            trend["slope_per_minute"] is not None
            and trend["slope_per_minute"] <= 128
            for trend in rss_steady_state_trends.values()
        )
        and queue_samples_complete
        and all(
            trend["end_growth"] is not None
            and trend["end_growth"] <= 4
            and trend["slope_per_minute"] is not None
            and trend["slope_per_minute"] <= 0.25
            for trend in queue_depth_trends.values()
        )
        and queue_age_samples_complete
        and all(
            trend["peak"] is not None
            and trend["peak"] <= 1000
            and trend["end_growth"] is not None
            and trend["end_growth"] <= 250
            and trend["slope_per_minute"] is not None
            and trend["slope_per_minute"] <= 5
            for trend in queue_age_trends.values()
        )
        and counter_growth_complete
        and counter_growth["harmful_drops"]["total"] == 0
        and counter_growth["repair_misses"]["total"] <= 300
        and counter_growth["repair_misses"]["per_minute"] <= 10
        and counter_growth["late_duplicates"]["total"] <= 30000
        and counter_growth["late_duplicates"]["per_minute"] <= 1000
        and counter_growth["rebinds"]["total"] <= 3
        and tun_write_latency_samples_complete
        and all(
            trend["peak"] is not None
            and trend["peak"] <= 100000
            and trend["end_growth"] is not None
            and trend["end_growth"] <= 20000
            and trend["slope_per_minute"] is not None
            and trend["slope_per_minute"] <= 2000
            for trend in tun_write_latency_trends.values()
        )
        and all_sample_pids_unchanged
        and result.metrics["client_pid_unchanged"]
        and result.metrics["server_pid_unchanged"]
        else "fail"
    )


SCENARIOS: dict[str, Callable[[XBondLab, LabResult, int], None]] = {
    "topology-smoke": scenario_topology_smoke,
    "healthy-single": scenario_healthy_single,
    "anchor-bad-backup": scenario_anchor_bad_backup,
    "all-intermittent": scenario_all_intermittent,
    "heavy-bidirectional": scenario_heavy_bidirectional,
    "silent-blackhole": scenario_silent_blackhole,
    "usb-reenumeration": scenario_usb_reenumeration,
    "tun-read-failure": scenario_tun_read_failure,
    "server-process-restart": scenario_server_process_restart,
    "tun-write-backpressure": scenario_tun_write_backpressure,
    "server-tun-write-backpressure": scenario_server_tun_write_backpressure,
    "clock-rollback": scenario_clock_rollback,
    "clock-skew": scenario_clock_skew,
    "stale-return-schedule": scenario_stale_return_schedule,
    "queue-saturation": scenario_queue_saturation,
    "mtu-sweep": scenario_mtu_sweep,
    "soak": scenario_soak,
}


def execute_one(scenario: str, duration: int) -> tuple[dict[str, Any], pathlib.Path]:
    result = LabResult(scenario=scenario)
    orchestrator_path = pathlib.Path(__file__)
    schema_path = orchestrator_path.parent / "result.schema.json"
    orchestrator_hash = sha256_file(orchestrator_path)
    schema_hash = sha256_file(schema_path)
    expected_orchestrator_hash = os.environ.get(
        "LAB_ORCHESTRATOR_SHA256", "unknown"
    ).lower()
    expected_schema_hash = os.environ.get("LAB_SCHEMA_SHA256", "unknown").lower()
    result.provenance = {
        "git_commit": os.environ.get("LAB_GIT_COMMIT", "unknown"),
        "git_branch": os.environ.get("LAB_GIT_BRANCH", "unknown"),
        "git_dirty": os.environ.get("LAB_GIT_DIRTY", "true").lower() == "true",
        "docker_context": os.environ.get("LAB_DOCKER_CONTEXT", "unknown"),
        "docker_host": os.environ.get("LAB_DOCKER_HOST", "unknown"),
        "docker_engine_version": os.environ.get(
            "LAB_DOCKER_ENGINE_VERSION", "unknown"
        ),
        "docker_kernel": os.environ.get("LAB_DOCKER_KERNEL", platform.release()),
        "docker_os": os.environ.get("LAB_DOCKER_OS", platform.platform()),
        "image_id": os.environ.get("LAB_IMAGE_ID", "unknown"),
        "image_digests": [
            value
            for value in os.environ.get("LAB_IMAGE_DIGESTS", "").split(",")
            if value
        ],
        "client_binary_sha256": sha256_file(
            pathlib.Path("/opt/xbond/bin/xbond-client")
        ),
        "server_binary_sha256": sha256_file(
            pathlib.Path("/opt/xbond/bin/xbond-server")
        ),
        "lab_orchestrator_sha256": orchestrator_hash,
        "result_schema_sha256": schema_hash,
        "lab_orchestrator_source_sha256": expected_orchestrator_hash,
        "result_schema_source_sha256": expected_schema_hash,
        "lab_orchestrator_hash_matches_source": (
            orchestrator_hash == expected_orchestrator_hash
        ),
        "result_schema_hash_matches_source": schema_hash == expected_schema_hash,
    }
    lab = XBondLab(scenario, result)
    try:
        if not result.provenance["lab_orchestrator_hash_matches_source"]:
            raise RuntimeError(
                "copied lab.py hash does not match the run.ps1 source hash"
            )
        if not result.provenance["result_schema_hash_matches_source"]:
            raise RuntimeError(
                "copied result.schema.json hash does not match the run.ps1 source hash"
            )
        for binary_name in ("client_binary_sha256", "server_binary_sha256"):
            binary_hash = result.provenance.get(binary_name)
            if not isinstance(binary_hash, str) or not re.fullmatch(
                r"[0-9a-f]{64}", binary_hash
            ):
                raise RuntimeError(
                    f"{binary_name} is missing or is not a lowercase SHA-256 digest"
                )
        SCENARIOS[scenario](lab, result, duration)
    except Exception as error:  # noqa: BLE001 - result must capture operator failures
        result.status = "error"
        result.reason = str(error)
        result.errors.append(traceback.format_exc())
        with contextlib.suppress(Exception):
            result.metrics["failure_diagnostics"] = lab.collect_failure_diagnostics()
    finally:
        result.cleanup = lab.cleanup()
        if (
            result.cleanup["namespaces_remaining"]
            or result.cleanup["processes_remaining"]
            or result.cleanup["process_groups_remaining"]
            or result.cleanup["namespace_pids_remaining"]
            or not result.cleanup["qdisc_state_removed"]
        ):
            if result.status == "pass":
                result.status = "fail"
            result.errors.append(
                "Lab cleanup left namespaces, descendants, process groups, "
                "or managed qdisc state behind."
            )
    payload = result.finish()
    schema = json.loads(schema_path.read_text(encoding="utf-8"))
    try:
        jsonschema.validate(payload, schema)
    except jsonschema.ValidationError as error:
        payload["status"] = "error"
        payload["errors"].append(f"Result schema validation failed: {error.message}")
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    path = RESULTS_DIR / f"{stamp}-{scenario}.json"
    path.write_text(json.dumps(payload, indent=2, sort_keys=True), encoding="utf-8")
    print(json.dumps({"scenario": scenario, "status": payload["status"], "result": str(path)}))
    return payload, path


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("scenario", choices=(*SCENARIOS.keys(), "matrix"))
    parser.add_argument("--duration-seconds", type=int, default=1800)
    args = parser.parse_args()
    if args.scenario in {"soak", "matrix"} and args.duration_seconds < 1800:
        parser.error("soak and matrix require --duration-seconds >= 1800")

    signal.signal(signal.SIGTERM, lambda *_: raise_system_exit())
    if args.scenario != "matrix":
        payload, _ = execute_one(args.scenario, args.duration_seconds)
        return 0 if payload["status"] == "pass" else 1

    matrix_started_at = utc_now()
    matrix_scenarios = list(SCENARIOS)
    summaries = []
    paths = []
    for scenario in matrix_scenarios:
        payload, path = execute_one(scenario, args.duration_seconds)
        summaries.append(
            {
                "scenario": scenario,
                "status": payload["status"],
                "reason": payload.get("reason"),
            }
        )
        paths.append(str(path))
    matrix_payload = {
        "schema_version": 1,
        "scenario": "matrix",
        "started_at": matrix_started_at,
        "finished_at": utc_now(),
        "status": (
            "pass"
            if all(item["status"] == "pass" for item in summaries)
            else "fail"
        ),
        "results": summaries,
        "result_files": paths,
    }
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    matrix_path = RESULTS_DIR / f"{stamp}-matrix-summary.json"
    matrix_path.write_text(json.dumps(matrix_payload, indent=2), encoding="utf-8")
    print(json.dumps({"scenario": "matrix", "status": matrix_payload["status"], "result": str(matrix_path)}))
    return 0 if matrix_payload["status"] == "pass" else 1


def raise_system_exit() -> None:
    raise SystemExit(143)


if __name__ == "__main__":
    sys.exit(main())
