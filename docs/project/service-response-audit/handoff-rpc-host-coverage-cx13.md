# RPC host coverage handoff: RPC-00950–RPC-01328

## Worktree and scope

Worktree: `/home/user/wt-cx13`. No PostgreSQL database was used. Existing unrelated untracked `docs/project/roller/handoff-npc-entity-ewar.md` was preserved.

This pass added real Host socket regressions for the existing Blueprint, skyhook, and bounty services. Existing `CrateServiceHostWireTests.cs` covers ClaimFitting, ClaimLoot, and ClaimLootFromCrateStack over Host routes, including transport `machoVersion`, success replies, item-change notices, and malformed input.

## V24.01 client trace and EveJS comparison

Client searches covered the whole `/home/user/eve-logic/eve` tree. Callers and consumers found for the requested rows:

| RPC rows | Client call and consumer evidence |
|---|---|
| 00950 billMgr | The cited `corpBillsAutoPaySettingsPanel.py:59` does not call a remote method named `get`. Actual calls: `GetAutomaticPaySettings` at `corpBillsAutoPaySettingsPanel.py:36`, consumed as owner-to-settings map at `:50-57` and submitted at `:83`; `GetCorporationBills` at `corpBillsAutoPaidPanel.py:87`, `corpBillsPayablePanel.py:88`, and `warLandingPage.py:184`; `GetCorporationBillsReceivable` at `corpBillsReceivablePanel.py:74`; bill payments at `walletSvc.py:126,129`. EveJS v0.12.9 implements corporation bills and receivables at `server/src/services/account/billMgrService.js:263-277`, with no `get` method. |
| 00951–00954 blueprintManager | `blueprintSvc.py:24` calls GetLimits and reads `maxBlueprintResults`; `:95` passes GetBlueprintData to `industryCommon.BlueprintInstance`; `:136` and `:149-151` consume GetBlueprintDataByOwner's blueprint list and per-type counts. UI consumes blueprint objects in `infoContentBase.py:724,992,1275,1381` and `infosvc.py:2323`. EveJS v0.12.9 returns limit 500, blueprint payload, and (blueprints, counts) at `server/src/services/industry/blueprintManagerService.js:24-53`. |
| 00955–00968 bountyProxy | `bountySvc.py:82` calls AddToBounty; `:147` GetBounties; `:219-225` all three top-hunter methods; `:229` GetMyBounties; `:241-242` GM methods; `:255,262` cancel/sell; `:274` GetMyKillRights; `:290-302` available rights and combined bounty/rights data. Consumers include `bountyWindow.py:446,541-545,554-582,1006`, `killRightsPanel.py:30`, and `killRights.py:119,324`. Rows 00967/00968 are local `GetMyBounties.clear_memoized()` calls at `bountySvc.py:50,84`, invalidating a client cache, not RPCs. No tree-wide caller for GM reimburse/cache-clear was found. EveJS behavior is in `server/src/services/bounty/bountyProxyService.js:378-592`. |
| 01173 colonyResourcesMgr | `sovereigntyResourceSvc.py:264-268` forwards/caches the RPC; UI consumes rows at `skyhookBrowserController.py:25-26` and `sovHubEntryController.py:138-139`. EveJS reads corporation from session and returns sorted summary rows at `server/src/services/sovereignty/colonyResourcesMgrService.js:108-121`. |
| 01324–01328 crateService | `lootItem.py:54,103` calls ClaimLoot and ClaimFitting; inventory state refreshes via item notifications. `controller.py:113,299` consumes ClaimLootFromCrateStack opened/requested counts. EveJS claim logic is at `server/src/services/campaign/crateService.js:542-704`. |

## eve.rs wiring and checks

- Existing production routes: BlueprintManagerService via `IndustryRpc.cs:59-62`; SkyhookResourceMgrService via `SovereigntyComposition.cs:251`; BountyProxyKillRightService via `CrimewatchRpc.cs:119-125` to `PlayerBountyRpc.HandleAsync`, backed by persistent bounty and wallet services.
- Added `BlueprintAndSkyhookHostWireTests.cs`: real Host socket, production registration, fixture read providers, transport `machoVersion`, limits, item payload, owner list/count, facility filtering, and corporation skyhook rows.
- Added `BountyProxyHostWireTests.cs`: real Host socket and registered bounty service; exercises read/placement/ranking routes and wire shapes. Persistent placement/retry/refusal/restart behavior is covered by DB-backed `PlayerBountyHostTests`; no database tests were run.
- Local focused Blueprint/Skyhook Host test passed (1/1). Combined focused Host test initially caught integer-width assertions in bounty payload; corrected to observed PyLong widths. Host.Tests build passed with 0 warnings/errors in 25m 03s.
- Actions dispatch on commit `95df57874a6944098cb56b38c42c669516b206b7`, run `40266759084`, failed 19 passed / 1 failed because the bounty regression expected PyInt where Host returned PyLong. The assertion has been corrected. The rerun and push-triggered CI outcome for the corrective commit are pending.

## Remaining classification issues

RPC-00950's supplied method/reference conflicts with the client tree and EveJS; no speculative alias was added. RPC-00967/00968 are local cache operations and should not be server routes. The fixture-backed bounty wire test does not prove persistent mutation behavior; its DB-backed persistence test was not run. Graphical-client behavior was not checked.
