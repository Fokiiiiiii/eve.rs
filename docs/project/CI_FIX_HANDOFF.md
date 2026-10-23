# CI Fix Handoff

## Current state

- Main CI runs 523–525 failed in the New Eden Store availability test and the live fitting walk. Runs 519–525 show repeated live failures at `test_switching_and_moving_modules_is_what_the_reference_does[race-1]`; runs 519, 522, 524, and 525 include the same Store regression failure. Runs 519, 520, and 522 include an `Alliance_expensive_methods_use_a_named_per_session_budget` failure.
- The Store availability fix is present as commit `7c316f1c` on origin/main and as an equivalent earlier local commit; it aligns the Store availability test with its required settlement provider. The Release focused test passed on lane `eve_p4` with `EVE_MARKET_SEED=skip`.
- The live fitting failure is `SessionRpcBudgetExceeded` during the inventory `List` after a module move. RPC tracing counted 132 weighted units for character setup and the six inventory/Dogma re-reads; the server default was 120.
- The working tree raises the weighted RPC allowance to 160 and adds a dispatcher regression for that fitting read sequence. The strict single-race live regression passed with this allowance (1/1), and the full fast live suite passed on `eve_p4` (8 slow cases and one skill-training-speed case skipped by suite conditions). The focused dispatcher regression passed 147/147; the post-merge Release suites passed `Eve.Services.Core.Tests` 862/862 and `Eve.Services.Tests` 208/208.
- Runs 519, 520, and 522 reported an alliance budget assertion failure because `GetAllianceApplications` was in an `allianceRegistry` budget test even though the active service route is `corpRegistry`. Commit `f2384b0f` moved the coverage to the corporation route; the current full `Eve.Services.Tests` Release run passed 205/205.
- The pinned SDE build 3396210 is loaded in `eve_p4`; its local JSONL copy is in `.cache/sde/eve-online-static-data-3396210-jsonl`.

## Continue

1. `origin/main` was merged; the sole conflict in `SECURITY_STATUS.md` retains upstream repairSvc/FitFitting coverage and the 160-unit default.
2. Finish merging the concurrently pushed Store test fix and push the budget correction with this handoff.
3. Check the newest CI status after push and record its result here.

## Commands

Use lane `eve_p4` only and run database work through `tools/dev/db-lock.sh`. The live port is 26103. For .NET, set `MSBUILDDISABLENODEREUSE=1` and `UseSharedCompilation=false`, and pass `-p:UseSharedCompilation=false`.
