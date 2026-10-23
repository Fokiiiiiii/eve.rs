# CI First Handoff

## Current state

- Remote `main` is `f28b84ac`. Run 564 (`39925943103`, SHA `f28b84ac`) is active. Rust format/clippy, advisory stable Rust, Python client, and fixtures have passed; .NET build, live, Rust tests, and Windows are still running.
- Required CI run 562 (`39925475745`, SHA `42e9783d`) remains active after the later push of `f28b84ac`. Rust format/clippy, Rust tests, .NET build, Python, and fixtures have passed; live, Windows, and .NET shards remain active.
- Agent-test run `39923041081` succeeded for `main`: focused paper-doll RPC test, fast live files, and Rust workspace tests passed. Artifact `agent-test-logs-39923041081` exists (52,187 bytes).
- Run 550 (`39920654219`, SHA `e7c06a86`) exposed a `GetMyPaperDollData` test passing `characterId + 1`; that was corrected in `e8d7ed04`. Run 556 (`39922996473`, SHA `e8d7ed04`) then exposed `Handlers_return_reference_shapes` omitting the required `GetPaperDollData` ID; that was corrected in `42e9783d`. All other Linux jobs and Windows passed in run 556.
- GitHub timestamps and ANSI codes initially hid failed test names from `wait`. The parser fix in `f28b84ac` normalizes them; running `wait 39922996473` now prints `Eve.Host.Tests.CompatibilityRpcServiceTests.Handlers_return_reference_shapes` and its exception tail.
- Main push CI uses a SHA-specific group with cancellation disabled. Run 562 stayed active after later main pushes. The reusable Windows workflow has a per-caller run ID group.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed, including timestamped/ANSI .NET and timestamped agent-test logs.
- The DB-free .NET focused test for the corrected input passed: 1 passed, 0 failed, 0 skipped. MSBuild/compiler servers were shut down afterward.
- The selected agent-test workflow completed end to end and uploaded its full logs. No broad local suite or database was used.

## Next steps

1. Monitor runs 562 and 564 through completion.
2. Inspect any red checks and fix their root causes; confirm the required gate is green.
3. Record final run IDs and outcomes here before the final handoff.
