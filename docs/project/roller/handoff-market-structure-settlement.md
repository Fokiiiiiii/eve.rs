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
- Push-triggered `ci.yml` run 689 (`40016219712`) was queued at the latest check. Run 686 (`40015069193`) for parent `4484ec9c` remains in progress; run 683 was queued when checked. Recheck current-head Actions before considering these flows verified.
- No PostgreSQL database or graphical client was used locally. Python syntax compilation passed; the latest CI gate has not completed yet.
