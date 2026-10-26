# Host coverage handoff: RPC-01355–01389 subset

## Result

Added real Host wire coverage for custom agency warp, development indices, and dungeon editor/cache reads. Implemented the missing per-system development-index read from persisted sovereignty state and accepted keyword-only `dungeonID` requests for dungeon and room reads.

`RPC-01379` and `RPC-01380` are false-positive service rows: whole-client inspection shows the cited `dungeoneditor.py:197-198` calls are local `list.sort()` and `list.insert()` after `DEGetArchetypes()`; they are not remote RPCs, so no Host service was added.

## V24.01 client callers and consumers

| Rows / methods | Caller and consumed data |
| --- | --- |
| 01355 `custom_agency_provider.warp_to_content` | `eve/client/script/ui/shared/agencyNew/contentPieces/customContentPiece.py:44-46` sends `agency_content_id`; the return is ignored. |
| 01356–01357 `devIndexManager.GetAllDevelopmentIndices` | `eve/client/script/ui/services/sovSvc.py:235-243` maps `solarSystemID`, military/industrial points and `claimedFor` into the star map. Callers: `starModeHandler.py:37`, `mapViewColorHandler.py:42`, `mapFilterDevIndex.py:17`. |
| 01358 `devIndexManager.GetDevelopmentIndicesForSystem` | `sovSvc.py:210-234,293-304` reads attributes 1583/1584/1615 and treats absent entries as zero; `eve/client/script/ui/shared/info/panels/panelSov.py:33` calls it. |
| 01363–01364 `dungeon.DEGetArchetypes`; 01371–01372 `DEGetFactions` | `eve/client/script/ui/inflight/dungeoneditor.py:196-205` sorts archetype name/ID pairs and resolves faction IDs through `cfg.eveowners`. |
| 01365–01370 `dungeon.DEGetDungeons` | `dungeoneditor.py:141-155` passes keyword filters `archetypeID`/`factionID` and consumes dungeon fields; `:223-224,1379-1380` selects the first row for `dungeonID` and reads its name fields. |
| 01373–01376 `dungeon.DEGetRoomObjectPaletteData` | `dungeoneditor.py:1102-1127,1439-1478` consumes either `(id,name)` pairs or grouped dictionary entries and stores the selected ID as the room object's type. |
| 01377–01378 `dungeon.DEGetRooms` | `dungeoneditor.py:236-253` passes keyword `dungeonID` and iterates `(roomID, roomName)` tuples. |
| 01379–01380 `dungeon.insert/sort` | `dungeoneditor.py:197-198` are local list operations, not remote calls. |
| 01381–01382 `dungeonExplorationMgr.GetMyEscalatingPathDetails` | `eve/client/script/ui/shared/neocom/journal.py:185-200,247-251` indexes rows by `instanceID`, refreshes on escalation changes, and scatters `OnEscalationsDataUpdated`; `contentProviders/contentProviderEscalations.py:55-69` filters on `destDungeon` and invalidates Agency content. |
| 01389 `dungeonInstanceCacheMgr.GetFactionWarfareBattlefieldInstances` | `eve/client/script/ui/services/fwWarzoneSvc.py:161-162` forwards the returned grouped instance dictionary; whole-client search found this as the sole remote caller. |

Whole-tree client searches found no additional callers of these RPC names. CBM graph tracing confirms the sovereignty service consumers; Serena source search was used for dynamic remote calls not represented by graph edges.

## EveJS v0.12.9 comparison

- Development-index route and payload: `server/src/services/map/devIndexManagerService.js:19-37`, `sovPayloads.js:66-89`, state calculation `sovState.js:3376-3393`. eve.rs reads persisted sovereignty state via `PersistenceSovFwReadModel`, registered by `StationRpc`.
- Dungeon editor resources and filters: `server/src/services/dungeon/dungeonService.js:172-213`. eve.rs keeps the list/row shapes and handles the client's keyword-only `dungeonID`.
- Escalation response: `server/src/services/dungeon/dungeonExplorationMgrService.js:86-105` returns the cached empty six-column rowset used by eve.rs.
- Battlefield instances: `server/src/services/dungeon/dungeonInstanceCacheMgrService.js:679-682` returns grouped entries; eve.rs uses the active persisted dungeon snapshot.
- Custom agency warp: `server/src/services/agency/customAgencyProviderService.js:335-359` calls the configured warp callback and returns null when none is configured. The Host wire test verifies callback delivery and the null result.

## Checks

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -p:BuildInParallel=false -m:1 --no-restore --filter FullyQualifiedName~Development_indices_are_returned_from_persisted_state_through_registered_rpc_routes` — 1 passed, 0 skipped. This also compiled the changed Host, Services.Core and test projects.
- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false -m:1 --no-restore` — 0 warnings, 0 errors.
- GitHub Actions selected checks and push-triggered CI are pending.
- No database or graphical-client verification was performed.
