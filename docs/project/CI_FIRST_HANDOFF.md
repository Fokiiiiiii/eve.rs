# CI First Handoff

## Current state

- Remote `main` is `46b929af`. The underage live test now sends `m.WString(...)`: the headless codec maps plain Python `str` to `PyBytes`, while the RPC accepts the client token as `PyStr` or `PyWString`.
- CI run 573 (`39929438401`, SHA `46b929af`) is active. Rust format/clippy, fixtures, and Python passed; .NET build, end-to-end, Rust tests, advisory Rust, and Windows were running at last check.
- Agent-test run `39929447455` was dispatched against current `main` for `test_login_to_character.py`; it was queued at last check. Earlier run `39927613179` uses SHA `32d87282` and the first, incorrect `PyBytes` test value.
- CI run 569 (`39927500454`, SHA `32d87282`) failed in the fast live end-to-end job with `AUTHENTICATION_TOKEN_INVALID`; the value was a plain Python `str` encoded as `PyBytes`. Run 564 had the same empty-token failure. All other jobs checked in those runs passed.
- Nightly run `39926148439` completed on SHA `622509c2` with 22 failed jobs (21 live/skill-training checks plus the summary). Extended RPC fuzz, benchmarks, and Windows passed. Its tests ran before the token-wire fix; inspect failure tails to classify remaining slow-test failures.
- Earlier agent-test run `39923041081` passed for `main` with a paper-doll RPC test, fast live files, and Rust tests; its log artifact was uploaded.
- Main push CI uses a SHA-specific concurrency group with cancellation disabled. Runs 556 and 562 stayed active across later pushes; Windows uses a per-caller run ID group.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed, including timestamped/ANSI Actions log parsing.
- The DB-free .NET focused paper-doll test passed: 1 passed, 0 failed, 0 skipped.
- The underage test file passes `python3 -m py_compile`; live integration verification is running on Actions. No local database or broad local suite was used.

## Next steps

1. Check agent-test run 39929447455 and push CI run 573.
2. Inspect nightly run 39926148439 failed-job test names and log tails; fix in-scope root causes.
3. Record final run IDs and outcomes here before final handoff.
