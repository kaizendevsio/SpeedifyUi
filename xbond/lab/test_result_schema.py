import json
import pathlib
import unittest
from unittest.mock import patch

from jsonschema import Draft202012Validator, ValidationError

from lab import (
    ResultSemanticValidationError,
    collect_soak_baseline,
    soak_baseline_is_complete,
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


def result(scenario="soak"):
    metrics = {}
    if scenario == "soak":
        metrics = {
            "configured_duration_seconds": 1800,
            "baseline_measurement": baseline_measurement(),
        }
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
