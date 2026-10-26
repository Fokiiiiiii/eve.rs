# Map RPC wire coverage: RPC-02032–RPC-02057

## Client-wide trace

These row IDs include local operations on objects returned by the `map` service. `Index`, `get`, `iteritems`, and `keys` are consumed client-side; the Host must return the Rowset or dictionary expected by those operations.

- RPC-02032–RPC-02036: the client calls `GetSystemsInIncursions()` from `eve/client/script/ui/shared/mapView/filters/mapFilterIncursion.py:18-23`, `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:952-960,981-987`, `eve/client/script/ui/shared/maps/starModeHandler.py:975-981,1151-1157`, and `eve/client/script/ui/shared/agencyNew/contentPieces/incursionContentPiece.py:86-89`. The UI reads each row's `locationID`, `sceneType`, and `templateNameID`; the agency content piece also builds a location-to-scene-type map.
- RPC-02037–RPC-02038: `eve/client/script/ui/shared/maps/mapSvc.py:511-513` forwards `GetTriglavianMinorVictorySystems()`; its whole-tree consumers are `eve/client/script/ui/shared/mapView/filters/mapFilterTriglavianMinorVictories.py:16-18`, `eve/client/script/util/clientPathfinderService.py:251-252`, and `stargate/client/jump_checks.py:75-80`. They treat the reply as solar-system IDs.
- RPC-02039: `eve/client/script/ui/services/sovSvc.py:257-263` calls `GetCurrentSovData`, indexes rows by `locationID`, and indexes loyalty rows by `solarSystemID` from `GetConstellationLPData`.
- RPC-02041: `eve/client/script/ui/services/uisvc.py:244-247` calls `GetStationInfo`, indexes rows by `stationID`, then uses dictionary `get`; map UI consumers also iterate station rows and read station/system fields (`eve/client/script/ui/shared/maps/starModeHandler.py:693-700`, `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:746-750`).
- RPC-02042–RPC-02050: `eve/client/script/ui/shared/mapView/filters/mapFilterMyAgents.py:21-22,30-38` indexes `GetMyExtraMapInfoAgents` by `fromID`, then uses `get` for faction, corporation, and agent IDs and reads each row's `rank`.
- RPC-02051–RPC-02057: `eve/client/script/ui/shared/mapView/filters/mapFilterCynosuralFields.py:24-43` and `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:666-684` consume `GetBeaconCount` as a dictionary, iterating key/count pairs and using `keys` and `get`; they combine module counts with the separate structure-directory result.

No other V24.01 callers of these named methods or container operations were found in the whole `/home/user/eve-logic` tree.

## EveJS and Host comparison

- EveJS v0.12.9 `server/src/services/map/mapService.js:581-589` builds the incursion Rowset from active incursion state. `server/src/services/incursion/incursionPayloads.js:138-145` defines its columns and row fields. Its Triglavian method returns the system-ID list (`mapService.js:553-557`).
- EveJS returns an empty `fromID, rank` Rowset for agent standings (`mapService.js:656-659`). This is the existing behavior when no standing source is populated.
- EveJS `GetBeaconCount` aggregates active fleet module/deployable beacons and filters to public solar systems (`mapService.js:615-624` -> `fleetHelpers.js:57-59` -> `fleetRuntime.js:1471-1486`). eve.rs currently has no matching fleet beacon state owner or provider; the registered map method falls back to an empty dictionary.
- EveJS `GetCurrentSovData` reads current sovereignty state and serializes it through `buildCurrentSovDataPayload` (`mapService.js:690-694`). The current eve.rs map route is registered in `dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:44-53`; its map service returns the required Rowset types, but current sov and loyalty providers are not wired.
- The station Rowset is read from the pinned SDE via `MapService.GetStationInfo`; the other map reads delegate through the same registered map route to `MapService` or the active-incursion read model.

## Regression added

`dotnet/tests/Eve.Host.Tests/MapRpcContainerHostWireTests.cs` sends actual socket RPCs through the registered `map` route with transport `machoVersion`. It checks active incursion fields, Triglavian IDs, and Rowset/dictionary wire shapes consumed by `Index`, `get`, `iteritems`, and `keys`. The active values are test fixtures passed through the existing read-provider seam; this verifies wire compatibility, not a production beacon or sovereignty source.

## Checks

- PASS: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:BuildProjectReferences=false -p:UseSharedCompilation=false` (0 warnings, 0 errors).
- PASS: `Map_client_container_consumers_receive_reference_shapes_through_the_registered_route` (1 passed, 0 failed, 0 skipped; no database).
- GitHub Actions results will be recorded after dispatch.

## Remaining work

The live active-fleet beacon provider and current sovereignty/loyalty read models remain open. No test database was used and no graphical-client run was performed. The new regression is a headless Host wire check.
