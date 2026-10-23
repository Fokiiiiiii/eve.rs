# Market structure settlement handoff

## Current state
- Worktree: `/home/user/wt-cx14`, branch `cx14`. Market feature, handoff and service-access regression commits were pushed to `origin/main` at `5877066d` after merging the latest main snapshot.
- Nine unrelated Rust deletions from the interrupted checkout remain saved in stash `Preserve pre-existing cx14 unrelated deletions before syncing main`. Do not apply or drop that stash without reviewing it.
- No PostgreSQL database has been used. This task explicitly forbids local database use.

## Implemented
- Added `MarketStructureDirectory`, the persisted structure-market directory, SDE-based eligible-structure topology, Market service fuel/access wiring, and structure-aware order location/settlement in the existing market, wallet and inventory owners.
- Extended `MarketProxyService` visibility, mutation checks and cached-method scope keys for structure orders. Preserved 64-bit structure IDs on market wire rows and summaries.
- Added Host dispatch coverage for permitted and denied structures, order reads and writes, partial and competing fills, rollback and retry, wallet/item state, persistence and restart. Added topology and wire-shape focused tests.
- Added a protocol-level live structure market walk in `tools/headless-client/tests/test_market.py`; it discovers an eligible structure from SDE and checks both order sides, fills, visible book, wallet, item custody and notification sequence.
- Updated `docs/project/TODO.md` and `docs/reference/compat/DEVIATIONS.md` with implementation status, mechanics sources, EveJS market-server cache paths, cached-method scopes, the 1.0% fallback/11.5% cap, and the named-KeyVal ask-row divergence.

## Source checks
- V24 asks callsite and buy consumer were traced. The caller reads `ask.typeID`; both EveJS snapshots request tuple summaries in `GetStationAsks`, so eve.rs uses the named KeyVal form covered by tests.
- Eve University says the Market Hub is limited to Large/Extra Large non-refinery structures and required for market access. Structure fees combine 0.5% SCC with owner-set rate; Broker Relations does not affect structure fees; sales tax starts at 7.5% before Accounting reduction. Both EveJS snapshots use a 1.0% `MARKET_TAX` fallback (max 11.5%) if no group value resolves. Runtime structure type/fuel values come from pinned SDE; configured rates come from persisted settings.
- EveJS external book paths read persistent rows and invalidate scope caches after writes/fills. Its server was compared from source, not executed.

## Checks and next steps
- After the latest main merge, `Eve.Host.Tests` builds successfully with 0 warnings/errors. Earlier, `Eve.Market.Tests` compiled with 0 warnings/errors.
- The focused no-database `MarketStructureWireTests` run passed: 2 passed, 0 skipped. Python syntax compilation of `test_market.py` passed. `git diff --check` passed after the main merge.
- Host.Tests compilation needed small test-only repairs: missing required `ServiceCall.Args` members and analyzer-approved filtered `Assert.Single` forms in `CharacterReSculptHostTests.cs`; market filters use the same form.
- No database-backed test or live headless walk has completed. The focused Market Actions check passed (run `40008539837`); push-triggered `ci.yml` run `40008500517` remains in progress. Next: finish that run, then push the industry budget fix and dispatch its focused Services check. No graphical-client result is claimed.

## 2026-10-23 continuation
- Fresh origin/main changes were merged into this worktree; `Eve.Host.Tests` now builds successfully with 0 warnings and 0 errors. The earlier focused no-database `MarketStructureWireTests` run passed 2/2 with 0 skips.
- Push `5877066d` is complete. The selected Market Actions check passed; push-triggered `ci.yml` is still running. No database lane was used.
- No PostgreSQL or graphical client was used for this continuation; graphical-client acceptance and the live structure-market walk remain outstanding.

## 2026-10-23 industry diagnostic follow-up
- The retry capture's two `industryManager.GetJobCounts` budget refusals were traced through the V24 slot-refresh caller, EveJS handler and registered eve.rs `IndustryJobsService`. The dispatcher had shared the general 160-unit budget; industry now has an independent 160-unit session bucket and a regression for cross-service starvation.
- `Eve.Services.Tests` builds with 0 warnings and 0 errors. The focused regression is scheduled for Actions after push. No local focused test beyond the market wire test was run; no PostgreSQL or graphical client was used.
