import importlib.util
import json
import os
import pathlib
import tempfile
import unittest
from unittest import mock


SCRIPT = pathlib.Path(__file__).with_name("xbond-client-egress.py")
SPEC = importlib.util.spec_from_file_location("xbond_client_egress", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(MODULE)


class EgressHelperTests(unittest.TestCase):
    def test_reserved_mark_and_table_ranges_do_not_overlap_bypass(self):
        self.assertEqual(0x130001, MODULE.mark_for(1))
        self.assertEqual(13001, MODULE.table_for(1))
        self.assertGreater(13001, 12100)
        self.assertEqual(0x13FFFF, MODULE.NONE_MARK)
        self.assertEqual(13999, MODULE.NONE_TABLE)

    def test_rejects_unsafe_or_non_wan_interfaces(self):
        for name in ("eth0", "xbond0", "tailscale0", "bad iface", "../wan"):
            with self.assertRaises(MODULE.EgressError):
                MODULE.validate_interface(name, "eth0")

    def test_accepts_expected_physical_interfaces(self):
        for name in ("wlan0", "enxc8a3627ddf6a", "wwan0"):
            self.assertEqual(name, MODULE.validate_interface(name, "eth0"))

    def test_invalid_path_ids_are_rejected(self):
        for value in (0, 999, 1000, -1):
            with self.assertRaises(MODULE.EgressError):
                MODULE.validate_path_id(value)

    def test_ipv4_prefix_is_derived_without_string_parsing(self):
        self.assertEqual("192.168.5.0/24", str(MODULE.ipaddress.ip_interface("192.168.5.10/24").network))

    def test_filtered_route_json_does_not_need_to_repeat_device(self):
        with mock.patch.object(
            MODULE,
            "json_command",
            return_value=[{"dst": "default", "gateway": "192.168.5.1", "metric": 103}],
        ):
            self.assertEqual("192.168.5.1", MODULE.default_route("wwan0")["gateway"])

    def test_state_write_is_atomic_and_round_trips(self):
        with tempfile.TemporaryDirectory() as directory:
            state_path = pathlib.Path(directory) / "egress.json"
            with mock.patch.dict(os.environ, {"ULINK_EGRESS_STATE": str(state_path)}):
                MODULE.write_state({"egress": "direct", "path_id": 4})
                self.assertEqual(
                    {"egress": "direct", "path_id": 4},
                    MODULE.read_state(),
                )
                self.assertFalse(state_path.with_suffix(".tmp").exists())

    def test_reconcile_reapplies_saved_target_without_reselecting(self):
        with tempfile.TemporaryDirectory() as directory:
            state_path = pathlib.Path(directory) / "egress.json"
            state_path.write_text(
                json.dumps(
                    {
                        "egress": "direct",
                        "path_id": 3,
                        "interface": "wwan0",
                        "lan_interface": "eth0",
                        "lan_cidr": "192.168.145.0/24",
                        "tun_interface": "xbond0",
                    }
                ),
                encoding="utf-8",
            )
            with (
                mock.patch.dict(os.environ, {"ULINK_EGRESS_STATE": str(state_path)}),
                mock.patch.object(MODULE, "switch") as switch,
            ):
                MODULE.reconcile()
                args = switch.call_args.args[0]
                self.assertEqual("direct", args.egress)
                self.assertEqual(3, args.path_id)
                self.assertEqual("wwan0", args.interface)

    def test_none_target_installs_unreachable_policy(self):
        args = MODULE.argparse.Namespace(
            egress="none",
            path_id=None,
            interface=None,
            flush_failed_path_id=None,
        )
        with (
            mock.patch.object(MODULE, "prepare_none", return_value=(MODULE.NONE_MARK, MODULE.NONE_TABLE)) as prepare,
            mock.patch.object(MODULE, "ensure_nft_table") as ensure_nft,
            mock.patch.object(MODULE, "replace_main_default_none") as replace_default,
            mock.patch.object(MODULE, "write_state") as write_state,
        ):
            MODULE.switch(args)
            prepare.assert_called_once_with()
            ensure_nft.assert_called_once_with("eth0", "192.168.145.0/24", MODULE.NONE_MARK)
            replace_default.assert_called_once_with("xbond0")
            write_state.assert_called_once()

    def test_failed_switch_reapplies_previous_state(self):
        args = MODULE.argparse.Namespace(
            egress="direct",
            path_id=2,
            interface="wwan1",
            flush_failed_path_id=None,
        )
        previous = {
            "egress": "direct",
            "path_id": 1,
            "interface": "wwan0",
            "lan_interface": "eth0",
            "lan_cidr": "192.168.145.0/24",
            "tun_interface": "xbond0",
        }
        with (
            mock.patch.object(MODULE, "read_state", return_value=previous),
            mock.patch.object(MODULE, "switch", side_effect=MODULE.EgressError("apply failed")) as switch,
            mock.patch.object(MODULE, "apply_saved_state") as restore,
        ):
            with self.assertRaises(MODULE.EgressError):
                MODULE.switch_with_rollback(args)
            switch.assert_called_once_with(args)
            restore.assert_called_once_with(previous)


if __name__ == "__main__":
    unittest.main()
