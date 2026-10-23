# CI Fix Handoff

## Current state

- Release reproduced the New Eden Store test failure: the test constructed `StoreOffersService` without a settlement provider but expected `can_purchase_offer` to report purchasable. The service correctly requires a configured provider. Commit `722f26dd` adds a throwing test provider and verifies availability does not invoke checkout; its focused Release run passed in `eve_p4`.
- CI runs 523 and 524 also failed the fast live fitting walk, at `test_switching_and_moving_modules_is_what_the_reference_does` race 1 and race 2. The server refused an inventory `List` with `SessionRpcBudgetExceeded`: the walk uses 132 weighted units while the default allowance was 120. `DEVIATIONS.md` records the measured sequence.
- The live budget correction is in progress: raise the default to 160, add a dispatcher regression for character setup and six inventory/Dogma rereads, and keep the security status in sync. The first full fast live rerun with that change passed; eight intentionally slow cases were skipped by the script.
- In the earlier checkout, `Eve.Services.Core.Tests` passed 862/862 and `Eve.Services.Tests` passed 208/208 in Release. The same focused regressions are being rerun from a clean worktree at current `origin/main` before pushing.
- CI failures in runs 519, 520, and 522 included an obsolete alliance-budget fixture for `GetAllianceApplications`; commit `f2384b0f` removed that stale alliance entry. It was a contract mismatch, not a runtime flake.

## Continue

1. Finish the focused Release tests in this worktree.
2. Push the Store test fix first, keeping this handoff current.
3. Commit and push the 160-unit budget correction and regression test after validation.
4. Check the newest CI run and update this note with its result.

Use only `eve_p4` through `tools/dev/db-lock.sh`; the live port is `26103`. For .NET set `MSBUILDDISABLENODEREUSE=1` and pass `-p:UseSharedCompilation=false`.
