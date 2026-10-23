# Market structure settlement handoff

## Current state
- Worktree: `/home/user/wt-cx14`, task branch `cx14`, based on `eec8a530`; implementation is not committed yet.
- Nine unrelated Rust deletions from the interrupted checkout remain saved in stash `Preserve pre-existing cx14 unrelated deletions before syncing main`. Do not apply or drop that stash without reviewing it.
- No PostgreSQL database has been used. This task explicitly forbids local database use.

## Implemented
- Added `MarketStructureDirectory`, the persisted structure-market directory, SDE-based eligible-structure topology, Market service fuel/access wiring, and structure-aware order location/settlement in the existing market, wallet and inventory owners.
- Extended `MarketProxyService` visibility, mutation checks and cached-method scope keys for structure orders. Preserved 64-bit structure IDs on market wire rows and summaries.
- Added Host dispatch coverage for permitted and denied structures, order reads and writes, partial and competing fills, rollback and retry, wallet/item state, persistence and restart. Added topology and wire-shape focused tests.
- Added a protocol-level live structure market walk in `tools/headless-client/tests/test_market.py`; it discovers an eligible structure from SDE and checks both order sides, fills, visible book, wallet, item custody and notification sequence.
- Updated `docs/project/TODO.md` and `docs/reference/compat/DEVIATIONS.md` with implementation status, mechanics sources, the EveJS market-server cache paths, the 1.0% fallback/11.5% cap, and the named-KeyVal ask-row divergence.

## Source checks
- V24 asks callsite and buy consumer were traced. The caller reads `ask.typeID`; both EveJS snapshots request tuple summaries in `GetStationAsks`, so eve.rs uses the named KeyVal form covered by tests.
- EveJS market cached-method scopes: station asks use `stationid`, system asks and `GetOrders` use `solarsystemid2`, PLEX books use `regionid`. Both external market-server snapshots read persistent order-book rows and invalidate scope caches after writes/fills.
- EVE University states the Market Hub is limited to Large/Extra Large non-refinery structures, is required for market access, and that structure fees combine 0.5% SCC with an owner-set rate. Broker Relations does not affect structure fees; sales tax starts at 7.5% before Accounting reduction. Both EveJS snapshots use a 1.0% `MARKET_TAX` fallback (max 11.5%) when no group value resolves. Runtime structure type/fuel values are from pinned SDE; configured rates are from persisted settings.
- EveJS was compared from source; its server was not executed.

## Checks
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Host/Eve.Host.csproj --no-restore -m:1 -p:UseSharedCompilation=false` succeeded with 0 warnings/errors.
- `Eve.Market.Tests` and `Eve.Host.Tests` each compile with 0 warnings/errors.
- The focused no-database `MarketStructureWireTests` run passed: 2 passed, 0 skipped.
- `python3 -m py_compile tools/headless-client/tests/test_market.py` passed.
- Host.Tests compilation required minimal test-only fixes in `CharacterReSculptHostTests.cs` (required `Args` members and analyzer-approved filtered Single calls); the market test uses the same analyzer-approved form.
- `git diff --check` passed before the last test-only cleanup; rerun it before commit.
- No database-backed test or live headless run has completed yet. Next: rerun diff check, commit, fetch/merge/push main, dispatch selected Host/Market Actions checks and live-slow `test_market.py`, then inspect the push-triggered CI run. No graphical-client result is claimed.
