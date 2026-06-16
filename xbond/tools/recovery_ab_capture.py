#!/usr/bin/env python3
"""Operator capture for XBond all-live-intermittent recovery tests.

Run on the XBond client/router. This is not a user-facing UI feature.
It temporarily applies netem to live physical XBond interfaces, captures
client/server status snapshots, runs tunnel ping and iperf, and removes
the qdisc state before writing a JSON artifact.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import threading
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def run(command: list[str], timeout: int = 30) -> dict[str, Any]:
    started = time.time()
    try:
        completed = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        return {
            "command": command,
            "returncode": completed.returncode,
            "stdout": completed.stdout,
            "stderr": completed.stderr,
            "elapsed_seconds": round(time.time() - started, 3),
        }
    except subprocess.TimeoutExpired as error:
        return {
            "command": command,
            "returncode": 124,
            "stdout": error.stdout or "",
            "stderr": error.stderr or f"timed out after {timeout} seconds",
            "elapsed_seconds": round(time.time() - started, 3),
        }


def run_shell(command: str, timeout: int = 30) -> dict[str, Any]:
    started = time.time()
    try:
        completed = subprocess.run(
            command,
            shell=True,
            check=False,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        return {
            "command": command,
            "returncode": completed.returncode,
            "stdout": completed.stdout,
            "stderr": completed.stderr,
            "elapsed_seconds": round(time.time() - started, 3),
        }
    except subprocess.TimeoutExpired as error:
        return {
            "command": command,
            "returncode": 124,
            "stdout": error.stdout or "",
            "stderr": error.stderr or f"timed out after {timeout} seconds",
            "elapsed_seconds": round(time.time() - started, 3),
        }


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except Exception as error:  # noqa: BLE001 - diagnostics must record parse/read failures.
        return {"error": str(error), "path": str(path)}


def live_xbond_paths(status: dict[str, Any]) -> list[dict[str, Any]]:
    paths = status.get("paths") or []
    anchor_path_id = status.get("anchor_path_id")
    live = [
        path
        for path in paths
        if path.get("interface_up") is True
        and path.get("interface_name")
        and path.get("interface_name") != "xbond0"
        and not str(path.get("interface_name")).startswith("xbond")
    ]
    live.sort(key=lambda path: (path.get("path_id") != anchor_path_id, path.get("path_id") or 0))
    return live


def qdisc_show(interface_name: str) -> dict[str, Any]:
    return run(["tc", "-s", "qdisc", "show", "dev", interface_name])


def qdisc_cleanup(interface_name: str) -> dict[str, Any]:
    return run(["tc", "qdisc", "del", "dev", interface_name, "root"])


def apply_netem(interface_name: str, delay_ms: int, jitter_ms: int, loss_percent: float) -> dict[str, Any]:
    return run(
        [
            "tc",
            "qdisc",
            "replace",
            "dev",
            interface_name,
            "root",
            "netem",
            "delay",
            f"{delay_ms}ms",
            f"{jitter_ms}ms",
            "loss",
            f"{loss_percent}%",
        ]
    )


def sample_statuses(
    stop_event: threading.Event,
    client_status_path: Path,
    server_status_command: str | None,
    samples: list[dict[str, Any]],
) -> None:
    while not stop_event.is_set():
        sample: dict[str, Any] = {
            "timestamp": datetime.now(timezone.utc).isoformat(),
            "client": read_json(client_status_path),
        }
        if server_status_command:
            result = run_shell(server_status_command, timeout=10)
            sample["server_status_command"] = result
            if result["returncode"] == 0 and result["stdout"].strip():
                try:
                    sample["server"] = json.loads(result["stdout"])
                except json.JSONDecodeError as error:
                    sample["server"] = {"error": str(error), "raw": result["stdout"]}
        samples.append(sample)
        stop_event.wait(1.0)


def run_capture(args: argparse.Namespace) -> dict[str, Any]:
    status_before = read_json(args.client_status_path)
    paths = live_xbond_paths(status_before)
    qdisc_before = {
        path["interface_name"]: qdisc_show(path["interface_name"])
        for path in paths
    }
    netem_apply: dict[str, Any] = {}
    cleanup: dict[str, Any] = {}
    samples: list[dict[str, Any]] = []
    stop_event = threading.Event()
    sampler = threading.Thread(
        target=sample_statuses,
        args=(stop_event, args.client_status_path, args.server_status_command, samples),
        daemon=True,
    )

    try:
        if args.apply_netem:
            for index, path in enumerate(paths):
                interface_name = path["interface_name"]
                if index == 0:
                    netem_apply[interface_name] = apply_netem(interface_name, 140, 80, 12.0)
                else:
                    netem_apply[interface_name] = apply_netem(interface_name, 360, 180, 35.0)

        sampler.start()
        time.sleep(args.settle_seconds)
        ping = run(
            [
                "ping",
                "-c",
                str(args.ping_count),
                "-i",
                str(args.ping_interval_seconds),
                args.tunnel_server_ip,
            ],
            timeout=max(10, int(args.ping_count * args.ping_interval_seconds) + 10),
        )
        iperf_upload = run(
            [
                "iperf3",
                "-c",
                args.tunnel_server_ip,
                "-p",
                str(args.iperf_port),
                "-t",
                str(args.iperf_seconds),
                "-J",
            ],
            timeout=args.iperf_seconds + 20,
        )
        iperf_download = run(
            [
                "iperf3",
                "-c",
                args.tunnel_server_ip,
                "-p",
                str(args.iperf_port),
                "-t",
                str(args.iperf_seconds),
                "-R",
                "-J",
            ],
            timeout=args.iperf_seconds + 20,
        )
        qdisc_during = {
            path["interface_name"]: qdisc_show(path["interface_name"])
            for path in paths
        }
    finally:
        if args.apply_netem:
            for path in paths:
                cleanup[path["interface_name"]] = qdisc_cleanup(path["interface_name"])
        stop_event.set()
        sampler.join(timeout=3)

    qdisc_after = {
        path["interface_name"]: qdisc_show(path["interface_name"])
        for path in paths
    }
    artifact = {
        "scenario": "all-live-intermittent",
        "timestamp": datetime.now(timezone.utc).isoformat(),
        "apply_netem": args.apply_netem,
        "client_status_path": str(args.client_status_path),
        "server_status_command": args.server_status_command,
        "paths": paths,
        "status_before": status_before,
        "status_after": read_json(args.client_status_path),
        "qdisc_before": qdisc_before,
        "qdisc_during": qdisc_during,
        "qdisc_cleanup": cleanup,
        "qdisc_after": qdisc_after,
        "netem_apply": netem_apply,
        "samples": samples,
        "ping": ping,
        "iperf_upload": iperf_upload,
        "iperf_download": iperf_download,
    }
    args.artifact_dir.mkdir(parents=True, exist_ok=True)
    output_path = args.artifact_dir / f"xbond-recovery-capture-{int(time.time())}.json"
    output_path.write_text(json.dumps(artifact, separators=(",", ":")), encoding="utf-8")
    artifact["artifact_path"] = str(output_path)
    return artifact


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Capture XBond recovery behavior under all-live intermittent netem.")
    parser.add_argument("--client-status-path", type=Path, default=Path("/run/xbond/client-status.json"))
    parser.add_argument("--artifact-dir", type=Path, default=Path("/tmp"))
    parser.add_argument("--server-status-command", default=None)
    parser.add_argument("--tunnel-server-ip", default="10.250.0.1")
    parser.add_argument("--iperf-port", type=int, default=5201)
    parser.add_argument("--iperf-seconds", type=int, default=10)
    parser.add_argument("--ping-count", type=int, default=60)
    parser.add_argument("--ping-interval-seconds", type=float, default=0.2)
    parser.add_argument("--settle-seconds", type=float, default=3.0)
    parser.add_argument("--no-netem", action="store_false", dest="apply_netem")
    parser.set_defaults(apply_netem=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.apply_netem and hasattr(os, "geteuid") and os.geteuid() != 0:
        raise SystemExit("netem capture must run as root; rerun with sudo or pass --no-netem")
    artifact = run_capture(args)
    print(json.dumps({"artifact_path": artifact["artifact_path"], "paths": len(artifact["paths"])}, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
