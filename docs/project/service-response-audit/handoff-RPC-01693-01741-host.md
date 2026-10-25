# Host coverage handoff: RPC-01693–RPC-01741

## Completed in this increment

Added `Targeted_support_rpcs_keep_the_client_wire_contract_through_host_registration` in `dotnet/tests/Eve.Host.Tests/TargetedRpcHostCoverageTests.cs`. The test starts the real `HostServer`, registers the production `CoreServices` list, sends Macho calls over the test socket, and checks the client-visible result for the seven in-game report methods, `jumpTimers.GetTimers`, and `keeper.GetCurrentDungeonForCharacter`.

The jump-timer call uses a session whose `charid` is 9001 and requests that same character. A zero ID is rejected as `JUMP_TIMER_CHARACTER_INVALID`, matching the server validation rather than the client call.

## Whole-client evidence and EveJS comparison

- `eve/client/script/ui/shared/zhReport/reportSvc.py:35-50` contains the only callers for these report methods. Inappropriate language sends five values (character, comment, chat time, channel, content); the other listed reports send character and comment. The caller does not consume an RPC value; it reports success after the call returns. EveJS v0.12.9 records the method and arguments in `server/src/services/support/ingamereportService.js:87-121`.
- `eve/client/script/ui/services/insurgencyCampaignSvc.py:51-54` unpacks `GetLocalCampaignClientSnapshot` as (solar system ID, snapshot), retries when the ID differs from the current system, and caches the snapshot. Lines 91-117 consume the campaign collection and fields such as `coveredSolarsystemIDs`, `originSolarsystemID`, `pirateFactionID`, and `fsmState`. EveJS returns the campaign list and local pair from `server/src/services/map/insurgencySolarsystemService.js:67-87`.
- `eve/client/script/ui/services/crimewatchSvc.py:606-610` stores `GetTimers(session.charid)` as the timer tuple and replaces it on `OnJumpTimersUpdated`. EveJS validates the requested character against the session and returns the timer tuple in `server/src/services/security/jumpTimersService.js:15-24`.
- `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:541-552` calls `keeper.ActivateAccelerationGate(itemID)` inside `PerformSessionChange`; the call result is not read. EveJS delegates to the active-session gate operation in `server/src/services/dungeon/keeperService.js:992-995`.
- `eve/client/script/parklife/scenarioMgr.py:91-102` calls `Bind` and then `EditDungeon` only when `GetLevelEditor()` returns a truthy object. EveJS returns null from `GetLevelEditor` and records that the editor runtime is disabled (`keeperService.js:997-1001`), so those bound-object methods are unreachable on the normal client path. `eve/client/script/parklife/dungeonTracking.py:65-72` unpacks `GetCurrentDungeonForCharacter` as dungeon ID, room ID, instance ID, and values; EveJS returns the active dungeon tuple or null in `keeperService.js:987-990`.

## Remaining work

The focused regression passed for the listed report methods, jump timers, and the empty current-dungeon state. The Host’s `CoreServices.Create` registers those three services, but does not register `InsurgencySolarsystemService`; that RPC class currently has only service-level tests. `ActivateAccelerationGate` still needs a real Host success-path regression with its provider. The requested `keeper.Bind` / `keeper.EditDungeon` rows describe bound-object calls, while the reference server disables that object; implementing the QA level editor needs a separate supported runtime path.

Edits to existing files could not be made in this increment: Serena `replace_content` and `replace_in_files` both failed because no language servers were available. The repository instructions prohibit using shell edits as a fallback. No database or graphical client was used.
