# Handoff: map RPC Host coverage (RPC-01956–01980)

## Result

All requested methods are registered on the real `map` Host service through `IncursionAnomalyService` and its `MapService` fallback. Their existing socket-level regression is `Map_history_incursion_and_industry_cost_replies_follow_registered_host_routes` in `dotnet/tests/Eve.Host.Tests/MapRpcHostWireTests.cs`. It sends transport-added `machoVersion`, decodes actual Host replies, covers `GetHistory` inputs `(1,1), (3,1), (3,24), (5,24)`, checks the cached rowset and one-minute version check, and exercises populated incursion and cost replies.

Added `Map_industry_cost_wire_reply_preserves_solar_system_dictionary_keys` in `dotnet/tests/Eve.Host.Tests/MapIndustryCostHostWireTests.cs` to assert the `GetIndustryCostModifier` reply maps the solar system ID to its numeric cost. The prior host test checked the dict value but did not pin the key.

RPC-01956–01974 share the tested history contract; RPC-01975–01978 share the tested incursion report contract; RPC-01979–01980 share the tested cost-index contract.

EveJS v0.12.9 matches these contracts: `server/src/services/map/mapService.js:591-604` builds history with the one-minute cache; `:571-575` builds the report from active incursions; `:645-649` returns the activity-specific cost dictionary. History row types/selection are in `server/src/services/map/mapService.js:378-391`; report fields are built in `server/src/services/incursion/incursionPayloads.js:57-95`.

## Client evidence

- History consumers use `solarSystemID`, `value1`, `value2`, and `value3`: `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:612-660`; `eve/client/script/ui/shared/maps/starModeHandler.py:479-575`; `eve/client/script/ui/shared/mapView/filters/mapFilterFactionKills.py:23-27`; `mapFilterPodKills.py:20-26`; `mapFilterKills.py:25-30`; `eve/client/script/util/clientPathfinderService.py:238-246`; `eve/client/script/ui/services/sovSvc.py:145-151`; `eve/client/script/ui/services/facWarSvc.py:75-79`.
- Incursion callers sort/use `stagingSolarSystemID` and `aggressorFactionID`: `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderIncursions.py:40-56`. Journal uses `rewardGroupID`, `stagingSolarSystemID`, `influence`, `lastUpdated`, `graceTime`, and `decayRate`: `eve/client/script/ui/shared/neocom/journal.py:780-795`.
- Cost index callers consume a system-keyed mapping and its values: `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:1061-1067`; `eve/client/script/ui/shared/maps/starModeHandler.py:1237-1246`; `eve/client/script/ui/shared/mapView/filters/mapFilterCostIndex.py:20-25`.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter 'FullyQualifiedName~Map_history_incursion_and_industry_cost_replies_follow_registered_host_routes' -p:UseSharedCompilation=false`: passed, 1/1, no skips.
- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter 'FullyQualifiedName~Map_industry_cost_wire_reply_preserves_solar_system_dictionary_keys' -p:UseSharedCompilation=false`: passed, 1/1, no skips.

The tests used no PostgreSQL database. The graphical V24.01 client was not exercised.
