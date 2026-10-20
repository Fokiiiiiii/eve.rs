#!/usr/bin/env python3
"""Switch GitHub Actions workflows between automatic and manual-only triggers."""

from __future__ import annotations

import argparse
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"
# Keep the automatic trigger policy here so manual mode never loses it.
AUTOMATIC_SCHEDULES = {
    "ci.yml": "  schedule:\n    - cron: '0 6 * * 1'\n",
    "ci-windows.yml": "  schedule:\n    - cron: '0 */6 * * *'\n",
}
AUTOMATIC_PUSH_WORKFLOWS = {"ci.yml"}
PUSH_TRIGGERS = {
    "ci.yml": "  push:\n    branches: [main]\n    paths-ignore:\n      - 'docs/**'\n      - '**/*.md'\n      - '.github/dependabot.yml'\n",
}
REUSABLE_EVENTS = {"ci-windows.yml": "  workflow_call:\n"}

def split_workflow(text: str) -> tuple[str, str, str, str]:
    """Return prefix, trigger header, dispatch event and suffix for a workflow."""
    lines = text.splitlines(keepends=True)
    start = next((i for i, line in enumerate(lines) if re.fullmatch(r"(?:'on'|\"on\"|on):\s*(?:\r?\n)?", line)), None)
    if start is None:
        raise ValueError("workflow has no top-level on: block")
    end = next((i for i in range(start + 1, len(lines)) if lines[i].strip() and not lines[i][0].isspace() and not lines[i].startswith("#")), len(lines))
    block = lines[start:end]
    header = block[0]
    events: list[list[str]] = []
    for line in block[1:]:
        if line.startswith("  ") and not line.startswith("    ") and re.match(r"  [A-Za-z_][\w-]*:\s*(?:#.*)?(?:\r?\n)?$", line):
            events.append([line])
        elif events:
            events[-1].append(line)
        elif line.strip():
            raise ValueError("unexpected content in workflow trigger block")
    dispatch = next(("".join(event) for event in events if re.match(r"  workflow_dispatch:", event[0])), "")
    if not dispatch:
        raise ValueError("workflow has no workflow_dispatch trigger")
    return "".join(lines[:start]), header, dispatch, "".join(lines[end:])

def rewrite(path: Path, mode: str) -> bool:
    original = path.read_text(encoding="utf-8")
    prefix, header, dispatch, suffix = split_workflow(original)
    reusable = REUSABLE_EVENTS.get(path.name, "")
    if mode == "manual":
        new_block = header + reusable + dispatch
    else:
        push = PUSH_TRIGGERS.get(path.name, "")
        schedule = AUTOMATIC_SCHEDULES.get(path.name, "")
        new_block = header + push + schedule + reusable + dispatch
    updated = prefix + new_block + suffix
    if updated != original:
        path.write_text(updated, encoding="utf-8", newline="")
        return True
    return False

def current_mode(paths: list[Path]) -> str:
    modes = set()
    for path in paths:
        if path.name not in AUTOMATIC_PUSH_WORKFLOWS:
            continue
        lines = path.read_text(encoding="utf-8").splitlines()
        start = next(i for i, line in enumerate(lines) if re.fullmatch(r"(?:'on'|\"on\"|on):\s*", line))
        end = next((i for i in range(start + 1, len(lines)) if lines[i].strip() and not lines[i][0].isspace() and not lines[i].startswith("#")), len(lines))
        events = [line.strip().split(":", 1)[0] for line in lines[start + 1:end] if re.match(r"  [A-Za-z_][\w-]*:", line)]
        is_auto = "push" in events and "schedule" in events
        modes.add("auto" if is_auto else "manual")
    return next(iter(modes)) if len(modes) == 1 else "mixed"

def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", nargs="?", choices=("manual", "auto"))
    parser.add_argument("--check", action="store_true", help="print the current trigger mode without changing files")
    args = parser.parse_args()
    paths = sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml"))
    if not paths:
        print("no workflow files found", file=sys.stderr)
        return 2
    if args.check:
        print(current_mode(paths))
        return 0
    if args.mode is None:
        parser.error("mode is required unless --check is used")
    changed = [str(path.relative_to(ROOT)) for path in paths if rewrite(path, args.mode)]
    print(f"{args.mode}: updated {len(changed)} workflow(s)" if changed else f"{args.mode}: already current")
    return 0

if __name__ == "__main__":
    raise SystemExit(main())
