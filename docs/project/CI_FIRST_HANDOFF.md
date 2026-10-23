# CI First Handoff

## Current state

- Remote `main` is `ae6c86ce`. The slow live workflow now loads pinned mission data as well as the SDE; nightly `test_arc29_walk.py` failed because `sde_mission.epicArcs` was absent.
- CI run 574 (`39929853113`, SHA `ae6c86ce`) is active. Fixtures passed; Rust format/clippy, .NET build, Rust tests, Python, end-to-end, advisory Rust, and Windows were running at last check.
- Live-slow run `39929860762` was dispatched against `main` for `test_arc29_walk.py` to verify mission setup.
- Agent-test run `39929447455` is active against SHA `46b929af` for `test_login_to_character.py`. It uses the updated Unicode token value. Run `39927613179` checked the earlier incorrect byte-string value.
- CI run 573 (`39929438401`, SHA `46b929af`) failed in the fast live end-to-end job with `AUTHENTICATION_TOKEN_INVALID`: plain Python `str` encoded to `PyBytes`, which the RPC does not accept. The test now sends `m.WString(...)`; run 574 tests this change. Rust format/clippy, fixtures, Python, and Rust tests passed in run 573.
- Nightly run `39926148439` completed on SHA `622509c2` with 22 failed jobs (21 live/skill-training checks plus the summary). RPC fuzz, benchmarks, and Windows passed. The missing mission-data setup is fixed in `live-slow.yml`; remaining failures include observed gameplay/timing errors and need triage if they recur on the corrected workflow.
- Earlier agent-test run `39923041081` passed for `main` with a paper-doll RPC test, fast live files, and Rust tests; its log artifact was uploaded.
- Main push CI uses SHA-specific groups with cancellation disabled. Runs 556 and 562 stayed active across later pushes; Windows has a per-caller run ID group.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused parser tests passed, including timestamped/ANSI Actions logs.
- The DB-free .NET focused paper-doll test passed: 1 passed, 0 failed, 0 skipped.
- The updated underage live test file passed `python3 -m py_compile`; Actions is checking it. No local database or broad local suite was used.

## Next steps

1. Check agent-test 39929447455, live-slow 39929860762, and CI run 574.
2. Review any remaining nightly failures against the corrected setup.
3. Record final outcomes here before final handoff.
