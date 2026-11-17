#!/usr/bin/env python3
"""Unit tests for tools/dev/feature-gate's evidence checks and report contract."""

from __future__ import annotations

from contextlib import redirect_stdout, redirect_stderr
import importlib.machinery
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).with_name("feature-gate")
LOADER = importlib.machinery.SourceFileLoader("feature_gate", str(SCRIPT))
SPEC = importlib.util.spec_from_loader(LOADER.name, LOADER)
gate = importlib.util.module_from_spec(SPEC)
LOADER.exec_module(gate)


class RpcRegistrationTests(unittest.TestCase):
    def test_requires_every_configured_client_method_in_the_active_host_table(self):
        scope = [{"service": "ship", "methods": ["FitShips", "Scoop"]}]
        rows = [
            {"row_type": "rpc", "service": "ship", "method": "FitShips", "client_file_line": "fitting.py:10"},
            {"row_type": "rpc", "service": "ship", "method": "Scoop", "client_file_line": "menusvc.py:20"},
        ]

        result = gate.check_rpc_registration(scope, rows, {("ship", "FitShips")})

        self.assertEqual("fail", result["status"])
        self.assertEqual(["ship.Scoop"], result["details"]["missing_host_methods"])
        self.assertIn("menusvc.py:20", result["summary"])

    def test_whole_service_scope_includes_new_client_inventory_methods(self):
        scope = [{"service": "ship", "all_client_methods": True}]
        rows = [
            {"row_type": "rpc", "service": "ship", "method": "FitShips", "client_file_line": "fitting.py:10"},
            {"row_type": "rpc", "service": "ship", "method": "Scoop", "client_file_line": "menusvc.py:20"},
            {"row_type": "notification", "service": "ship", "method": "OnShip", "client_file_line": "ship.py:4"},
        ]

        result = gate.check_rpc_registration(scope, rows, {("ship", "FitShips"), ("ship", "Scoop")})

        self.assertEqual("pass", result["status"])
        self.assertEqual(2, result["details"]["rpc_count"])

    def test_explicit_bound_accessor_callsite_is_checked_against_client_source(self):
        with tempfile.TemporaryDirectory() as directory:
            client = Path(directory)
            source = client / "eve/client/fittingSvc.py"
            source.parent.mkdir(parents=True)
            source.write_text("shipAccess.FitShips(shipTypeID=601)\n", encoding="utf-8")
            scope = [{
                "service": "ship",
                "methods": ["FitShips"],
                "client_callsites": {"FitShips": ["eve/client/fittingSvc.py:1"]},
            }]

            passed = gate.check_rpc_registration(scope, [], {("ship", "FitShips")}, client)
            source.write_text("shipAccess.OtherCall()\n", encoding="utf-8")
            stale = gate.check_rpc_registration(scope, [], {("ship", "FitShips")}, client)

            self.assertEqual("pass", passed["status"])
            self.assertEqual("fail", stale["status"])
            self.assertIn("callsite references no longer match", stale["summary"])


class HostEvidenceTests(unittest.TestCase):
    def test_requires_postgres_registration_wire_state_notifications_and_a_non_skipped_run(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            test_file = root / "ShipTests.cs"
            test_file.write_text(
                """[PgFact]\npublic async Task Scoop_registered() {\n"
                "await using var rig = await Rig.CreateAsync();\n"
                "Assert.Same(PyNone.Instance, await rig.RegisteredCallAsync(session, bound, \"Scoop\"));\n"
                "var item = await rig.Items.GetAsync(itemId);\n"
                "Assert.Equal(\"OnItemChange\", Assert.Single(Events.Of(session)).Name);\n}\n""",
                encoding="utf-8",
            )
            harness = root / "Rig.cs"
            harness.write_text("Registry.Register(Ship); RegisteredCallAsync(ClientSession session)", encoding="utf-8")
            postgres = root / "Pg.cs"
            postgres.write_text("EVE_PG_URL; NpgsqlDataSource", encoding="utf-8")
            evidence = {
                "project": "Space.Tests",
                "selected_projects": ["Space.Tests", "Host.Tests"],
                "test_file": "ShipTests.cs",
                "test_name": "Scoop_registered",
                "required_attribute": "[PgFact]",
                "required_body_markers": ["RegisteredCallAsync", "PyNone.Instance", "rig.Items.GetAsync", "Events.Of(session)", "OnItemChange"],
                "registration_harness_file": "Rig.cs",
                "required_harness_markers": ["Registry.Register(Ship)", "RegisteredCallAsync(ClientSession session)"],
                "postgres_harness_file": "Pg.cs",
                "required_postgres_markers": ["EVE_PG_URL", "NpgsqlDataSource"],
            }
            results = {
                "Space.Tests": {"executed": 1, "skipped": 0, "outcomes": {"Scoop_registered": "passed"}},
                "Host.Tests": {"executed": 1, "skipped": 0, "outcomes": {}},
            }

            passed = gate.check_host_evidence(root, evidence, results)
            self.assertEqual("pass", passed["status"])

            skipped = gate.check_host_evidence(root, evidence, {
                "Space.Tests": {"executed": 1, "skipped": 0, "outcomes": {"Scoop_registered": "passed"}},
                "Host.Tests": {"executed": 1, "skipped": 1, "outcomes": {}},
            })
            self.assertEqual("fail", skipped["status"])
            self.assertIn("skipped", skipped["summary"])

    def test_missing_wire_or_notification_assertions_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "ShipTests.cs").write_text(
                "[PgFact]\npublic async Task Scoop_registered() { await rig.RegisteredCallAsync(); }",
                encoding="utf-8",
            )
            (root / "Rig.cs").write_text("Registry.Register(Ship); RegisteredCallAsync(ClientSession session)", encoding="utf-8")
            (root / "Pg.cs").write_text("EVE_PG_URL; NpgsqlDataSource", encoding="utf-8")
            evidence = {
                "project": "Space.Tests", "test_file": "ShipTests.cs", "test_name": "Scoop_registered",
                "selected_projects": ["Space.Tests"],
                "required_attribute": "[PgFact]", "required_body_markers": ["PyNone.Instance", "Events.Of(session)"],
                "registration_harness_file": "Rig.cs", "required_harness_markers": ["Registry.Register(Ship)"],
                "postgres_harness_file": "Pg.cs", "required_postgres_markers": ["EVE_PG_URL", "NpgsqlDataSource"],
            }

            result = gate.check_host_evidence(root, evidence, {"Space.Tests": {
                "executed": 1, "skipped": 0, "outcomes": {"Scoop_registered": "passed"}
            }})

            self.assertEqual("fail", result["status"])
            self.assertIn("PyNone.Instance", result["summary"])


class ErrorInventoryTests(unittest.TestCase):
    def test_partial_error_inventory_stays_failed_even_when_known_cases_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            client = root / "client/eve.py"
            client.parent.mkdir(parents=True)
            client.write_text("ship.Scoop()\n", encoding="utf-8")
            test = root / "ShipTests.cs"
            test.write_text(
                "[Fact]\npublic async Task Scoop_denied() {\n"
                "var error = await Assert.ThrowsAsync<MachoWrappedException>(() => call());\n"
                "Assert.Equal(\"SCOOP_DENIED\", error.Error.UserErrorMessage);\n}\n",
                encoding="utf-8",
            )
            inventory = {
                "complete": False,
                "review_record": "review.md",
                "incomplete_reason": "not exhaustive",
                "cases": [{
                    "method": "Scoop", "test_file": "ShipTests.cs", "test_name": "Scoop_denied",
                    "project": "Space.Tests", "client_callsite": "client/eve.py:1", "expected_error": "SCOOP_DENIED",
                }],
            }
            results = {"Space.Tests": {"outcomes": {"Scoop_denied": "passed"}}}

            result = gate.check_error_inventory(root, root, inventory, results)

            self.assertEqual("fail", result["status"])
            self.assertEqual(1, result["details"]["verified_cases"])
            self.assertIn("not exhaustive", result["summary"])


class ReplayAndDeviationTests(unittest.TestCase):
    def test_missing_area_replay_is_a_failure(self):
        result = gate.run_replay_cases(Path("."), "ship-rpcs", {"replay_cases": []}, {})
        self.assertEqual("fail", result["status"])
        self.assertIn("No area-specific", result["summary"])

    def test_deviation_requires_a_reason_in_the_matching_section(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            deviations = root / "docs/reference/compat/DEVIATIONS.md"
            deviations.parent.mkdir(parents=True)
            deviations.write_text(
                "## Difference\nA reason is documented.\n\n## Another difference\nIts own reason is documented.\n",
                encoding="utf-8",
            )
            record = root / "docs/project/record.md"
            record.parent.mkdir(parents=True)
            record.write_text("comparison", encoding="utf-8")
            review = {
                "complete": True,
                "review_record": "docs/project/record.md",
                "entries": [
                    {"heading": "## Difference", "reason_marker": "A reason is documented."},
                    {"heading": "## Another difference", "reason_marker": "Its own reason is documented."},
                ],
            }

            self.assertEqual("pass", gate.check_deviations(root, review)["status"])
            review["entries"][1]["reason_marker"] = "missing reason"
            self.assertEqual("fail", gate.check_deviations(root, review)["status"])

    def test_empty_deviation_list_needs_an_explicit_none_known_attestation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            deviations = root / "docs/reference/compat/DEVIATIONS.md"
            deviations.parent.mkdir(parents=True)
            deviations.write_text("# Deviations\n", encoding="utf-8")
            record = root / "docs/project/record.md"
            record.parent.mkdir(parents=True)
            record.write_text("review", encoding="utf-8")
            review = {"complete": True, "review_record": "docs/project/record.md", "entries": []}

            self.assertEqual("fail", gate.check_deviations(root, review)["status"])
            review["none_known"] = True
            self.assertEqual("pass", gate.check_deviations(root, review)["status"])


class ReportContractTests(unittest.TestCase):
    def test_any_failed_item_returns_nonzero_and_emits_machine_readable_json(self):
        report = {"area": "ship-rpcs", "overall": "fail", "checks": [
            {"status": "pass", "summary": "one passed"},
            {"status": "fail", "summary": "one failed"},
        ]}
        stdout = io.StringIO()
        stderr = io.StringIO()
        with patch.object(gate, "AREAS_PATH", Path("areas.json")), \
             patch.object(gate, "ROOT", Path(".")), \
             patch.object(gate, "load_json", return_value={"ship-rpcs": {}}), \
             patch.object(gate, "build_report", return_value=report), \
             redirect_stdout(stdout), redirect_stderr(stderr):
            status = gate.main(["ship-rpcs"])

        self.assertEqual(1, status)
        self.assertEqual(report, json.loads(stdout.getvalue()))
        self.assertIn("Feature gate ship-rpcs: FAIL", stderr.getvalue())


if __name__ == "__main__":
    unittest.main()
