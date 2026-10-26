# RPC host coverage handoff: RPC-00950–RPC-01328

## Worktree and scope

Worktree: `/home/user/wt-cx13`. No PostgreSQL database was used. Existing unrelated untracked `docs/project/roller/handoff-npc-entity-ewar.md` was preserved.

This pass added real Host socket regressions for the existing Blueprint, skyhook, and bounty services. The crate Host socket regression already present in `CrateServiceHostWireTests.cs` covers ClaimFitting, ClaimLoot, and ClaimLootFromCrateStack paths, transport `machoVersion`, success replies, item-change notices, and malformed input.

## V24.01 client trace and EveJS comparison

Client searches covered the whole `/home/user/eve-logic/eve` tree. These are the callers and consumers found for the requested rows:

| RPC rows | Client call and consumer evidence |
|---|---|
| 00950 billMgr | The cited `corpBillsAutoPaySettingsPanel.py:59` does not call a remote method named `get`. Actual billMgr calls are `GetAutomaticPaySettings` at `corpBillsAutoPaySettingsPanel.py:36`, consumed as the owner-to-settings map at `:50-57` and sent back at `:83`; `GetCorporationBills` at `corpBillsAutoPaidPanel.py:87`, `corpBillsPayablePanel.py:88`, and `warLandingPage.py:184`; `GetCorporationBillsReceivable` at `corpBillsReceivablePanel.py:74`; and bill payments at `walletSvc.py:126,129`. The v0.12.9 implementation provides `GetCorporationBills` and `GetCorporationBillsReceivable` (`server/src/services/account/billMgrService.js:263-277)); it has no `get` method. |
| 00951–00954 blueprintManager | `blueprintSvc.py:24` calls GetLimits and reads `maxBlueprintResults`; `:95` passes GetBlueprintData to `industryCommon.BlueprintInstance`; `:136` and `:149-151` consume GetBlueprintDataByOwner's blueprint list and per-type counts. All matching calls in the client tree were found there. The UI uses the resulting blueprint objects in `infoContentBase.py:724,992,1275,1381` and `infosvc.py:2323`. EveJS v0.12.9 returns the 500-row limit, a blueprint payload, and the (blueprints, counts) tuple in `server/src/services/industry/blueprintManagerService.js:24-53`. |
| 00955–00968 bountyProxy | `bountySvc.py:82` calls AddToBounty; `:147` calls GetBounties; `:219-225` calls all three top-hunter methods; `:229` calls GetMyBounties; `:241-242` are GM methods; `:255,262` call cancel/sell; `:274` calls GetMyKillRights; `:290-302` reads available kill rights and combined bounty/rights data. Consumers include `bountyWindow.py:446,541-545,554-582,1006`, `killRightsPanel.py:30`, and `killRights.py:119,324`. Rows 00967/00968 refer to local `GetMyBounties.clear_memoized()` calls at `bountySvc.py:50,84`; these invalidate a decorated client cache and are not service RPCs. No whole-tree client caller for the GM reimburse/cache-clear methods was found. EveJS v0.12.9's behavior is in `server/src/services/bounty/bountyProxyService.js:378-592`. |
| 01173 colonyResourcesMgr | `sovereigntyResourceSvc.py:264-268` forwards/caches the RPC. The skyhook browser and sov hub consume the returned rows at `skyhookBrowserController.py:25-26` and `sovHubEntryController.py:138-139`. EveJS reads the corporation from session and returns sorted summary rows in `server/src/services/sovereignty/colonyResourcesMgrService.js:108-121`. |
| 01324–01328 crateService | `lootItem.py:54,103` calls ClaimLoot and ClaimFitting as actions; inventory state is refreshed through item notifications. `controller.py:113,299` consumes ClaimLootFromCrateStack's opened/requested counts to update the stack. EveJS handles crate claims in `server/src/services/campaign/crateService.js:542-704`. |

## eve.rs wiring and checks

- `BlueprintManagerService` is registered by `IndustryRpc.cs:59-62` and reads the existing item/job providers. `SkyhookResourceMgrService` is composed by `SovereigntyComposition.cs:251` and reads the existing skyhook owner data/authorization path. `BountyProxyKillRightService` is registered by `CrimewatchRpc.cs:119-125`; its production bounty handler is `PlayerBountyRpc.HandleAsync`, backed by the persistent bounty store and wallet transaction path.
- Added `dotnet/tests/Eve.Host.Tests/BlueprintAndSkyhookHostWireTests.cs`, using a real Host socket and registered production services with fixture read providers. It verifies transport-added `machoVersion`, limits, item payload, owner listing/count shape, facility filtering, and corporation skyhook row shape.
- Added `dotnet/tests/Eve.Host.Tests/BountyProxyHostWireTests.cs`, using the real Host socket and registered bounty service. It exercises client read/placement/ranking method routing and reply shapes with fixture service providers. Persistent placement/retry/refusal/restart behavior remains covered by the existing database-backed `PlayerBountyHostTests`; no database test was run in this task.
- Existing `CrateServiceHostWireTests` exercises seven crate service methods over the Host socket. The prior handoff records the earlier Actions result for that test.
- Local checks: Blueprint/Skyhook focused Host wire test passed (1/1). Host.Tests compile-check passed with zero warnings and errors after adding both test files. A prior attempt at the focused test caught and corrected response-key and numeric-width expectations; the final focused run passed.
- The focused bounty Host wire test and Actions checks are pending the first push.

## Remaining classification issues

RPC-00950's supplied name/reference conflicts with the observed client: no `billMgr.get` call exists at the cited location or in the complete tree, and EveJS has no such method. No speculative alias was added. Rows RPC-00967/00968 are local cache operations and must not be registered as server routes. The bounty regression's fixture provider does not itself prove persistent mutation behavior; the existing persistence tests are database-backed and were not run here. Graphical-client behavior was not checked.
