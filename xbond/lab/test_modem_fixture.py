import json
import unittest
import urllib.request

try:
    from .modem_fixture import grpc_success_trailer, serve, starlink_status_frame
except ImportError:
    from modem_fixture import grpc_success_trailer, serve, starlink_status_frame


class ModemFixtureTests(unittest.TestCase):
    def test_f50_exposes_provider_and_signal(self) -> None:
        server = serve("f50", "SMART", 4, 0)
        try:
            port = server.server_address[1]
            with urllib.request.urlopen(
                f"http://127.0.0.1:{port}/goform/goform_get_cmd_process?cmd=network_provider,signalbar"
            ) as response:
                payload = json.load(response)
            self.assertEqual("SMART", payload["network_provider"])
            self.assertEqual("4", payload["signalbar"])
        finally:
            server.shutdown()
            server.server_close()

    def test_cudy_exposes_split_band_fields(self) -> None:
        server = serve("cudy", "Fiber Wi-Fi", 5, 0)
        try:
            port = server.server_address[1]
            with urllib.request.urlopen(
                f"http://127.0.0.1:{port}/cgi-bin/luci/admin/network/wireless/config/uncombine"
            ) as response:
                body = response.read().decode()
            self.assertIn("cbid.wireless.wlan00.disabled", body)
            self.assertIn("cbid.wireless.wlan10.disabled", body)
        finally:
            server.shutdown()
            server.server_close()

    def test_starlink_frames_are_well_formed(self) -> None:
        status = starlink_status_frame()
        self.assertEqual(0, status[0])
        self.assertEqual(len(status) - 5, int.from_bytes(status[1:5], "big"))

        trailer = grpc_success_trailer()
        self.assertEqual(0x80, trailer[0])
        self.assertIn(b"grpc-status: 0", trailer)


if __name__ == "__main__":
    unittest.main()
