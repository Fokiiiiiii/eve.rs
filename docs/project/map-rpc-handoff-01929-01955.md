# Map RPC wire handoff: RPC-01929–RPC-01955

## Outcome

Added `Map_history_and_faction_reads_follow_the_registered_map_route_with_client_kwargs` in `dotnet/tests/Eve.Host.Tests/MapRpcHostWireTests.cs`. It sends actual socket packets through `HostServer`, `PacketDispatcher`, and the registered `map` `IncursionAnomalyService`, which delegates to the production `MapService` route. Packets carry the standard `machoVersion` keyword. The test covers two deadspace language arguments, both Edencom ID lists, a populated faction-war zone response, and both populated and empty history rowsets.

The test uses a non-connecting `NpgsqlDataSource` to construct `ReadSide`; the tested methods do not open a database connection. It exercises the MapService read hook as a provider seam for populated state and the production fallback for empty history. It does not start PostgreSQL.

## Client evidence

- `GetDeadspaceComplexMap`: `mapViewColorHandler.py:578` and `starModeHandler.py:444` pass `session.languageID`; each returns early for None, otherwise iterates rows shaped as (solarSystemID, dungeonID, difficulty, dungeonName) (`mapViewColorHandler.py:581-586`, `starModeHandler.py:447-450`). The whole-tree search also found `mapFilterDungeons.py:21-25`, which maps None to an empty filter.
- `GetEdencomFortressSystems` and `GetEdencomMinorVictorySystems`: `mapSvc.py:500-505` forwards both calls; `mapSvc.py:507-508` concatenates the lists for route avoidance. UI filters turn the returned IDs into system-keyed maps (`mapFilterEDENCOMFortress.py:16-17`, `mapFilterEDENCOMMinorVictories.py:18-19`).
- `GetFacWarZoneInfo`: `facWarSvc.py:149-150` forwards factionID. `facWarSvc.py:69-70` reads `systemUpgradeLevel[solarSystemID]`; `facwarCommon.py:207-210` consumes `factionPoints/maxWarZonePoints`; the infrastructure hub invalidates this cached method after a donation-tax change (`infrastructureHub.py:168`).
- `GetHistory`: client callers across the full tree are `clientPathfinderService.py:239-242`, `facWarSvc.py:75-82`, `sovSvc.py:145-155`, `mapFilterJumps.py:21-25`, `mapFilterKills.py:21-27`, `mapFilterPodKills.py:20-25`, `mapFilterFactionKills.py:23-28`, `mapViewColorHandler.py:612-655`, `starModeHandler.py:479-571`, and `launcherContController.py:117-120`. Consumers iterate rows with `solarSystemID` and `value1/value2/value3`: jumps use value1, ship kills use value1-value2, faction kills use value2, and pod kills use value3. The pathfinder uses value3 and caches the version against (statID, hours).

## EveJS and server comparison

EveJS v0.12.9 returns None for deadspace maps (`server/src/services/map/mapService.js:632-636`); its Edencom methods return their lists (`mapService.js:559-569`); its FacWar fallback is a KeyVal with factionID and an empty systemUpgradeLevel (`mapService.js:681-688`); GetHistory wraps a four-column rowset in a cached result with a one-minute version check (`mapService.js:591-604`). Its history builder uses telemetry/kill state for jumps, kills and faction-war kills (`mapService.js:378-391`).

eve.rs registers the map service as an `IncursionAnomalyService` delegating to `MapService` (`dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:44-53`). `StationService.cs:308-336` owns the tested replies and cached history shape. In production, the map read hook supplies faction-war zone state (`IndustryComposition.cs:599-623`, `FactionWarfareComposition.cs:42-55`); this test verifies the hook's wire shape and fallback without replacing the registered handler. No mutation, persistence, or notification is expected for these reads.

## Checks and limits

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` (0 warnings, 0 errors).
- PASS: focused Host wire test (1 passed, 0 failed, 0 skipped; no database).
- Selected GitHub Actions and push-gate results are added after dispatch.
- The graphical client and production database-backed map read models were not exercised. Existing TODOs for missing telemetry history and Edencom data provenance remain open.
