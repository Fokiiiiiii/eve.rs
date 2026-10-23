# CI First Handoff

## Current state

- Remote main contains candidate dd223b9f and the CI-first changes described here. The concurrent DispatchGoldenTests conflict was resolved with the latest main-side mock registrations.
- Agent-test run `39931135089` succeeded on dd223b9f. It ran project `Eve.Services.Tests`, filter `Market_owner_history_reads_have_a_named_per_session_budget`, live test `test_login_to_character.py`, and Rust tests.
- Push CI runs `39931188008` (0d2a7312), `39931680738` (beaa821b), `39932084783` (8baa4ba6), `39932448507` (f7646151), and `39932859826` (0c062d6d) completed successfully, including Windows and the required gate. Later main pushes did not cancel earlier runs.
- `python3 tools/dev/ci-status.py failures ae6c86ce` printed the prior failure and test log tail: `Market_owner_history_reads_have_a_named_per_session_budget` expected 5 calls and observed 4. The mock registration was corrected; the focused local regression and later full Actions gates passed.
- Local focused .NET regression: 1 passed, 0 failed, 0 skipped. No local database or broad local suite was used.
- Nightly run `39926148439` completed with 22 failed jobs (21 slow-live/skill-training checks plus the summary). RPC fuzz, throughput benchmarks, and full Windows passed. The workflow reported these failures separately from the push gate.
- Slow-live run `39929860762` loaded mission SDE data, then failed an Arc 29 reward assertion (expected 100,000, got 89,000); this gameplay mismatch is outside the push-gate work.
- Earlier agent-test run `39923041081` passed for a selected paper-doll test, fast live files, and Rust tests; its log artifact was uploaded.

## Verification

- `python3 tools/dev/test_ci_status.py`: 8 focused parser tests passed.
- The selected .NET regression test passed locally after resolving the merge conflict.
- The dispatched agent workflow passed its selected .NET, live-client, and Rust tests.
- Five push CI runs passed their required gates. Rust changes were not made in this job.

## Follow-up

- The CI-first workflow work is complete. Investigate the Arc 29 reward mismatch and nightly gameplay failures separately.
