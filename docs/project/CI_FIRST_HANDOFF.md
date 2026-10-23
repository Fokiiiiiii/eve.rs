# CI First Handoff

## Current state

- Remote `main` is `32d87282`. This includes the fix in `tools/headless-client/tests/test_login_to_character.py`: `AmUnderage` is now called with a non-empty JWT-shaped test value. V24.01 calls it with `connection.GetLoginJWT()`, and the eve.rs RPC rejects an empty token field.
- Run 569 (`39927500454`, SHA `32d87282`) was queued at the last check.
- Agent-test run `39927613179` was dispatched against `main` for the focused `test_login_to_character.py` live file; waiting for its result.
- Required runs 562 (`39925475745`) and 564 (`39925943103`) both failed only in their fast live end-to-end job on the empty-token call above. All Rust, .NET test shards, Windows, Python, and fixture jobs in those runs passed. The gate failed in run 562 and was still queued in run 564 at the last check.
- Nightly run `39926148439` was manually dispatched against `main`. It runs the slow live matrix, isolated skill-training-speed Host, benchmarks, Windows, and extended RPC fuzz. Seven jobs had failed at the last check; logs were not yet available while the run continued.
- Earlier agent-test run `39923041081` passed for `main` with a focused paper-doll RPC test, fast live files, and Rust workspace tests. Its full log artifact was uploaded.
- Main push CI uses a SHA-specific concurrency group with cancellation disabled. Runs 556 and 562 remained active across later pushes; Windows uses a per-caller run ID group.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused tests passed, including timestamped/ANSI Actions log parsing.
- The DB-free .NET focused test for the corrected paper-doll input passed: 1 passed, 0 failed, 0 skipped.
- The authentication test source compiles with `python3 -m py_compile`; its live integration test is being checked through agent-test Actions. No local database or broad local suite was used.

## Next steps

1. Check agent-test run 39927613179 and push CI run 569.
2. Once nightly run 39926148439 completes, inspect each failed job's test names and log tails and fix root causes.
3. Record final run IDs and outcomes here before final handoff.
