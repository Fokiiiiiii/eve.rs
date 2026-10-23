# CI First Handoff

## Current state

- Remote `main` is `622509c2`, a handoff-only update following CI run `f28b84ac`.
- CI run 564 (`39925943103`, SHA `f28b84ac`) was active at the last poll. Rust format/clippy, advisory stable Rust, Python client, and fixtures had passed; .NET build, live, Rust tests, and Windows were still running.
- Required CI run 562 (`39925475745`, SHA `42e9783d`) remains active after later main pushes. Rust format/clippy, Rust tests, .NET build, Python, and fixtures passed; live, Windows, and .NET shards remained active.
- Nightly extended CI was manually dispatched against `main` as run `39926148439`; it exercises slow live tests, the isolated skill-training-speed Host, benchmarks, Windows, and extended RPC fuzz.
- Agent-test run `39923041081` succeeded for `main`: focused paper-doll RPC test, fast live files, and Rust workspace tests passed. Artifact `agent-test-logs-39923041081` exists (52,187 bytes).
- Run 550 (`39920654219`, SHA `e7c06a86`) exposed a `GetMyPaperDollData` test passing `characterId + 1`; corrected in `e8d7ed04`. Run 556 (`39922996473`, SHA `e8d7ed04`) exposed `Handlers_return_reference_shapes` omitting the required `GetPaperDollData` ID; corrected in `42e9783d`.
- The parser fix in `f28b84ac` normalizes GitHub timestamps and ANSI codes. `wait 39922996473` now prints the failed test name and exception tail.
- Main push CI uses a SHA-specific group with cancellation disabled. Run 562 stayed active after later main pushes. The reusable Windows workflow has a per-caller run ID group.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed, including timestamped/ANSI .NET and timestamped agent-test logs.
- The DB-free .NET focused test for the corrected input passed: 1 passed, 0 failed, 0 skipped. MSBuild/compiler servers were shut down afterward.
- Agent-test completed end to end and uploaded its full logs. No broad local suite or database was used.

## Next steps

1. Monitor runs 562, 564, and nightly run 39926148439 through completion.
2. Inspect red checks and fix their root causes; confirm the required push gate is green.
3. Record final run IDs and outcomes here before final handoff.
