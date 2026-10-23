# Dungeon and keeper client-contract handoff

## Completed in this increment

Added `DungeonKeeperClientContractTests.Escalation_and_operation_queries_preserve_v24_client_shapes` in `dotnet/tests/Eve.Services.Core.Tests/DungeonKeeperClientContractTests.cs`. It calls the services from `CoreServices.Create` and records these client contracts:

- `dungeonExplorationMgr.GetMyEscalatingPathDetails()` returns the cached Rowset with the six EveJS columns: `instanceID`, `dungeon`, `destDungeon`, `srcDungeon`, `expiryTime`, and `journalEntryTemplateID`. The client reads and caches the Rowset in `eve/client/script/ui/shared/neocom/journal.py:248-252`; EveJS v0.12.9 builds that same Rowset in `server/src/services/dungeon/dungeonExplorationMgrService.js:86-105`.
- `keeper.GetCurrentDungeonForCharacter()` returns the four-value tuple consumed as dungeon ID, room ID, instance ID, and dungeon values in `eve/client/script/parklife/dungeonTracking.py:65-72`. EveJS resolves it from the active session in `server/src/services/dungeon/keeperService.js:987-990`.
- `keeper.GetOperationSpawnpointSolarSystem(characterID, siteID)` returns a solar-system ID. The client passes those two IDs in `operations/client/UI/operationDestinationController.py:19-25`; EveJS resolves them with its operation-site argument helper and returns the stored system ID or None in `server/src/services/dungeon/keeperService.js:1003-1012`.
- `keeper.IsOperationSite(siteID)` returns a bool; the operation objective calls it with only the site ID in `operations/client/UI/taskObjectiveData.py:106-114`. EveJS returns the active-session site membership result in `server/src/services/dungeon/keeperService.js:1034-1045`.

## Remaining work

This closes response-shape evidence for these methods only. The production dungeon/operation providers remain future work as recorded in TODO section 3. No live dungeon, operation state, persistence, notification, PostgreSQL, EveJS execution, or graphical-client behavior was exercised by this test.
