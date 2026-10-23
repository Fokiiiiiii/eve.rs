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
- Updated `docs/project/TODO.md` and `docs/reference/compat/DEVIATIONS.md` with implementation status, mechanics sources, EveJS market-server cache paths, cached-method scopes, the 1.0% fallback/11.5% cap, and the station/system tuple wire contract.

## Source checks
- V24 `PopulateAsksForMyRange` expands station/system mapping values through `ConvertTuplesToBestByOrders` into `BestByOrder(price, volRemaining, typeID, stationID)`. Both EveJS snapshots return four-value tuples for these ranges; eve.rs now does the same. Region best orders remain named KeyVals. The direct buy helper's `ask.typeID` read does not define the browse shape.
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

## 2026-10-23 diagnostics and CI follow-up
- The structure market Host regression used sessions at an Upwell structure while persisting their characters at an SDE NPC station. With no Marketing skill, that placed the seller outside the one-jump sell range. Main now starts those fixture characters at the structure; the current push gate is checking the scenario.
- The live SKINR failure was in the headless test encoder. V24.01 wire descriptors show two nested messages before the sequential item ID; the test emitted three. The helper now matches the client shape, and the push gate will verify the live consume/retry flow.
- The separate real-client `industryManager.GetJobCounts` budget fix is on main with its dispatcher regression. Its focused Services Actions check passed. No local PostgreSQL or graphical-client verification was performed.

## 2026-10-23 push 8610990c
- Corrected the SKINR live test's ConsumeRequest encoder using the V24.01 generated descriptors: two nested messages lead to the sequential item ID. The prior helper added a third nested message, so the gateway received ID 0 and returned its correct invalid-item response.
- Push-triggered CI run 683 (`40014207144`) was queued when checked; run 682 on parent `d7f45242` was still running. The full CI E2E job will exercise the corrected consume/retry flow. No PostgreSQL database was accessed locally and no graphical-client result is claimed.
- The industry budget focused Actions regression passed on run `40010004814`; the structure-market scenario's corrected location fixture is included in the current gate.


## 2026-10-23 CI run 678 follow-up

- Run 678 (`737ba657`) failed the first structure sell even after the test persisted seller, buyer and structure owner at the same structure as their sessions. That disproves the location-only cause recorded above.
- The Host test now includes the returned Macho label or CustomInfo text when the first sell fails. The exact refusal and its root fix are still pending the next focused Actions check; the corrected location fixture alone is not a verified fix.
## 2026-10-23 run 683 affiliation follow-up
- Run 683 on `8610990c` reported `StructureMarketDenied` in the structure sell regression after the location fix. The production read model resolves affiliation from persisted `CorporationMember` relations. The fixture helper had created a character with a corporation ID but skipped the relation mutation performed by normal character creation.
- Added the active corporation membership relation to every market test trader in the scratch relation schema; the denied trader remains affiliated with the different corporation. This makes the fixture's stored state match its intended character setup without changing access logic.
- The updated `Eve.Host.Tests` project builds locally with 0 warnings and 0 errors. No database-backed test was run locally; Actions must verify the fixture after push.

## 2026-10-23 SKINR draft-save follow-up
- Agent run 158 on `253597a9` advanced through component consumption and failed at Draft.SaveRequest with HTTP 400. The V24 client messenger builds the slot layout and sends editor configurations; Host catalog validation also rejects SDE components marked disallowed for the selected slot.
- Updated the live test's SDE selector to exclude `disallowedSlots` for its chosen slot, matching the same catalog condition used by the Host. The save assertion now prints the gateway response on failure. The current full push gate run 686 (`40015069193`) is checking the parent fixture and market affiliation update; the new selector needs Actions verification after push.
- No local PostgreSQL test or graphical-client check was run.

## 2026-10-23 SKINR draft layout wire correction
- Agent run 158 (`253597a9`) passed component consumption and failed the next `Draft.SaveRequest`. The V24 client encodes a slot as `Slot.id` plus `Slot.configuration`; `SlotConfiguration` selects coating/pattern by category, and the selected variant wraps its identifier in inner field 1. `_layout` omitted that inner wrapper, so `ReadSlot` could not read the component ID.
- Added the missing nested field in `_layout`, kept `_component_kind` category-selected per the V24 `ComponentLicense` builder, excluded SDE `disallowedSlots` in the fixture selector, and surfaced the SaveRequest response message.
- The current full gate on `4484ec9c` predates these wire changes. The updated Python live test and correct MarketHost affiliation fixture require the next Actions gate. No PostgreSQL or graphical-client test was run.

## 2026-10-23 push 1a9e73e9
- Merged the latest `origin/main` into `cx14` and pushed the SKINR SaveRequest layout correction, test-side `disallowedSlots` filtering, and market trader affiliation fixture repair in commit `1a9e73e9`.
- Push-triggered `ci.yml` run 689 (`40016219712`) completed with failures in the SKINR live test and `MarketHostTests`. The market failure was the test's `PyStr` expectation for raw `PyBytes`; the SKINR failure was a UUID-length error in the live parser. Both test-side corrections are now in the worktree and need verification on the next pushed SHA. The fast-live step also skipped eight tests: one training-speed check and seven slow tests.
- No PostgreSQL database or graphical client was used locally. Python syntax compilation passed; the latest CI gate has not completed yet.

## 2026-10-23 focused MarketHost Actions follow-up
- Focused Actions run `40016728738` failed in `MarketHostTests` at the test's `CachedMethodCallResult.details.sessionInfo` assertion: it expected `PyStr`, while `ObjectCaching.MethodResult` intentionally writes `PyBytes` through `Raw`. The V24 client's `CachedMethodCalled` checks only for the key's presence. Production wire behavior is unchanged; the regression now asserts the exact ASCII byte value.
- The corrected `Eve.Host.Tests` project builds with 0 warnings and 0 errors. Focused run `40016728738` predates the assertion correction; a new focused Actions run must verify the current test; no local PostgreSQL database or graphical client was used.

## 2026-10-23 SKINR nested UUID follow-up
- The push gate exposed a Python `uuid.UUID` length error in the SKINR live walk. V24 descriptors define `GetAllActiveResponse.Job.id` and `CompletedNotice.id` as an `Identifier` message whose `uuid` field contains the 16-byte value; the test read that message as the UUID bytes directly. Added the missing nested field read in both assertions; production response encoding is unchanged.
- Focused live run `40018639989` on the earlier SHA remains in progress. The fix is pushed as `0f0cef8f`; current focused runs `40019674482` (`MarketHostTests`) and `40019703405` (`test_skinr_live.py`) are in progress. No local PostgreSQL or graphical client was used.

## 2026-10-23 push 0f0cef8f
- Pushed the SKINR nested UUID test parser fix and the market cached-result test expectation correction after merging latest `origin/main`.
- `Eve.Host.Tests` builds with 0 warnings and 0 errors; `test_skinr_live.py` passes Python syntax compilation. Focused Actions runs `40019674482` and `40019703405` are in progress. Initial `ci-status.py failures 0f0cef8f` check found no push-triggered `ci.yml` run yet; recheck it.
- No PostgreSQL database or graphical client was used locally.

## 2026-10-23 station/system ask wire correction
- V24 `MarketBase.PopulateAsksForMyRange` applies `ConvertTuplesToBestByOrders` to station/system ask values, expanding each value as `BestByOrder(price, volRemaining, typeID, stationID)`. EveJS v0.12.9 and 261018.1 return that four-value tuple; region best results keep their named KeyVal shape. The direct-buy helper's `ask.typeID` path did not establish the browse shape.
- eve.rs now serializes station/system summaries as tuples and retains named KeyVals for region best. `MarketRpcTests`, `ProtocolTests`, the real Host structure dispatch test and the live walk assert the correct tuple order.
- Local Release builds of `Eve.Market.Tests` and `Eve.Host.Tests` passed with 0 warnings/errors. The focused no-database `MarketStructureWireTests.Station_and_system_ask_summaries_are_tuples_for_the_client_range_converter` passed 1/1 with 0 skips. Python syntax compilation and `git diff --check` passed.
- The parent gate on `d86a5ef5` exposed the remaining structure settlement fixture assertion: expected total wallet balance 19,980 ISK, actual 19,835 ISK. This reflects one two-unit immediate fill and one losing immediate buy resting at 10 ISK/unit with 20 ISK escrow plus the 100 ISK broker minimum and 25 ISK SCC minimum. The Host regression now checks 19,835 total and the 9,980/9,855 balances. Current focused Actions checks are pending after push. No local PostgreSQL database or graphical client was used.

## 2026-10-23 structure broker minimum

- The CI failure on `1d96ae32` compared seller wallet 9900 with 9875 ISK after placing a 40 ISK structure sell order. Main commit `d589748d` correctly made the injected-failure check compare the wallet balances immediately before and after the failed buy.
- EVE University specifies a 100 ISK minimum for the Upwell broker fee, defined as owner rate plus the 0.5% SCC surcharge ([Market](https://wiki.eveuniversity.org/Market)). EveJS v0.12.9 floors its two entries separately at 100 and 25 ISK (`server/src/services/market/marketRules.js:208-241,243-276`). eve.rs now applies one 100 ISK minimum to their sum and allocates the raw 0.20 ISK SCC amount first on the 40 ISK test order.
- The Host scenario retains the rollback baseline assertions and checks the post-placement seller balance is 9900 ISK. `Structure_broker_and_scc_components_share_the_100_isk_minimum` covers the fee split without a database.
- Worktree checks: Market and Host test project builds passed with 0 warnings and 0 errors; the focused Market rule test passed 1/1 with no skips. No local PostgreSQL test was run. Commit and focused Actions verification remain pending.


## 2026-10-23 immediate-buy market regression

- CI run 699 on `1f1e23a0` exposed a Host balance mismatch: one of two concurrent `BuyMultipleItems` requests filled the last ask, while the other was converted from duration zero to a 90-day bid and charged 20 ISK escrow plus fees. EveJS v0.12.9 `executeBuyRequest` only creates an order when duration is positive; V24 `BuyMulti` announces only rows returned by `BuyMultipleItems`. EVE University specifies broker fees only for non-immediate orders ([Market](https://wiki.eveuniversity.org/Market)).
- `MarketTrading` now preserves duration zero, `PlaceBuyOrder` restores the `CustomInfo` refusal when no seller matches, and comments and regression cases now cover partial/no-match, concurrency, rollback, wallet and returned-order state. Existing tests for duration-positive orders remain unchanged.
- The merged upstream change `d86a5ef5` adds graceful Host shutdown for the SKINR restart walk; its full CI run and subsequent main gate were still running at this update. The local `Eve.Host.Tests` build on `1f1e23a0` passed; no local PostgreSQL tests were run. The focused Actions checks and push gate for this correction are pending.

## 2026-10-23 CI follow-up on cce06a2d

- CI 703 and Agent 184 confirmed the concurrent structure buy still posted an unwanted remainder bid; the Host assertion expected 19,980 ISK and observed 19,835 ISK. CI 704 on `0649fc34` also showed 9,730 ISK versus 9,980 ISK after a no-fill immediate buy, confirming the conversion to a 90-day bid was still active.
- The current change uses the requested duration unchanged, refuses a direct duration-zero buy when there is neither a fill nor an order, and tests the returned rowset, wallet deltas, no resting bids and rollback after a later batch-line failure. The structure fee path also now applies EVE University's 100 ISK Upwell floor once across owner and SCC components.
- Market.Tests and Host.Tests builds passed after merging current main. No local PostgreSQL test or graphical-client verification was run. CI 707 and Agent 187 on `cce06a2d` were active at this note; the focused test and push gate are pending.
