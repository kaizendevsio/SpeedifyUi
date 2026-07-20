import json
import pathlib
import unittest
from unittest.mock import patch

from jsonschema import Draft202012Validator, ValidationError

from lab import (
    ResultSemanticValidationError,
    anchor_stabilization_summary,
    collect_soak_baseline,
    counter_growth_by_key,
    is_harmful_drop_counter,
    repair_cache_quiescence_evidence,
    robust_steady_state_evidence,
    soak_baseline_is_complete,
    telemetry_subset,
    theil_sen_slope_per_minute,
    validate_result_semantics,
)


SCHEMA_PATH = pathlib.Path(__file__).with_name("result.schema.json")


def direction(mbps=100.0, *, valid=True):
    return {
        "exit_code": 0 if valid else -1,
        "valid_complete_json": valid,
        "mbps": mbps if valid else 0.0,
    }


def throughput(upload=100.0, download=120.0, *, valid=True):
    return {
        "upload": direction(upload, valid=valid),
        "download": direction(download, valid=valid),
    }


def attempt(number, floor, upload, download):
    return {
        "attempt": number,
        "valid_complete": True,
        "floor_mbps": floor,
        "throughput": throughput(upload, download),
    }


def invalid_attempt(number):
    return {
        "attempt": number,
        "valid_complete": False,
        "floor_mbps": None,
        "throughput": throughput(valid=False),
    }


def baseline_measurement():
    attempts = [
        attempt(1, 90.0, 90.0, 110.0),
        attempt(2, 100.0, 100.0, 120.0),
        attempt(3, 105.0, 105.0, 125.0),
    ]
    return {
        "warmup_seconds": 2,
        "warmup": {
            "valid_complete": True,
            "floor_mbps": 80.0,
            "throughput": throughput(80.0, 95.0),
        },
        "sample_seconds": 3,
        "required_valid_samples": 3,
        "max_attempts": 5,
        "attempt_count": 3,
        "valid_attempt_count": 3,
        "attempts": attempts,
        "complete": True,
        "selected_attempt": 2,
        "selected_median_floor_mbps": 100.0,
        "selected_throughput": throughput(100.0, 120.0),
    }


def robust_window(slope=0.0, growth=0.0):
    return {
        "sample_count": 10,
        "start_at_seconds": 0.0,
        "end_at_seconds": 600.0,
        "duration_seconds": 600.0,
        "theil_sen_slope_kib_per_minute": slope,
        "edge_sample_count": 5,
        "start_edge_median_kib": 1000.0,
        "end_edge_median_kib": 1000.0 + growth,
        "edge_median_growth_kib": growth,
    }


def robust_memory_evidence(slope=0.0, growth=0.0):
    return {
        "sample_count": 20,
        "final_half": robust_window(slope=slope),
        "final_10_minutes": robust_window(slope=slope, growth=growth),
    }


def anchor_precondition_stage(stage, *, require_degraded_backups=False):
    backup_loss = 25.0 if require_degraded_backups else 0.0
    backup_role = "Probe" if require_degraded_backups else "Backup"
    return {
        "stage": stage,
        "valid": True,
        "failures": [],
        "anchor_path_id": 1,
        "anchor_path_loss_percent": 0.0,
        "recovery_active": False,
        "require_degraded_backups": require_degraded_backups,
        "backup_paths": {
            "2": {
                "present": True,
                "loss_percent": backup_loss,
                "role": backup_role,
                "degraded": require_degraded_backups,
            },
            "3": {
                "present": True,
                "loss_percent": backup_loss,
                "role": backup_role,
                "degraded": require_degraded_backups,
            },
        },
        "checks": {
            "anchor_loss_available": True,
            "anchor_path_loss_within_limit": True,
            "correct_anchor_selected": True,
            "recovery_inactive": True,
            "backup_paths_present": True,
            "backup_paths_degraded": True,
            "sustained_health_evidence": True,
        },
        "stabilization": {
            "achieved": True,
            "required_consecutive_updates": 8,
            "observed_unique_updates": 8,
            "ending_consecutive_valid_updates": 8,
            "maximum_consecutive_valid_updates": 8,
            "elapsed_seconds": 7.0,
            "observations": [
                {
                    "at_seconds": float(index),
                    "status_mtime_ns": index + 1,
                    "valid": True,
                    "anchor_path_id": 1,
                    "anchor_path_loss_percent": 0.0,
                    "recovery_active": False,
                    "backup_paths": {
                        "2": {
                            "present": True,
                            "loss_percent": backup_loss,
                            "role": backup_role,
                            "degraded": require_degraded_backups,
                        },
                        "3": {
                            "present": True,
                            "loss_percent": backup_loss,
                            "role": backup_role,
                            "degraded": require_degraded_backups,
                        },
                    },
                }
                for index in range(8)
            ],
        },
    }


def anchor_metrics():
    clean = baseline_measurement()
    impaired = baseline_measurement()
    impaired["selected_median_floor_mbps"] = 95.0
    impaired["selected_throughput"] = throughput(95.0, 115.0)
    impaired["attempts"][1] = attempt(2, 95.0, 95.0, 115.0)
    return {
        "stable_anchor_native_baseline": throughput(110.0, 120.0),
        "clean_tunnel_baseline_measurement": clean,
        "impaired_tunnel_measurement": impaired,
        "preconditions": {
            "valid": True,
            "failures": [],
            "clean_tunnel_baseline": anchor_precondition_stage(
                "before-clean-tunnel-baseline"
            ),
            "clean_tunnel_baseline_after": anchor_precondition_stage(
                "after-clean-tunnel-baseline"
            ),
            "impaired_tunnel_measurement": anchor_precondition_stage(
                "before-impaired-tunnel-measurement",
                require_degraded_backups=True,
            ),
            "impaired_tunnel_measurement_after": anchor_precondition_stage(
                "after-impaired-tunnel-measurement",
                require_degraded_backups=True,
            ),
        },
        "comparison_valid": True,
        "throughput": throughput(95.0, 115.0),
        "throughput_retained_ratio": 0.95,
    }


def result(scenario="soak"):
    metrics = {}
    if scenario == "soak":
        metrics = {
            "configured_duration_seconds": 1800,
            "samples": [
                {
                    "repair_cache_quiescence": {
                        "verified": True,
                        "waited_seconds": 3.25,
                        "client": {
                            "telemetry_available": True,
                            "authoritative_fields_present": True,
                            "metrics": {
                                "process.repair_cache.entries": 0,
                                "process.repair_cache.accounted_bytes": 0,
                            },
                            "missing_metrics": [],
                            "legacy_metrics": {},
                            "all_zero": True,
                        },
                        "server": {
                            "telemetry_available": True,
                            "authoritative_fields_present": True,
                            "metrics": {
                                "control_plane.repair_cache.entries": 0,
                                "control_plane.repair_cache.accounted_bytes": 0,
                            },
                            "missing_metrics": [],
                            "legacy_metrics": {},
                            "all_zero": True,
                        },
                    },
                    "processes": {
                        "client": {"memory_rollup_available": True},
                        "server": {"memory_rollup_available": True},
                    },
                }
            ],
            "baseline_measurement": baseline_measurement(),
            "rss_samples_complete": True,
            "rss_trends": {"client": {}, "server": {}},
            "rss_robust_steady_state": {
                "client": robust_memory_evidence(),
                "server": robust_memory_evidence(),
            },
            "memory_sample_coverage": {},
            "memory_trends": {},
            "memory_robust_steady_state": {},
            "quiescent_sample_phase": {
                "repair_cache_ttl_seconds": 3.0,
                "quiescent_delay_seconds": 3.25,
                "cache_quiescence_timeout_seconds": 6.0,
                "target_sample_period_seconds": 5.0,
                "all_samples_after_cache_ttl": True,
                "all_samples_cache_quiescent": True,
            },
        }
    elif scenario == "anchor-bad-backup":
        metrics = anchor_metrics()
    return {
        "schema_version": 1,
        "scenario": scenario,
        "status": "pass",
        "started_at": "2026-07-20T00:00:00Z",
        "finished_at": "2026-07-20T00:30:01Z",
        "metrics": metrics,
        "thresholds": {},
        "cleanup": {
            "namespaces_remaining": [],
            "processes_remaining": [],
            "process_groups_remaining": [],
            "namespace_pids_remaining": {},
            "qdisc_state_removed": True,
            "qdisc_remaining": [],
        },
        "provenance": {
            "git_commit": "a" * 40,
            "git_branch": "feature/xband-only-runtime",
            "git_dirty": False,
            "docker_context": "xeon-dev",
            "docker_host": "xeon-dev",
            "docker_engine_version": "1",
            "docker_kernel": "1",
            "docker_os": "linux",
            "image_id": "sha256:" + ("b" * 64),
            "image_digests": [],
            "lab_orchestrator_sha256": "c" * 64,
            "result_schema_sha256": "d" * 64,
            "lab_orchestrator_source_sha256": "c" * 64,
            "result_schema_source_sha256": "d" * 64,
            "lab_orchestrator_hash_matches_source": True,
            "result_schema_hash_matches_source": True,
            "client_binary_sha256": "e" * 64,
            "server_binary_sha256": "f" * 64,
        },
    }


class ResultSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)
        cls.validator = Draft202012Validator(schema)

    def validate_payload(self, payload):
        self.validator.validate(payload)
        validate_result_semantics(payload)

    def test_complete_soak_baseline_is_valid(self):
        self.validate_payload(result())

    def test_soak_without_baseline_evidence_is_invalid(self):
        payload = result()
        del payload["metrics"]["baseline_measurement"]
        with self.assertRaises(ValidationError):
            self.validator.validate(payload)

    def test_soak_quiescence_summary_must_match_runtime_evidence(self):
        payload = result()
        sample = payload["metrics"]["samples"][0]
        sample["repair_cache_quiescence"]["server"]["metrics"][
            "control_plane.repair_cache.accounted_bytes"
        ] = 64
        sample["repair_cache_quiescence"]["server"]["all_zero"] = False
        sample["repair_cache_quiescence"]["verified"] = False
        payload["metrics"]["quiescent_sample_phase"][
            "all_samples_cache_quiescent"
        ] = False
        payload["status"] = "fail"
        sample["processes"]["client"]["memory_rollup_available"] = False
        sample["processes"]["server"]["memory_rollup_available"] = False
        self.validate_payload(payload)

        sample["processes"]["client"]["memory_rollup_available"] = True
        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "sampled smaps before repair caches",
        ):
            validate_result_semantics(payload)

    def test_warmup_requires_validity_floor_and_throughput_evidence(self):
        missing_warmup = result()
        del missing_warmup["metrics"]["baseline_measurement"]["warmup"]
        with self.assertRaises(ValidationError):
            self.validator.validate(missing_warmup)

        inconsistent_floor = result()
        warmup = inconsistent_floor["metrics"]["baseline_measurement"]["warmup"]
        warmup["valid_complete"] = False
        with self.assertRaises(ValidationError):
            self.validator.validate(inconsistent_floor)

        inconsistent_validity = result()
        warmup = inconsistent_validity["metrics"]["baseline_measurement"]["warmup"]
        warmup["valid_complete"] = False
        warmup["floor_mbps"] = None
        with self.assertRaises(ValidationError):
            self.validator.validate(inconsistent_validity)

        failed_throughput = result()
        failed_throughput["metrics"]["baseline_measurement"]["warmup"][
            "throughput"
        ] = throughput(valid=False)
        with self.assertRaises(ValidationError):
            self.validator.validate(failed_throughput)

    def test_passing_soak_requires_valid_warmup_but_failed_result_can_record_one(self):
        payload = result()
        warmup = payload["metrics"]["baseline_measurement"]["warmup"]
        warmup.update(
            {
                "valid_complete": False,
                "floor_mbps": None,
                "throughput": throughput(valid=False),
            }
        )
        with self.assertRaises(ValidationError):
            self.validator.validate(payload)

        payload["status"] = "fail"
        self.validate_payload(payload)

    def test_valid_attempt_requires_floor_and_complete_iperf(self):
        missing_floor = result()
        del missing_floor["metrics"]["baseline_measurement"]["attempts"][0][
            "floor_mbps"
        ]
        with self.assertRaises(ValidationError):
            self.validator.validate(missing_floor)

        failed_iperf = result()
        failed_iperf["metrics"]["baseline_measurement"]["attempts"][0][
            "throughput"
        ] = throughput(valid=False)
        with self.assertRaises(ValidationError):
            self.validator.validate(failed_iperf)

        invalid_floor = result()
        measurement = invalid_floor["metrics"]["baseline_measurement"]
        measurement["attempt_count"] = 4
        measurement["attempts"] = [
            invalid_attempt(1),
            attempt(2, 90.0, 90.0, 110.0),
            attempt(3, 100.0, 100.0, 120.0),
            attempt(4, 105.0, 105.0, 125.0),
        ]
        measurement["attempts"][0]["floor_mbps"] = 0.0
        measurement["selected_attempt"] = 3
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid_floor)

        inconsistent_validity = result()
        measurement = inconsistent_validity["metrics"]["baseline_measurement"]
        measurement["attempt_count"] = 4
        measurement["attempts"] = [
            invalid_attempt(1),
            attempt(2, 90.0, 90.0, 110.0),
            attempt(3, 100.0, 100.0, 120.0),
            attempt(4, 105.0, 105.0, 125.0),
        ]
        measurement["attempts"][0]["throughput"] = throughput()
        measurement["selected_attempt"] = 3
        with self.assertRaises(ValidationError):
            self.validator.validate(inconsistent_validity)

    def test_attempt_and_valid_counts_match_attempt_evidence(self):
        payload = result()
        measurement = payload["metrics"]["baseline_measurement"]
        measurement["attempt_count"] = 4
        with self.assertRaises(ValidationError):
            self.validator.validate(payload)

        wrong_valid_count = result()
        wrong_valid_count["metrics"]["baseline_measurement"][
            "valid_attempt_count"
        ] = 2
        with self.assertRaises(ValidationError):
            self.validator.validate(wrong_valid_count)

        wrong_attempt_number = result()
        wrong_attempt_number["metrics"]["baseline_measurement"]["attempts"][1][
            "attempt"
        ] = 3
        with self.assertRaises(ValidationError):
            self.validator.validate(wrong_attempt_number)

    def test_complete_baseline_requires_exactly_three_valid_attempts(self):
        payload = result()
        measurement = payload["metrics"]["baseline_measurement"]
        measurement["attempt_count"] = 4
        measurement["valid_attempt_count"] = 3
        measurement["attempts"] = [
            invalid_attempt(1),
            attempt(2, 90.0, 90.0, 110.0),
            attempt(3, 100.0, 100.0, 120.0),
            attempt(4, 105.0, 105.0, 125.0),
        ]
        measurement["selected_attempt"] = 3
        self.validator.validate(payload)

        measurement["attempts"][0] = attempt(1, 80.0, 80.0, 95.0)
        measurement["valid_attempt_count"] = 4
        with self.assertRaises(ValidationError):
            self.validator.validate(payload)

    def test_complete_baseline_requires_selected_median_evidence(self):
        missing_attempt = result()
        missing_attempt["metrics"]["baseline_measurement"]["selected_attempt"] = None
        with self.assertRaises(ValidationError):
            self.validator.validate(missing_attempt)

        missing_floor = result()
        missing_floor["metrics"]["baseline_measurement"][
            "selected_median_floor_mbps"
        ] = None
        with self.assertRaises(ValidationError):
            self.validator.validate(missing_floor)

        invalid_throughput = result()
        invalid_throughput["metrics"]["baseline_measurement"][
            "selected_throughput"
        ] = throughput(valid=False)
        with self.assertRaises(ValidationError):
            self.validator.validate(invalid_throughput)

        selected_invalid_attempt = result()
        measurement = selected_invalid_attempt["metrics"]["baseline_measurement"]
        measurement["attempt_count"] = 4
        measurement["attempts"] = [
            invalid_attempt(1),
            attempt(2, 90.0, 90.0, 110.0),
            attempt(3, 100.0, 100.0, 120.0),
            attempt(4, 105.0, 105.0, 125.0),
        ]
        measurement["selected_attempt"] = 1
        with self.assertRaises(ValidationError):
            self.validator.validate(selected_invalid_attempt)

    def test_semantic_validator_requires_the_median_valid_attempt(self):
        payload = result()
        measurement = payload["metrics"]["baseline_measurement"]
        measurement["selected_attempt"] = 1
        measurement["selected_median_floor_mbps"] = 90.0
        measurement["selected_throughput"] = throughput(90.0, 110.0)

        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "selected_attempt is not the median",
        ):
            validate_result_semantics(payload)

    def test_semantic_validator_requires_selected_floor_to_match_attempt(self):
        payload = result()
        payload["metrics"]["baseline_measurement"][
            "selected_median_floor_mbps"
        ] = 99.0

        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "selected_median_floor_mbps does not match",
        ):
            validate_result_semantics(payload)

    def test_semantic_validator_requires_selected_throughput_to_match_attempt(self):
        payload = result()
        payload["metrics"]["baseline_measurement"][
            "selected_throughput"
        ] = throughput(105.0, 125.0)

        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "selected_throughput does not match",
        ):
            validate_result_semantics(payload)

    def test_short_soak_baseline_smoke_reaches_acceptance_logic(self):
        samples = [
            throughput(75.0, 90.0),
            throughput(90.0, 110.0),
            throughput(110.0, 130.0),
            throughput(100.0, 120.0),
        ]
        with patch("lab.basic_throughput", side_effect=samples):
            measurement = collect_soak_baseline(object())

        selected = measurement["selected_throughput"]
        self.assertTrue(soak_baseline_is_complete(measurement, selected))
        self.assertEqual(3, measurement["selected_attempt"])
        self.assertEqual(100.0, measurement["selected_median_floor_mbps"])

        payload = result()
        payload["metrics"]["baseline_measurement"] = measurement
        self.validate_payload(payload)

        measurement["warmup"]["valid_complete"] = False
        measurement["warmup"]["floor_mbps"] = None
        measurement["warmup"]["throughput"] = throughput(valid=False)
        self.assertFalse(soak_baseline_is_complete(measurement, selected))

    def test_anchor_result_requires_explicit_comparison_evidence(self):
        payload = result("anchor-bad-backup")
        self.validate_payload(payload)

        del payload["metrics"]["preconditions"]
        with self.assertRaises(ValidationError):
            self.validator.validate(payload)

    def test_anchor_invalid_preconditions_explicitly_fail(self):
        payload = result("anchor-bad-backup")
        payload["status"] = "fail"
        metrics = payload["metrics"]
        metrics["comparison_valid"] = False
        metrics["throughput_retained_ratio"] = None
        metrics["preconditions"]["valid"] = False
        metrics["preconditions"]["failures"] = [
            "before-impaired-tunnel-measurement:recovery_inactive"
        ]
        stage = metrics["preconditions"]["impaired_tunnel_measurement"]
        stage["valid"] = False
        stage["failures"] = ["recovery_inactive"]
        stage["recovery_active"] = True
        stage["checks"]["recovery_inactive"] = False
        self.validate_payload(payload)

        payload["status"] = "pass"
        with self.assertRaises(ValidationError):
            self.validator.validate(payload)

    def test_anchor_stabilization_requires_consecutive_valid_updates(self):
        observations = [
            {"valid": True},
            {"valid": True},
            {"valid": False},
            {"valid": True},
            {"valid": True},
            {"valid": True},
        ]
        evidence = anchor_stabilization_summary(
            observations,
            required_consecutive_updates=4,
            elapsed_seconds=6.0,
            achieved=False,
        )

        self.assertFalse(evidence["achieved"])
        self.assertEqual(3, evidence["ending_consecutive_valid_updates"])
        self.assertEqual(3, evidence["maximum_consecutive_valid_updates"])
        self.assertEqual(6, evidence["observed_unique_updates"])

    def test_anchor_stage_cannot_pass_without_sustained_health_evidence(self):
        payload = result("anchor-bad-backup")
        payload["status"] = "fail"
        metrics = payload["metrics"]
        metrics["comparison_valid"] = False
        metrics["throughput_retained_ratio"] = None
        preconditions = metrics["preconditions"]
        preconditions["valid"] = False
        preconditions["failures"] = [
            "after-impaired-tunnel-measurement:sustained_health_evidence"
        ]
        stage = preconditions["impaired_tunnel_measurement_after"]
        stage["valid"] = False
        stage["failures"] = ["sustained_health_evidence"]
        stage["checks"]["sustained_health_evidence"] = False
        stage["stabilization"].update(
            {
                "achieved": False,
                "ending_consecutive_valid_updates": 3,
                "maximum_consecutive_valid_updates": 3,
            }
        )
        for index, observation in enumerate(
            stage["stabilization"]["observations"]
        ):
            observation["valid"] = index >= 5

        self.validate_payload(payload)

    def test_anchor_stabilization_summary_must_match_observations(self):
        payload = result("anchor-bad-backup")
        stage = payload["metrics"]["preconditions"]["clean_tunnel_baseline"]
        stage["stabilization"]["observed_unique_updates"] = 7

        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "inconsistent stabilization observed_unique_updates",
        ):
            validate_result_semantics(payload)

    def test_anchor_ratio_must_match_selected_medians(self):
        payload = result("anchor-bad-backup")
        payload["metrics"]["throughput_retained_ratio"] = 0.94
        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "throughput_retained_ratio does not match",
        ):
            validate_result_semantics(payload)

    def test_zero_anchor_baseline_is_explicit_invalid_artifact(self):
        payload = result("anchor-bad-backup")
        payload["status"] = "fail"
        payload["reason"] = "invalid anchor comparison: clean-tunnel-baseline-zero"
        metrics = payload["metrics"]
        baseline = metrics["clean_tunnel_baseline_measurement"]
        baseline["attempts"] = [
            attempt(number, 0.0, 0.0, 0.0)
            for number in range(1, 4)
        ]
        baseline["selected_attempt"] = 2
        baseline["selected_median_floor_mbps"] = 0.0
        baseline["selected_throughput"] = throughput(0.0, 0.0)
        metrics["comparison_valid"] = False
        metrics["throughput_retained_ratio"] = None

        self.validate_payload(payload)

    def test_anchor_post_measurement_requires_both_degraded_backups(self):
        payload = result("anchor-bad-backup")
        payload["status"] = "fail"
        metrics = payload["metrics"]
        metrics["comparison_valid"] = False
        metrics["throughput_retained_ratio"] = None
        preconditions = metrics["preconditions"]
        preconditions["valid"] = False
        preconditions["failures"] = [
            "after-impaired-tunnel-measurement:backup_paths_present"
        ]
        stage = preconditions["impaired_tunnel_measurement_after"]
        stage["valid"] = False
        stage["failures"] = ["backup_paths_present"]
        stage["backup_paths"]["3"].update(
            {
                "present": False,
                "loss_percent": None,
                "role": None,
                "degraded": False,
            }
        )
        stage["checks"]["backup_paths_present"] = False
        stage["checks"]["backup_paths_degraded"] = False

        self.validate_payload(payload)

    def test_anchor_throughput_must_match_selected_impaired_sample(self):
        payload = result("anchor-bad-backup")
        payload["metrics"]["throughput"] = throughput(90.0, 110.0)
        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "throughput does not match",
        ):
            validate_result_semantics(payload)

    def test_theil_sen_slope_is_robust_to_one_outlier(self):
        timestamps = [0.0, 60.0, 120.0, 180.0, 240.0]
        values = [1000.0, 1000.0, 5000.0, 1000.0, 1000.0]
        self.assertEqual(0.0, theil_sen_slope_per_minute(timestamps, values))

    def test_robust_memory_evidence_records_required_windows(self):
        timestamps = [float(value) for value in range(0, 1201, 60)]
        values = [1000.0 + (stamp / 60) for stamp in timestamps]
        evidence = robust_steady_state_evidence(timestamps, values)

        self.assertAlmostEqual(
            1.0,
            evidence["final_half"]["theil_sen_slope_kib_per_minute"],
        )
        self.assertAlmostEqual(
            1.0,
            evidence["final_10_minutes"][
                "theil_sen_slope_kib_per_minute"
            ],
        )
        self.assertEqual(
            6.0,
            evidence["final_10_minutes"]["edge_median_growth_kib"],
        )

    def test_telemetry_subset_preserves_all_sender_lane_queue_fields(self):
        status = {
            "sender_lanes": [
                {
                    "path_id": 1,
                    "data": {
                        "depth": 2,
                        "peak_depth": 7,
                        "capacity": 8,
                        "oldest_age_ms": 4,
                        "enqueue_drops": 1,
                        "deadline_drops": 0,
                    },
                    "control": {
                        "depth": 1,
                        "peak_depth": 3,
                        "capacity": 5,
                        "replacements": 9,
                    },
                    "repair": {
                        "depth": 0,
                        "peak_depth": 2,
                        "capacity": 128,
                    },
                }
            ],
            "process": {
                "tun_packet_pool": {
                    "retained": 2,
                    "capacity": 8,
                    "discarded": 1,
                },
                "receive_payload_pool": {
                    "retained": 3,
                    "capacity": 16,
                    "discarded": 4,
                },
            },
            "misc": {"value": 42},
        }
        telemetry = telemetry_subset(status)

        self.assertEqual(2, telemetry["sender_lanes[0].data.depth"])
        self.assertEqual(7, telemetry["sender_lanes[0].data.peak_depth"])
        self.assertEqual(8, telemetry["sender_lanes[0].data.capacity"])
        self.assertEqual(3, telemetry["sender_lanes[0].control.peak_depth"])
        self.assertEqual(9, telemetry["sender_lanes[0].control.replacements"])
        self.assertEqual(2, telemetry["sender_lanes[0].repair.peak_depth"])
        self.assertEqual(2, telemetry["process.tun_packet_pool.retained"])
        self.assertEqual(8, telemetry["process.tun_packet_pool.capacity"])
        self.assertEqual(1, telemetry["process.tun_packet_pool.discarded"])
        self.assertEqual(3, telemetry["process.receive_payload_pool.retained"])
        self.assertEqual(4, telemetry["process.receive_payload_pool.discarded"])
        self.assertNotIn("misc.value", telemetry)

    def test_repair_cache_quiescence_requires_zero_runtime_state(self):
        client = {
            "process": {
                "repair_cache": {
                    "entries": 0,
                    "accounted_bytes": 0,
                },
            },
        }
        server = {
            "control_plane": {
                "repair_cache": {
                    "entries": 0,
                    "accounted_bytes": 0,
                    "byte_capacity": 1024,
                },
            },
        }
        evidence = repair_cache_quiescence_evidence(
            client,
            server,
            waited_seconds=3.25,
        )
        self.assertTrue(evidence["verified"])
        self.assertTrue(evidence["client"]["authoritative_fields_present"])
        self.assertTrue(evidence["server"]["authoritative_fields_present"])
        self.assertEqual([], evidence["client"]["missing_metrics"])
        self.assertEqual([], evidence["server"]["missing_metrics"])
        self.assertNotIn(
            "control_plane.repair_cache.byte_capacity",
            evidence["server"]["metrics"],
        )

        server["control_plane"]["repair_cache"]["accounted_bytes"] = 64
        self.assertFalse(
            repair_cache_quiescence_evidence(
                client,
                server,
                waited_seconds=3.25,
            )["verified"]
        )
        self.assertFalse(
            repair_cache_quiescence_evidence(
                {},
                server,
                waited_seconds=3.25,
            )["verified"]
        )

    def test_repair_cache_quiescence_rejects_legacy_metrics_without_accounting(self):
        client = {
            "repair_cache_entries": 0,
            "repair_cache_bytes": 0,
            "process": {"repair_cache": {"entries": 0}},
        }
        server = {
            "control_plane": {
                "repair_cache_entries": 0,
                "repair_cache_bytes": 0,
                "repair_cache": {"entries": 0},
            },
        }

        evidence = repair_cache_quiescence_evidence(
            client,
            server,
            waited_seconds=3.25,
        )

        self.assertFalse(evidence["verified"])
        self.assertFalse(evidence["client"]["telemetry_available"])
        self.assertFalse(evidence["server"]["telemetry_available"])
        self.assertEqual(
            ["process.repair_cache.accounted_bytes"],
            evidence["client"]["missing_metrics"],
        )
        self.assertEqual(
            ["control_plane.repair_cache.accounted_bytes"],
            evidence["server"]["missing_metrics"],
        )
        self.assertTrue(evidence["client"]["legacy_metrics"])
        self.assertTrue(evidence["server"]["legacy_metrics"])

    def test_repair_cache_quiescence_rejects_unrelated_suffix_metrics(self):
        client = {
            "diagnostics": {
                "process": {
                    "repair_cache": {
                        "entries": 0,
                        "accounted_bytes": 0,
                    },
                },
            },
        }
        server = {
            "diagnostics": {
                "control_plane": {
                    "repair_cache": {
                        "entries": 0,
                        "accounted_bytes": 0,
                    },
                },
            },
        }

        evidence = repair_cache_quiescence_evidence(
            client,
            server,
            waited_seconds=3.25,
        )

        self.assertFalse(evidence["verified"])
        self.assertEqual({}, evidence["client"]["metrics"])
        self.assertEqual({}, evidence["server"]["metrics"])

    def test_passing_soak_rejects_legacy_only_quiescence_evidence(self):
        payload = result()
        evidence = payload["metrics"]["samples"][0]["repair_cache_quiescence"]
        evidence["client"].update(
            {
                "telemetry_available": True,
                "authoritative_fields_present": True,
                "metrics": {},
                "missing_metrics": [],
                "legacy_metrics": {
                    "repair_cache_entries": 0,
                    "repair_cache_bytes": 0,
                },
                "all_zero": True,
            }
        )

        self.validator.validate(payload)
        with self.assertRaisesRegex(
            ResultSemanticValidationError,
            "quiescence does not match runtime telemetry",
        ):
            validate_result_semantics(payload)

    def test_counter_growth_accumulates_across_sender_generation_reset(self):
        samples = []
        for generation, data, control, repair in (
            (1, 0, 0, 0),
            (1, 2, 1, 3),
            (2, 0, 0, 0),
            (2, 1, 1, 1),
        ):
            samples.append(
                {
                    "at_seconds": float(len(samples)),
                    "client_status": {
                        "sender_lanes[0].path_id": 7,
                        "sender_lanes[0].socket_generation": generation,
                        "sender_lanes[0].data.total_drops": data,
                        "sender_lanes[0].control.total_drops": control,
                        "sender_lanes[0].repair.total_drops": repair,
                    },
                    "server_status": {},
                }
            )

        growth, complete, resets = counter_growth_by_key(
            samples,
            is_harmful_drop_counter,
        )

        self.assertTrue(complete)
        self.assertEqual(
            3,
            growth[
                "client_status:sender_lanes[path_id=7].data.total_drops"
            ],
        )
        self.assertEqual(
            2,
            growth[
                "client_status:sender_lanes[path_id=7].control.total_drops"
            ],
        )
        self.assertEqual(
            4,
            growth[
                "client_status:sender_lanes[path_id=7].repair.total_drops"
            ],
        )
        self.assertEqual(
            1,
            resets[
                "client_status:sender_lanes[path_id=7].data.total_drops"
            ],
        )

    def test_counter_growth_rejects_same_generation_regression(self):
        samples = [
            {
                "at_seconds": float(index),
                "client_status": {
                    "sender_lanes[0].path_id": 7,
                    "sender_lanes[0].socket_generation": 3,
                    "sender_lanes[0].data.total_drops": value,
                },
                "server_status": {},
            }
            for index, value in enumerate((5, 4, 5))
        ]

        growth, complete, resets = counter_growth_by_key(
            samples,
            is_harmful_drop_counter,
        )

        key = "client_status:sender_lanes[path_id=7].data.total_drops"
        self.assertFalse(complete)
        self.assertEqual(1, growth[key])
        self.assertEqual(0, resets[key])

    def test_counter_growth_is_stable_when_sender_lane_array_reorders(self):
        samples = [
            {
                "at_seconds": 0.0,
                "client_status": {
                    "sender_lanes[0].path_id": 7,
                    "sender_lanes[0].socket_generation": 1,
                    "sender_lanes[0].data.total_drops": 0,
                    "sender_lanes[1].path_id": 9,
                    "sender_lanes[1].socket_generation": 4,
                    "sender_lanes[1].data.total_drops": 0,
                },
                "server_status": {},
            },
            {
                "at_seconds": 1.0,
                "client_status": {
                    "sender_lanes[0].path_id": 9,
                    "sender_lanes[0].socket_generation": 4,
                    "sender_lanes[0].data.total_drops": 3,
                    "sender_lanes[1].path_id": 7,
                    "sender_lanes[1].socket_generation": 1,
                    "sender_lanes[1].data.total_drops": 2,
                },
                "server_status": {},
            },
        ]

        growth, complete, resets = counter_growth_by_key(
            samples,
            is_harmful_drop_counter,
        )

        self.assertTrue(complete)
        self.assertEqual(
            2,
            growth[
                "client_status:sender_lanes[path_id=7].data.total_drops"
            ],
        )
        self.assertEqual(
            3,
            growth[
                "client_status:sender_lanes[path_id=9].data.total_drops"
            ],
        )
        self.assertEqual(0, sum(resets.values()))

    def test_incomplete_baseline_with_full_evidence_is_valid(self):
        for valid_count in range(3):
            with self.subTest(valid_count=valid_count):
                payload = result()
                payload["status"] = "fail"
                measurement = payload["metrics"]["baseline_measurement"]
                attempts = [
                    (
                        attempt(number, 90.0 + number, 90.0 + number, 110.0)
                        if number <= valid_count
                        else invalid_attempt(number)
                    )
                    for number in range(1, 6)
                ]
                measurement.update(
                    {
                        "attempt_count": 5,
                        "valid_attempt_count": valid_count,
                        "attempts": attempts,
                        "complete": False,
                        "selected_attempt": None,
                        "selected_median_floor_mbps": None,
                        "selected_throughput": None,
                    }
                )
                self.validator.validate(payload)

    def test_non_soak_result_does_not_require_baseline_evidence(self):
        self.validator.validate(result("topology-smoke"))


if __name__ == "__main__":
    unittest.main()
