# CI First Handoff

## Current state

- Commit `42e9783d` is on `main`; required push CI run 562 (`39925475745`) is in progress.
- Agent-test run `39923041081` succeeded for `main`: the focused paper-doll RPC test, fast live files, and Rust workspace tests passed. Artifact `agent-test-logs-39923041081` exists (52,187 bytes).
- Previous push CI run 556 (`39922996473`, SHA `e8d7ed04`) failed only because `Handlers_return_reference_shapes` omitted the required character ID. The input correction is in `42e9783d`; all other Linux jobs and Windows passed in run 556.
- `wait` originally missed the test name because Actions prefixes each log line with a timestamp and ANSI codes. The pending change normalizes those prefixes and adds matching parser tests. Re-running `wait 39922996473` now prints the failing test name and its exception tail.
- Main push CI uses a SHA-specific group with cancellation disabled; run 556 stayed active after later pushes and its Windows job completed successfully. The reusable Windows workflow has a per-caller run ID group.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed, including timestamped/ANSI .NET and timestamped agent-test logs.
- The DB-free .NET focused test for the corrected input passed: 1 passed, 0 failed, 0 skipped. MSBuild/compiler servers were shut down afterward.
- The selected agent-test workflow completed end to end and uploaded its full logs. No broad local suite or database was used.

## Next steps

1. Commit the log-prefix parser fix and this note, fetch/merge the latest `origin/main`, then push.
2. Monitor run 562 and the new push run, inspect any red checks, and fix their root causes.
3. Record final run IDs and outcomes here before the final handoff.
