#!/usr/bin/env python3
import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest

MODULE = Path(__file__).with_name("ci-status.py")
SPEC = importlib.util.spec_from_file_location("ci_status", MODULE)
ci_status = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ci_status)
AGENT_MODULE = Path(__file__).with_name("agent-test.py")
AGENT_SPEC = importlib.util.spec_from_file_location("agent_test", AGENT_MODULE)
agent_test = importlib.util.module_from_spec(AGENT_SPEC)
AGENT_SPEC.loader.exec_module(agent_test)
MODE_MODULE = Path(__file__).with_name("ci-mode.py")
MODE_SPEC = importlib.util.spec_from_file_location("ci_mode", MODE_MODULE)
ci_mode = importlib.util.module_from_spec(MODE_SPEC)
MODE_SPEC.loader.exec_module(ci_mode)


class FailureParsingTests(unittest.TestCase):
    def test_pytest_failure_name_and_section_tail(self):
        log = """=================================== FAILURES ===================================
________________ test_character_walk __________________
assert actual == expected
E AssertionError: state differs
=========================== short test summary info ============================
FAILED tests/test_walk.py::test_character_walk - AssertionError
"""
        name = "tests/test_walk.py::test_character_walk"
        self.assertEqual(ci_status.parse_test_failures(log), [name])
        self.assertIn("AssertionError", "\n".join(ci_status.failure_log_tail(log, name)))

    def test_dotnet_failure_name(self):
        log = "Failed Eve.Host.Tests.RpcFuzzTests.Hostile_method_is_refused [27 ms]\n"
        self.assertEqual(ci_status.parse_test_failures(log), ["Eve.Host.Tests.RpcFuzzTests.Hostile_method_is_refused"])

    def test_rust_failure_name_and_output_tail(self):
        log = """test eve_marshal::decoder_rejects_bad_tag ... FAILED
failures:

---- eve_marshal::decoder_rejects_bad_tag stdout ----
bad tag 99
failures:
    eve_marshal::decoder_rejects_bad_tag
"""
        name = "eve_marshal::decoder_rejects_bad_tag"
        self.assertEqual(ci_status.parse_test_failures(log), [name])
        self.assertIn("bad tag 99", "\n".join(ci_status.failure_log_tail(log, name)))

    def test_agent_report_name_and_tail(self):
        log = """Agent test summary
[agent-test failure] Skills.Training_speed
Expected 1200000, received 20.
  at TrainingSpeedTest.cs:42
"""
        name = "Skills.Training_speed"
        self.assertEqual(ci_status.parse_test_failures(log), [name])
        self.assertEqual(ci_status.failure_log_tail(log, name), [
            "Expected 1200000, received 20.", "  at TrainingSpeedTest.cs:42"
        ])

    def test_success_log_has_no_failure_names(self):
        self.assertEqual(ci_status.parse_test_failures("test ok ... ok\nPassed! 1, Failed: 0"), [])

    def test_trx_result_counts_and_test_message(self):
        report = Path(__file__).with_name("ci-status-sample.trx")
        report.write_text("""<TestRun><ResultSummary><Counters executed="1" passed="0" notExecuted="1" /></ResultSummary>
<Results><UnitTestResult outcome="Failed" testName="Example.TrainingSpeed"><Output><ErrorInfo><Message>expected fast speed</Message><StackTrace>at Test.cs:9</StackTrace></ErrorInfo></Output></UnitTestResult></Results></TestRun>""", encoding="utf-8")
        try:
            self.assertEqual(agent_test.trx_results(report), (1, 0, 1, [("Example.TrainingSpeed", "expected fast speed\nat Test.cs:9")]))
        finally:
            report.unlink(missing_ok=True)

    def test_live_selection_accepts_only_test_files(self):
        files, slow = agent_test.live_files_for("test_login_to_character.py")
        self.assertEqual(files, ["test_login_to_character.py"])
        self.assertFalse(slow)
        slow_files, slow = agent_test.live_files_for("slow")
        self.assertIn("test_login_to_character.py", slow_files)
        self.assertTrue(slow)
        with self.assertRaises(ValueError):
            agent_test.live_files_for("../ci-status.py")

    def test_trigger_modes_restore_nightly_and_reusable_events(self):
        with tempfile.TemporaryDirectory() as directory:
            workflows = Path(directory)
            paths = []
            for name in ("ci-nightly.yml", "live-slow.yml"):
                target = workflows / name
                shutil.copy2(ci_mode.WORKFLOWS / name, target)
                paths.append(target)
            for path in paths:
                ci_mode.rewrite(path, "manual")
            nightly = (workflows / "ci-nightly.yml").read_text(encoding="utf-8")
            live = (workflows / "live-slow.yml").read_text(encoding="utf-8")
            self.assertNotIn("  schedule:", nightly)
            self.assertIn("  workflow_dispatch:", nightly)
            self.assertIn("  workflow_call:\n    inputs:\n      test_file:", live)
            for path in paths:
                ci_mode.rewrite(path, "auto")
            nightly = (workflows / "ci-nightly.yml").read_text(encoding="utf-8")
            self.assertIn("- cron: '0 4 * * *'", nightly)
            self.assertFalse(ci_mode.rewrite(workflows / "ci-nightly.yml", "auto"))


if __name__ == "__main__":
    unittest.main()
