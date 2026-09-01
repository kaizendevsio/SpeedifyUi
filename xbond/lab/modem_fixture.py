#!/usr/bin/env python3
"""Small HTTP fixtures for hardware-facing uLink development tests."""

from __future__ import annotations

import argparse
import json
import signal
import struct
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse


def varint(value: int) -> bytes:
    output = bytearray()
    while True:
        current = value & 0x7F
        value >>= 7
        output.append(current | (0x80 if value else 0))
        if not value:
            return bytes(output)


def bytes_field(field: int, value: bytes) -> bytes:
    return varint((field << 3) | 2) + varint(len(value)) + value


def string_field(field: int, value: str) -> bytes:
    return bytes_field(field, value.encode())


def int_field(field: int, value: int) -> bytes:
    return varint(field << 3) + varint(value)


def float_field(field: int, value: float) -> bytes:
    return varint((field << 3) | 5) + struct.pack("<f", value)


def starlink_status_frame() -> bytes:
    device_info = b"".join(
        (
            string_field(1, "ut-ulink-lab"),
            string_field(2, "virtual_dish"),
            string_field(3, "ulink-lab-1"),
        )
    )
    device_state = int_field(1, 7200)
    obstruction = float_field(1, 0.012) + int_field(5, 0)
    gps = int_field(1, 1) + int_field(2, 18)
    alignment = int_field(1, 2) + float_field(3, 1.8)
    dish_status = b"".join(
        (
            bytes_field(1, device_info),
            bytes_field(2, device_state),
            float_field(1003, 0.002),
            bytes_field(1004, obstruction),
            float_field(1007, 180_000_000),
            float_field(1008, 20_000_000),
            float_field(1009, 34.0),
            float_field(1011, 118.0),
            float_field(1012, 71.0),
            bytes_field(1015, gps),
            int_field(1023, 2),
            bytes_field(1027, alignment),
        )
    )
    message = bytes_field(2004, dish_status)
    return b"\x00" + len(message).to_bytes(4, "big") + message


def grpc_success_trailer() -> bytes:
    trailer = b"grpc-status: 0\r\n"
    return b"\x80" + len(trailer).to_bytes(4, "big") + trailer


class FixtureHandler(BaseHTTPRequestHandler):
    server_version = "uLinkLabFixture/1.0"

    @property
    def profile(self) -> str:
        return self.server.profile  # type: ignore[attr-defined]

    @property
    def provider(self) -> str:
        return self.server.provider  # type: ignore[attr-defined]

    @property
    def signal_bars(self) -> int:
        return self.server.signal_bars  # type: ignore[attr-defined]

    def log_message(self, format: str, *args: object) -> None:
        return

    def send_body(self, body: bytes, content_type: str = "text/html; charset=utf-8") -> None:
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def send_json(self, payload: dict[str, object]) -> None:
        self.send_body(json.dumps(payload).encode(), "application/json")

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        path = urlparse(self.path).path
        if self.profile == "f50":
            if path == "/goform/goform_get_cmd_process":
                self.send_json(
                    {
                        "network_type": "NR5G",
                        "current_network_type": "NR5G",
                        "network_provider": self.provider,
                        "operator": self.provider,
                        "signalbar": str(self.signal_bars),
                        "wan_ipaddr": "100.64.0.10",
                        "modem_main_state": "modem_init_complete",
                    }
                )
                return
            self.send_body(b"<html><title>Virtual F50</title></html>")
            return

        if self.profile == "cudy":
            if path == "/cgi-bin/luci":
                self.send_body(
                    b'<form action="/cgi-bin/luci" method="post">'
                    b'<input name="token" value="lab-token">'
                    b'<input name="salt" value="lab-salt">'
                    b'<input name="_csrf" value="lab-csrf">'
                    b'<input name="luci_username"><input name="luci_password"></form>'
                )
                return
            if path.endswith("/wireless/config/combo"):
                self.send_body(b'<input name="cbid.wireless.smart.connect" value="0">')
                return
            if path.endswith("/wireless/config/uncombine"):
                self.send_body(
                    b'<form action="/cgi-bin/luci/admin/network/wireless/config/uncombine">'
                    b'<input name="cbid.wireless.wlan00.disabled" value="0">'
                    b'<input name="cbid.wireless.wlan10.disabled" value="0"></form>'
                )
                return
            if path.endswith("/devices/devlist"):
                self.send_body(
                    b"var devices = [{mac:'02:00:00:00:04:10',hostname:'Lab Client',"
                    b"ip:'10.4.0.10',online:'1',internet:'1'}];"
                )
                return
            if path.endswith("/servicectl/status"):
                self.send_body(b"finish", "text/plain")
                return
            self.send_body(b"<html><title>Virtual Cudy</title></html>")
            return

        self.send_body(
            b'<html><title>Virtual Starlink</title><script src="/script.js"></script></html>'
            if path == "/"
            else b"window.starlink={reboot:true,stow:true,unstow:true,dish_clear_obstruction_map:true};",
            "application/javascript" if path == "/script.js" else "text/html; charset=utf-8",
        )

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        path = urlparse(self.path).path
        if self.profile == "starlink" and path.endswith("/SpaceX.API.Device.Device/Handle"):
            payload = body[5:] if len(body) >= 5 else body
            response = starlink_status_frame() if payload.startswith(b"\xe2\x3e\x00") else grpc_success_trailer()
            self.send_body(response, "application/grpc-web+proto")
            return
        if self.profile == "cudy":
            self.send_body(b"<html>saved</html>")
            return
        self.send_body(b"ok", "text/plain")


def serve(profile: str, provider: str, signal_bars: int, port: int) -> ThreadingHTTPServer:
    server = ThreadingHTTPServer(("0.0.0.0", port), FixtureHandler)
    server.profile = profile  # type: ignore[attr-defined]
    server.provider = provider  # type: ignore[attr-defined]
    server.signal_bars = signal_bars  # type: ignore[attr-defined]
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    return server


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", choices=("starlink", "f50", "cudy"), required=True)
    parser.add_argument("--provider", default="uLink Lab")
    parser.add_argument("--signal-bars", type=int, default=4)
    args = parser.parse_args()

    servers = [serve(args.profile, args.provider, max(0, min(5, args.signal_bars)), 80)]
    if args.profile == "starlink":
        servers.append(serve(args.profile, args.provider, args.signal_bars, 9201))

    stopped = threading.Event()
    signal.signal(signal.SIGTERM, lambda *_: stopped.set())
    signal.signal(signal.SIGINT, lambda *_: stopped.set())
    stopped.wait()
    for server in servers:
        server.shutdown()
        server.server_close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
