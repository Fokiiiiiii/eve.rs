# CI First Handoff

## Current state

- Remote `main` is `7d083ee1`; it contains candidate `dd223b9f`. The concurrent `.NET DispatchGoldenTests` conflict was resolved using the latest main-side mock registrations, and the focused test passed locally.
- Agent-test run `39931135089` (SHA `dd223b9f`) was dispatched from `main` with project `Eve.Services.Tests`, filter `Market_owner_history_reads_have_a_named_per_session_budget`, live test `test_login_to_character.py`, and Rust tests. It is still running; the workflow returned the run ID and action URL.
- Push CI run `39930594371` (SHA `2ae88e4d`) is still running. Rust format/clippy, Rust tests, .NET build, fixtures, Python, and .NET other-tests shard 1/3 passed; end-to-end, Windows, and remaining .NET shards were active at last check.
- Push CI run `39931188008` (SHA `0d2a7312`) is also running after a later concurrent push. Format/clippy, fixtures, Python, and advisory Rust passed; .NET build, Rust tests, Windows, and end-to-end were active at last check.
- Local focused .NET command passed: 1 passed, 0 failed, 0 skipped. No database or broad local suite was used. The source fix in this worktree is committed and pushed; the latest-main changes were merged before the handoff update.
- Nightly run `39926148439` completed with 22 failed jobs (21 slow-live/skill-training checks plus the summary). RPC fuzz, throughput benchmarks, and the full Windows workflow passed. The job reported the feature/timing failures and did not affect the push gate.
- Slow-live run `39929860762` loaded mission SDE data successfully, then failed an Arc 29 reward assertion (expected 100,000, got 89,000); this is a gameplay mismatch, not a setup failure.
- Earlier agent-test run `39923041081` passed for a selected paper-doll test, fast live files, and Rust tests, with an artifact uploaded.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused parser tests passed.
- The selected .NET regression test passed locally after the merge conflict was resolved.
- Rust changes were not made in this job. CI owns the broad checks; no full suite was run locally.

## Next steps

1. Check agent-test run `39931135089` and push CI runs `39930594371` / `39931188008`.
2. Fix any red gate result at the root and dispatch the selected workflow again if needed.
3. Update this handoff with final run outcomes and push the final note.
