# Map RPC wire coverage: RPC-02032–RPC-02057

## Client-wide trace

Rows include client-side operations on values returned by the remote map service. Index, get, iteritems, and keys are local Rowset/dictionary operations; the Host contract is the serialized container and its fields.

- RPC-02032–02036: GetSystemsInIncursions callers are eve/client/script/ui/shared/mapView/filters/mapFilterIncursion.py:18-23, eve/client/script/ui/shared/mapView/mapViewColorHandler.py:952-960,981-987, eve/client/script/ui/shared/maps/starModeHandler.py:975-981,1151-1157, and eve/client/script/ui/shared/agencyNew/contentPieces/incursionContentPiece.py:86-89. Consumers read locationID, sceneType, and templateNameID; the agency piece builds location-to-sceneType.
- RPC-02037–02038: eve/client/script/ui/shared/maps/mapSvc.py:511-513 forwards GetTriglavianMinorVictorySystems; whole-tree consumers are eve/client/script/ui/shared/mapView/filters/mapFilterTriglavianMinorVictories.py:16-18, eve/client/script/util/clientPathfinderService.py:251-252, and stargate/client/jump_checks.py:75-80. They use the reply as system IDs.
- RPC-02039: eve/client/script/ui/services/sovSvc.py:257-263 calls GetCurrentSovData, indexes by locationID, and indexes GetConstellationLPData by solarSystemID.
- RPC-02041: eve/client/script/ui/services/uisvc.py:244-247 calls GetStationInfo, indexes by stationID, then uses get; map consumers iterate station rows and read station/system fields at eve/client/script/ui/shared/maps/starModeHandler.py:693-700 and eve/client/script/ui/shared/mapView/mapViewColorHandler.py:746-750.
- RPC-02042–02050: eve/client/script/ui/shared/mapView/filters/mapFilterMyAgents.py:21-22,30-38 indexes GetMyExtraMapInfoAgents by fromID, uses get for faction/corporation/agent IDs, and reads rank.
- RPC-02051–02057: eve/client/script/ui/shared/mapView/filters/mapFilterCynosuralFields.py:24-43 and eve/client/script/ui/shared/mapView/mapViewColorHandler.py:666-684 consume GetBeaconCount as a dictionary: iterate key/count pairs, use keys and get, and combine with the structure-directory reply.

Whole-tree search of /home/user/eve-logic found no other callers for these named methods or container operations.

## EveJS and Host

- EveJS v0.12.9 server/src/services/map/mapService.js:581-589 builds the incursion Rowset from active incursions; server/src/services/incursion/incursionPayloads.js:138-145 defines its columns/fields. The Triglavian method returns its system list at mapService.js:553-557.
- EveJS returns an empty fromID, rank Rowset for agent standings (mapService.js:656-659).
- EveJS GetBeaconCount aggregates active fleet module/deployable beacons and filters public systems (mapService.js:615-624 -> fleetHelpers.js:57-59 -> fleetRuntime.js:1471-1486). eve.rs has no matching fleet beacon state owner/provider; the registered route falls back to an empty dictionary.
- EveJS GetCurrentSovData serializes current sov state (mapService.js:690-694). eve.rs registers map through dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:44-53, but current sov and loyalty providers are not wired.
- Station rows come from the pinned SDE through MapService.GetStationInfo; other tested map reads delegate through the registered route to MapService or the incursion read model.

## Regression and checks

dotnet/tests/Eve.Host.Tests/MapRpcContainerHostWireTests.cs sends socket RPCs through the registered Host map route with transport machoVersion. It checks active incursion fields, Triglavian IDs, and the Rowset/dictionary shapes used by the client container operations. Injected fixtures verify wire shapes; they do not supply production beacon or sov state.

- PASS: Host test project build (0 warnings, 0 errors).
- PASS: focused local test, 1 passed, 0 failed, 0 skipped; no database.
- PASS: selected Actions run [40264747440](https://github.com/Fokiiiiiii/eve.rs/actions/runs/40264747440), focused Host test passed.
- PASS: push gate [40264738950](https://github.com/Fokiiiiiii/eve.rs/actions/runs/40264738950), no failed tests or jobs.
- Code commit: 43c99ec67f740fe74aeee9712d6c9252e1a465de; subsequent handoff update and main merge were pushed.

## Remaining work

Production active-fleet beacon, current sovereignty, and loyalty data providers remain open. No test database or graphical-client run was used.
