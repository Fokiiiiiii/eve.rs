#!/usr/bin/env python3
"""Check out a branch or abbreviated commit supplied to the agent-test workflow."""

from __future__ import annotations

import re
import subprocess
import sys


def git(*args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", *args],
        check=False,
        capture_output=True,
        text=True,
    )


def resolve_commit(ref: str) -> str:
    ref = ref.strip()
    if not ref:
        raise ValueError("the requested ref is empty")

    if re.fullmatch(r"[0-9a-fA-F]{7,40}", ref):
        matches = git("rev-parse", f"--disambiguate={ref}")
        if matches.returncode == 0:
            commits = []
            for object_id in matches.stdout.splitlines():
                kind = git("cat-file", "-t", object_id)
                if kind.returncode == 0 and kind.stdout.strip() == "commit":
                    commits.append(object_id)
            commits = sorted(set(commits))
            if len(commits) == 1:
                return commits[0]
            if len(commits) > 1:
                raise ValueError(f"the commit prefix {ref!r} is ambiguous")

    valid_branch = git("check-ref-format", "--branch", ref)
    if valid_branch.returncode == 0:
        candidates = (ref, f"refs/remotes/origin/{ref}", f"refs/tags/{ref}")
        for candidate in candidates:
            resolved = git(
                "rev-parse",
                "--verify",
                "--quiet",
                "--end-of-options",
                f"{candidate}^{{commit}}",
            )
            if resolved.returncode == 0:
                return resolved.stdout.strip()

    raise ValueError(f"could not resolve requested branch or commit {ref!r}")


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: resolve-agent-test-ref.py REF", file=sys.stderr)
        return 2
    try:
        commit = resolve_commit(sys.argv[1])
    except ValueError as error:
        print(error, file=sys.stderr)
        return 2
    checkout = git("checkout", "--detach", commit)
    if checkout.returncode != 0:
        sys.stderr.write(checkout.stderr)
        return checkout.returncode
    print(f"Checked out {commit}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
