# Deep jump-drive, cyno and clone audit: clone-jump concurrency fix

## Selected daily flow and defect

The selected most routine player flow is switching a jump clone from the Character Sheet. Two CloneJump RPCs can both pass the cooldown preflight before either commits. The former PostgreSQL path serialized the writes but did not recheck persisted jump time, allowing a second clone to activate inside the cooldown.

The fix passes the skill-derived cooldown into the transaction. After taking the character row lock, the store checks persisted `time_last_clone_jump_filetime` before changing clone activation, ship, balance, or jump time. Same-location switching retains its exception. A refusal maps to the existing cooldown CustomNotify. The current main implementation also supports structure-resident clones and origin-bay settlement; the fix preserves these behaviors.

## Whole-client evidence

- `eve/client/script/ui/shared/neocom/charsheet/jumpClonesPanel.py:491-515` renders availability from last jump time and `cloneJumpCoolDown`; `:605-606` sends `CloneJump(clone.location_id, clone.id)`; `:627` guards the action; `:423` subscribes to clone updates; `:657-658` consumes `OnCloneJumpUpdate`.
- `eve/client/script/ui/services/clonejumpsvc.py:52-60` reads clone, implant and last-jump state; `:111-114` exposes last jump time; `:192-210` sends destination, clone ID, cost and confirmation; `:212-230` relocates the session on success, emits `OnCloneJumpUpdate` for the cooldown UserError, then rethrows; `:294-299` invalidates caches and fires the cache-invalidated event. The other `OnCloneJumpUpdate` consumer is `eve/client/script/ui/shared/neocom/skillExtractorController.py:624-626`.
- All structure-cyno client callers found: `eve/client/script/parklife/cynoTravelSvc.py:53` performs a single jump via session change; `:80` requests a fleet bridge to a structure beacon (after a fleet-membership check); `:122` performs a fleet group jump via session change (also gated on fleet membership). The menu routes single jumps at `eve/client/script/ui/services/menusvc.py:2663-2665`.
- All bridge-travel callers found: the menu uses `structureJumpBridgeMgr.CmdJumpThroughStructureStargate` at `eve/client/script/ui/services/menusvc.py:2105`; autopilot uses it at `eve/client/script/parklife/autopilot.py:348`. Bridge destination readers are `eve/client/script/ui/shared/info/infosvc.py:821`, `eve/client/script/ui/services/structure/structureDirectory.py:175-176`, and the destination-name validator at `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:361`.
- The map preview computes jump reactivation and fatigue in `eve/client/script/ui/shared/mapView/markers/markerTooltipUtil.py:71-97`, reading cached `jumpTimers.GetTimers(session.charid)` from `eve/client/script/ui/services/crimewatchSvc.py:604-612`; `OnJumpTimersUpdated` refreshes that cache at `:614-616`. These are preview consumers, separate from server settlement.

## Mechanics and references

- EVE University [Jump clones](https://wiki.eveuniversity.org/Jump_clones) documents the normal 24-hour cooldown, same-location exception and Infomorph Synchronizing's one-hour reduction per skill level.
- EVE University [Jump Drives and Cynosural Fields](https://wiki.eveuniversity.org/Jump_Drives_and_Cynosural_Fields) gives jump-drive restrictions and fatigue rules: minimum fatigue of 10 minutes times (1 + light years), otherwise remaining fatigue times (1 + distance), capped at 5 hours; activation delay is the greater of (1 + light years) minutes and one tenth of prior fatigue, capped at 30 minutes. eve.rs `JumpDriveRules.AfterJump` implements those minima, multipliers and caps. [Jump drives](https://wiki.eveuniversity.org/Jump_drives) describes field types and which hulls may use them.
- EveJS 261028.1 `server/src/services/_shared/jumpTimerRuntime.js:70-98,186-223` derives distance multipliers and the same timer floors/caps. Its provider fatigue multiplier (attribute 1972) is distinct from the passenger hull multiplier (1971), matching eve.rs bridge settlement.
- EveJS clone runtime `server/src/services/station/jumpCloneRuntime.js:1815-1825` checks cooldown; `:1827-1922` builds state and exempts same-location jumps; `:2425-2501` performs activation. The serial path matches the client rule but does not protect concurrent requests.
- No static SDE value drives the clone cooldown; it uses character skill and persisted timestamp. Jump-drive fuel, range, fatigue and field restrictions are read from pinned SDE attributes rather than captured values.

## eve.rs implementation and audit

- `dotnet/src/Eve.Services.Core/JumpCloneService.cs:379-424` retains the early client error, derives cooldown hours, validates structure destination/confirmation, passes cooldown into the store and maps transactional refusal to the same CustomNotify.
- `dotnet/src/Eve.Host/Composition/HostJumpCloneStore.cs:108,121-123` locks the character row, calls the transactional recheck, and stops settlement on refusal.
- `dotnet/src/Eve.Characters/JumpClones.cs:13,113-124` checks persisted time before moving clone rows and returns a cooldown refusal without mutation.
- `dotnet/src/Eve.Services.Space/JumpDriveRules.cs:13-61,193-197` reads SDE dogma attributes and implements fatigue floors/caps. `SpaceJumpDrive.cs:105-184` validates range/fuel/cooldown, reserves fuel and timer state before the simulation call, and refunds the spool on refusal.
- `dotnet/src/Eve.Services.Space/StructureBeaconTravel.cs:40-84` checks beacon type, selected system, fleet/service state, jammer and structure access before dispatching single, bridge or group jump. Host composition wires those callbacks in `dotnet/src/Eve.Host/Composition/StructureCynoBeaconComposition.cs:37-70`.
- Fleet jump portals settle passenger fuel and fatigue in `dotnet/src/Eve.Services.Space/SpaceBridges.cs:175-266`, using provider and passenger multipliers. Ansiblex structure-gate travel is a separate route.
- `dotnet/tests/Eve.Characters.Tests/JumpCloneStoreTests.cs:101+` checks a second jump inside cooldown without state movement. `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:316+` races two actual Host RPC calls against PostgreSQL and asserts one activation, one cooldown CustomNotify, one fee and the persisted destination.

## Local verification after merging main

Database: `eve_p8`, with pinned SDE build 3396210 and mission data build 3405148 loaded.

- Host test project build: passed, 0 warnings and 0 errors.
- `JumpCloneStoreTests`: 6 passed, 0 skipped.
- `JumpCloneServiceTests|JumpCloneShipInstallTests`: 7 passed, 0 skipped.
- Full `CharacterHostTests`: 25 passed, 0 skipped.
- Concurrent Host/PostgreSQL race regression: 1 passed, 0 skipped.
- Existing cooldown reset wire-order regression: 1 passed, 0 skipped.
- Relevant `JumpDriveRulesTests|CynoJumpHostTests|StructureTravelRpcTests|StructureTravelTests`: 66 passed, 0 skipped.
- Red/green regression check: with the transactional cooldown guard removed, the focused store test failed at the expected cooldown assertion (1 failed); after restoring the guard it passed (1 passed).

## Remaining area gap

The Ansiblex `CmdJumpThroughStructureStargate` path has no Host travel callback. `dotnet/src/Eve.Host/Rpc/StructureRpc.cs:68` wires only bridge-destination lookups; `dotnet/src/Eve.Services.Space/StructureTravelRpcService.cs:78-87` returns the named not-wired error for travel. User-facing travel and destination lookup callers are listed above. This audit does not claim Ansiblex traversal working; the deployed Titan/Black Ops/Rorqual jump-portal path and cyno beacon paths are separate and have their own implementation/tests.

## Handoff checkpoint

- Main run 1425 (`0e95daff`) failed two Host wire tests because their expectations omitted the client's `OnTutorialSkipped` event: `Login_and_onboarding_rpcs_keep_client_wire_contracts_with_transport_kwargs` and `Air_and_login_support_rpcs_return_client_shapes_through_the_host`.
- Both failures reproduced locally on `eve_p8`. Whole-client consumers in `storylines/client/airnpe.py:46-111`, `eve/client/script/ui/shared/infoPanels/infoPanelSvc.py:395-397`, and `operations/client/tutorialOperationsController.py:49-52` confirm the server notification order. Updated the wire assertions; both tests passed locally.
- The test-only correction was pushed as signed commit `d59083f6`. Main CI runs 1426 (`41449055096`), 1427 (`41450500732`), 1428 (`41454191541`), 1429 (`41457405065`), 1430 (`41461751365`), 1431 (`41467764632`), 1433 (`41471425498`), feature push run 1434 (`41477534789`) and newer main run 1435 (`41479014042`, SHA `a78bef65`) all completed successfully, including Windows and headless end-to-end jobs. Focused Agent test 1251 (`41477586521`) passed. Run 1432 was a gate-only run with all substantive jobs skipped and is not counted as verification.
- Merged main through `bc1f8141`; `Eve.Host` and its project references build with 0 warnings/errors. On `eve_p8`, after the final merge commit, DB-locked tests passed: `JumpCloneStoreTests` 6/6, `JumpCloneServiceTests|JumpCloneShipInstallTests` 7/7, and `CharacterHostTests|ServiceAuditGtoMHostWireTests` 25/25, all with 0 skips. Space tests (66/66) were run before the later unrelated main updates.
- The atomic clone cooldown change was signed as `76765008` and pushed to main in signed merge commit `e4239ab4`. Newer main CI run 1435 (`a78bef65`) includes the pushed change and completed successfully.
