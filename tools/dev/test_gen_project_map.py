#!/usr/bin/env python3
"""Focused tests for the deterministic project-map generator."""
from __future__ import annotations

import csv
from pathlib import Path
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))

from tools.dev.gen_project_map import MAP_REL, is_current, render_map  # noqa: E402


def write_csv(path: Path, fieldnames: list[str], rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


class ProjectMapTests(unittest.TestCase):
    def test_inventory_states_evidence_claims_and_staleness(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "dotnet/src/Eve.Services.Foo").mkdir(parents=True)
            (root / "dotnet/src/Eve.Services.Foo/Eve.Services.Foo.csproj").write_text("<Project />\n", encoding="utf-8")
            (root / "dotnet/tests/Eve.Services.Foo.Tests").mkdir(parents=True)
            (root / "dotnet/tests/Eve.Services.Foo.Tests/Eve.Services.Foo.Tests.csproj").write_text("<Project />\n", encoding="utf-8")
            (root / "dotnet/tests/Eve.Host.Tests").mkdir(parents=True)
            (root / "dotnet/tests/Eve.Host.Tests/FooHostTests.cs").write_text("// focused test\n", encoding="utf-8")
            (root / "crates/eve-space/src/scene").mkdir(parents=True)
            (root / "crates/eve-space/Cargo.toml").write_text("[package]\nname='eve-space'\n", encoding="utf-8")
            (root / "crates/eve-space/src/lib.rs").write_text("pub mod scene;\npub mod flight;\n", encoding="utf-8")
            (root / "crates/eve-space/src/flight.rs").write_text("// scene flight\n", encoding="utf-8")
            (root / "crates/eve-space/src/scene/mod.rs").write_text("pub mod movement;\n", encoding="utf-8")
            (root / "crates/eve-space/src/scene/movement.rs").write_text("// scene movement\n", encoding="utf-8")
            (root / "tools/dev").mkdir(parents=True)
            (root / "tools/dev/generate.py").write_text("print('tool')\n", encoding="utf-8")
            (root / "tools/dev/test_generate.py").write_text("# focused test\n", encoding="utf-8")

            audit = root / "docs/project/service-audit"
            write_csv(audit / "EVE_RS_COVERAGE.csv", ["row_id", "service", "method", "eve_rs_status", "status_evidence"], [
                {"row_id": "RPC-00001", "service": "Foo", "method": "Read", "eve_rs_status": "IMPLEMENTED-TESTED (dotnet/tests/Eve.Host.Tests/FooHostTests.cs:Foo_read)", "status_evidence": "Registered Host test; See docs/project/service-audit/DEEPCHECK_Foo.md."},
                {"row_id": "RPC-00002", "service": "Legacy", "method": "Removed", "eve_rs_status": "CLIENT-DEAD", "status_evidence": "No V24 client call."},
                {"row_id": "RPC-00003", "service": "Incomplete", "method": "Write", "eve_rs_status": "PARTIAL-TESTED", "status_evidence": "Persistence exists; force-field admission remains open."},
                {"row_id": "RPC-00004", "service": "TraceOnly", "method": "Read", "eve_rs_status": "IMPLEMENTED-TESTED", "status_evidence": "Registered Host test."},
            ])
            write_csv(audit / "CLIENT_CALL_MATRIX.csv", ["row_id", "row_type", "service"], [
                {"row_id": "RPC-00001", "row_type": "rpc", "service": "Foo"},
                {"row_id": "RPC-00002", "row_type": "rpc", "service": "Legacy"},
                {"row_id": "RPC-00003", "row_type": "rpc", "service": "Incomplete"},
                {"row_id": "RPC-00004", "row_type": "rpc", "service": "TraceOnly"},
                {"row_id": "SVC-00001", "row_type": "service-bind", "service": "BindOnly"},
            ])
            (audit / "CLIENT_VERIFIED.md").write_text(
                "| row | RPC | client | comparison | verdict |\n"
                "| --- | --- | --- | --- | --- |\n"
                "| RPC-00001 | Foo.Read | V24 client callsite | EveJS handler and Host test dotnet/tests/Eve.Host.Tests/FooHostTests.cs | MATCHES |\n"
                "| RPC-00004 | TraceOnly.Read | V24 client consumer | Registered Host regression | MATCH |\n",
                encoding="utf-8",
            )
            (audit / "DEEPCHECK_Foo.md").write_text("# Foo audit\n", encoding="utf-8")
            (root / "docs/project/TODO.md").write_text("# Map input A\n", encoding="utf-8")
            map_path = root / MAP_REL
            map_path.parent.mkdir(parents=True, exist_ok=True)
            map_path.write_text(
                "# eve.rs unit map\n<!-- MAP:START -->\n"
                "| Kind | Unit | State | Evidence | Owning doc | Open gaps | Claim |\n"
                "| --- | --- | --- | --- | --- | --- | --- |\n"
                "| Tool | tools/dev/generate.py | unknown | source | — | pending | agent-z |\n"
                "<!-- MAP:END -->\n",
                encoding="utf-8",
            )

            rendered = render_map(root)
            self.assertEqual(rendered, render_map(root))
            self.assertIn("| RPC family | Foo | verified-against-EveJS |", rendered)
            self.assertIn("| RPC family | Legacy | client-dead |", rendered)
            self.assertIn("| RPC family | Incomplete | partial-tested |", rendered)
            self.assertIn("| RPC family | TraceOnly | verified-against-client-trace |", rendered)
            self.assertIn("| RPC family | BindOnly | unverified |", rendered)
            self.assertIn("| Server project | dotnet/src/Eve.Services.Foo/Eve.Services.Foo.csproj | unknown |", rendered)
            self.assertIn("| Scene subsystem | crates/eve-space::scene::movement | unknown |", rendered)
            self.assertIn("| Tool | tools/dev/generate.py | unknown |", rendered)
            self.assertIn("[focused test](../../tools/dev/test_generate.py)", rendered)
            self.assertIn("agent-z |", rendered)

            (root / "docs/project/TODO.md").write_text("# Map input B\n", encoding="utf-8")
            updated = render_map(root)
            self.assertNotEqual(rendered, updated)
            map_path.write_text(rendered, encoding="utf-8")
            self.assertFalse(is_current(root))
            map_path.write_text(updated, encoding="utf-8")
            self.assertTrue(is_current(root))
            map_path.write_text(updated + "stale\n", encoding="utf-8")
            self.assertFalse(is_current(root))
            claimed_missing_unit = updated.replace("| Tool | tools/dev/generate.py |", "| Tool | tools/dev/removed.py |", 1)
            map_path.write_text(claimed_missing_unit, encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "claimed unit no longer exists"):
                render_map(root)


if __name__ == "__main__":
    unittest.main()
