"""Regression tests for CI trigger mode rewriting."""

import importlib.util
from pathlib import Path
import shutil
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[2] / "dev" / "ci-mode.py"
spec = importlib.util.spec_from_file_location("ci_mode", SCRIPT)
assert spec and spec.loader
ci_mode = importlib.util.module_from_spec(spec)
spec.loader.exec_module(ci_mode)


class CiModeTests(unittest.TestCase):
    def test_both_modes_are_idempotent_and_keep_workflow_content(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workflows = root / ".github" / "workflows"
            workflows.mkdir(parents=True)
            for name in ("ci.yml", "live-slow.yml"):
                shutil.copy2(ci_mode.WORKFLOWS / name, workflows / name)
            paths = sorted(workflows.glob("*.yml"))
            originals = {path.name: path.read_text(encoding="utf-8") for path in paths}
            for mode in ("manual", "auto"):
                for path in paths:
                    ci_mode.rewrite(path, mode)
                once = {path.name: path.read_text(encoding="utf-8") for path in paths}
                for path in paths:
                    self.assertFalse(ci_mode.rewrite(path, mode))
                self.assertEqual(once, {path.name: path.read_text(encoding="utf-8") for path in paths})
                for path in paths:
                    text = path.read_text(encoding="utf-8")
                    self.assertIn("workflow_dispatch:", text)
                    self.assertIn("jobs:", text)
                    self.assertIn("Trigger mode is set by tools/dev/ci-mode.py", text)
                    if mode == "manual":
                        lines = text.splitlines()
                        start = next(i for i, line in enumerate(lines) if line.strip() in ("on:", "'on':", '\"on\":'))
                        end = next(i for i in range(start + 1, len(lines)) if lines[i].strip() and not lines[i][0].isspace() and not lines[i].startswith("#"))
                        trigger = "\n".join(lines[start:end])
                        self.assertNotRegex(trigger, r"(?m)^  (push|schedule|pull_request):")
                    elif path.name in ci_mode.AUTOMATIC_PUSH_WORKFLOWS:
                        self.assertIn("  push:", text)
                    else:
                        self.assertNotRegex(text, r"(?m)^  (push|schedule):")
                if mode == "auto":
                    self.assertIn("- cron: '0 6 * * 1'", (workflows / "ci.yml").read_text(encoding="utf-8"))
            self.assertIn("The nightly schedule is switched off", originals["live-slow.yml"])


if __name__ == "__main__":
    unittest.main()
