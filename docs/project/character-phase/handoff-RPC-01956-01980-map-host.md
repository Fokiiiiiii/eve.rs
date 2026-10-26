# Handoff: map RPC Host coverage (RPC-01956–01980)

## Result

All requested methods are registered on the real `map` Host service through `IncursionAnomalyService` and its `MapService` fallback. The socket-level regression `Map_history_incursion_and_industry_cost_replies_follow_registered_host_routes` in `dotnet/tests/Eve.Host.Tests/MapRpcHostWireTests.cs` sends transport-added `machoVersion`, decodes actual Host replies, covers `GetHistory` inputs `(1,1), (3,1), (3,24), (5,24)`, checks the cached rowset and one-minute version check, and exercises populated incursion and cost replies.

`Map_industry_cost_wire_reply_preserves_solar_system_dictionary_keys` in `dotnet/tests/Eve.Host.Tests/MapIndustryCostHostWireTests.cs` additionally asserts that the `GetIndustryCostModifier` reply maps the solar system ID to its numeric cost.

RPC-01956–01974 share the tested history contract; RPC-01975–01978 share the tested incursion report contract; RPC-01979–01980 share the tested cost-index contract.

The focused history/incursion/cost Host regression passed locally on 2026-10-26 (1 passed, 0 failed, 0 skipped). It creates a data source but performs no PostgreSQL connection. The additional cost-key test had previously been recorded as passing. The graphical V24.01 client has not been exercised.

## Client evidence

Whole-tree search of V24.01 eve-logic found these callers and consumers:

- `GetHistory(statID, hours)`: `mapViewColorHandler.py:612-660` and `starModeHandler.py:479-575` consume `solarSystemID`, `value1`, `value2`, and `value3` for jumps, ship kills, faction kills, and pod kills. The faction-kill, pod-kill, ship-kill, and jump filters consume the same columns [C `mapFilterFactionKills.py:23-27`, `mapFilterPodKills.py:20-26`, `mapFilterKills.py:25-30`, `mapFilterJumps.py:21-25`]. The sov panel aggregates `value1-value2` and `value3` per system [C `sovSvc.py:145-151`]; faction-war danger ranking reads `value1-value2` [C `facWarSvc.py:75-79`]; pathfinder caches systems where `value3>0` using the method-result version [C `clientPathfinderService.py:238-246`]; the shipcaster controller indexes rows by `solarSystemID` [C `launcherContController.py:117-119`]. A four-column rowset and the cached result version serve all of these.
- `GetIncursionGlobalReport()`: the agency sorts/builds content from `stagingSolarSystemID` and `aggressorFactionID` [C `contentProviderIncursions.py:41-56`]. The journal consumes `rewardGroupID`, `stagingSolarSystemID`, `influence`, `lastUpdated`, `graceTime`, and `decayRate` [C `journal.py:780-795`].
- `GetIndustryCostModifier(activityID)`: star-mode and map-view handlers pass its dictionary to coloring; the cost-index filter passes it to per-system coloring [C `starModeHandler.py:1238-1249`, `mapViewColorHandler.py:1062-1069`, `mapFilterCostIndex.py:20-25`]. They use solar-system IDs as keys and numeric modifiers as values.

## EveJS comparison and eve.rs route

EveJS v0.12.9 normalizes the history arguments and wraps the rowset with a one-minute version check [J `server/src/services/map/mapService.js:591-604`]; history column selection is at `:378-391`. Its incursion report reads active incursions [J `:571-575`], with record fields built in `server/src/services/incursion/incursionPayloads.js:57-95`. The industry handler returns an activity-specific cost-index dictionary [J `:645-649`].

eve.rs uses the real registered `map` Host route: `IncursionAnomalyService` owns `GetIncursionGlobalReport` and delegates other map methods to its `MapService` fallback. The Host test asserts the actual wire response, including object-cached history, list-shaped incursion records, and dictionary cost modifiers.

## Verification

- Current run: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter 'FullyQualifiedName~Map_history_incursion_and_industry_cost_replies_follow_registered_host_routes' -p:UseSharedCompilation=false`: passed, 1/1, no skips.
- Previously recorded: `Map_industry_cost_wire_reply_preserves_solar_system_dictionary_keys`: passed, 1/1, no skips.
- No database was used. Real graphical-client behavior was not checked.

## CI follow-up

The earlier push gate reported `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` expected 1420 rows but found 1423 after the initial Host merge. A later `origin/main` merge added 18 `accessGroupBookmarkMgr` routes and one `BookmarkScanResult` route, bringing the expected total to 1442. The inventory assertion was updated in that prior work. The focused inventory test uses database schema setup, so it is checked by Actions rather than locally.
