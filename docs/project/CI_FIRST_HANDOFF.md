# CI First Handoff

## Current state

- Remote `main` is `8d2c901c`; it contains candidate `dd223b9f). The concurrent `.NET DispatchGoldenTests` conflict was resolved with the latest main-side mock registrations.
- Agent-test run `39931135089` succeeded on `dd223b9f). It was dispatched with project `Eve.Services.Tests`, filter `Market_owner_history_reads_have_a_named_per_session_budget`, live test `test_login_to_character.py`, and Rust tests. The workflow returned a run ID and action URL.
- Push CI run `39931188008` succeeded on `0d2a7312`, including end-to-end, Windows and the required gate. Run `39930594371` also succeeded after the candidate fix; its .NET other-tests shard 1/3 passed.
- Later main runs `39931680738` (`beaa821b`), `39932084783` (`8baa4ba6`), `39932448507` (`f7646151`), and `39932859826` (`0c062d6d`) remain active. At last check, run `39931680738` had all Linux jobs, including end-to-end and .NET shards, passed while Windows was active. Runs `39932084783` and `39932448507` had successful setup/build/Rust/Python work; remaining end-to-end, Windows, or test shards were active. Main pushes continued while earlier runs remained active, confirming they were not cancelled by later commits.
- `python3 tools/dev/ci-status.py failures ae6c86ce` printed the old failed test name and log tail: `Market_owner_history_reads_have_a_named_per_session_budget` expected 5 calls and observed 4. The mock registration was corrected; the focused local regression and later Actions checks passed.
- Local focused .NET regression: 1 passed, 0 failed, 0 skipped. No local database or broad local suite was used.
- Nightly run `39926148439` completed with 22 failed jobs (21 slow-live/skill-training checks plus the summary). RPC fuzz, throughput benchmarks and full Windows passed. The workflow reported failures separately from the push gate.
- Slow-live run `39929860762` loaded mission SDE data successfully, then failed an Arc 29 reward assertion (expected 100,000, got 89,000); this is a gameplay mismatch rather than a setup failure.
- Earlier agent-test run `39923041081` passed for a selected paper-doll test, fast live files and Rust tests, with a log artifact uploaded.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused parser tests passed.
- The selected .NET regression test passed locally after the merge conflict was resolved.
- The dispatched agent workflow passed its selected .NET, live-client and Rust tests.
- Push CI run `39931188008` passed the required gate. Rust changes were not made in this job.

## Next steps

1. Check the active main CI runs listed above.
2. Fix any red required-gate result at its root.
3. Update this handoff with final outcomes.
