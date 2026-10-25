# Host RPC coverage handoff: RPC-01956–RPC-01980

## Scope and client trace

The 25 inventory rows map to three `map` service methods. The whole `/home/user/eve-logic` search found the callers and consumers below.

| Rows and method | V24.01 callers and reply consumers |
|---|---|
| RPC-01956–RPC-01974 `map.GetHistory(statID, hours)` | Calls: `eve/client/script/ui/services/sovSvc.py:145-157`; `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:612-663`; `eve/client/script/ui/shared/maps/starModeHandler.py:478-594`; `eve/client/script/ui/shared/mapView/filters/mapFilterPodKills.py:20-21`; `mapFilterFactionKills.py:22-31`; `mapFilterKills.py:21-33`; plus whole-tree callers `mapFilterJumps.py:21-29` and `eve/client/script/ui/services/facWarSvc.py:75-83`. The pinned client constants in `eve/common/lib/appConst.py:2075-2077` are jumps=1, kills=3, faction-war kills=5. Consumers iterate records and read `solarSystemID`, `value1`, `value2`, and `value3`: jumps use `value1`; ship kills use `value1 - value2`; faction kills use `value2`; pod kills use `value3`; sovereignty UI sums ship and pod counts. |
| RPC-01975–RPC-01978 `map.GetIncursionGlobalReport()` | `eve/client/script/ui/shared/agencyNew/contentProviders/contentProviderIncursions.py:41-43` sorts the list by `stagingSolarSystemID`, then builds content from the staging and aggressor faction IDs. `eve/client/script/ui/shared/neocom/journal.py:781-802` reads staging system, influence, update/decay/grace fields, reward group, and aggressor faction for the global report UI. |
| RPC-01979–RPC-01980 `map.GetIndustryCostModifier(activityID)` | `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:1062-1069` renders the returned system-to-index mapping and percentage hint. Other whole-tree callers are `eve/client/script/ui/shared/maps/starModeHandler.py:1238-1249` and `eve/client/script/ui/shared/mapView/filters/mapFilterCostIndex.py:20-26`. |

## EveJS comparison and eve.rs route

- EveJS v0.12.9 `server/src/services/map/mapService.js:591-604` wraps the history rowset in a cached method result with a one-minute version check; `buildHistoryRows` at lines 378-391 selects jump, kill, or faction-war history and emits the four consumed columns.
- EveJS `mapService.js:571-579` reads active incursions and returns `buildIncursionGlobalReport`; the builder in `server/src/services/incursion/incursionPayloads.js:97-107` removes reports without a staging system and serializes the remaining entries.
- EveJS `mapService.js:645-649` returns an activity-specific system mapping; `buildIndustryCostIndexDict` at lines 495-502 includes known-space systems and resolves each activity's index.
- eve.rs registers `IncursionAnomalyService("map")` with the real `MapService` as fallback in `dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:42-50`. `MapService.GetHistory` at `dotnet/src/Eve.Services.Station/StationService.cs:330-336` returns a cached rowset with the same column names and one-minute check. The Incursion service reads active incursions; the Host industry callback connects `GetIndustryCostModifier` to persisted cost indices in `dotnet/src/Eve.Host/Composition/IndustryComposition.cs:608-618` and `dotnet/src/Eve.Host/Persistence/Industry/PersistenceIndustryJobOperations.cs:317-329`.

## Regression

- Added `HostServerTests.Map_history_incursion_and_industry_cost_replies_follow_registered_host_routes` in `dotnet/tests/Eve.Host.Tests/MapRpcHostWireTests.cs`.
- Uses the actual Host TCP server, dispatcher, registered `IncursionAnomalyService`, its real `MapService` fallback, and `machoVersion` kwargs. It asserts cached rowset headers, tuple row values, the one-minute cache metadata, incursion report fields, industry system-index mapping, and four distinct history argument pairs: (jumps, 1h), (kills, 1h/24h), and (faction-war kills, 24h).
- Read-side fixtures are in memory; the test builds a PostgreSQL data-source object with a closed invalid-port destination and does not open a connection. These are reads, so the flow has no state mutation, persistence write, or notification.
- Local verification: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` succeeded. The corrected focused test passed locally (1 passed, 0 failed, 0 skipped).
- GitHub Actions focused run `40235480255` for commit `460599ebb75f0bc260c8d26a390a44aea46df7ad` completed successfully with no failed tests or jobs.
- Push-triggered `ci.yml` run `40235467412` for `460599ebb75f0bc260c8d26a390a44aea46df7ad` completed with failures in three unrelated Host tests: `Character_manager_mutations_persist_through_registered_host_wire` received an error-response packet at `SaveCharacterSetting`; `Ship_fitting_info_and_dirt_timestamp_use_the_registered_host_routes` expected `PyStr` but decoded `PyBytes`; `Insurance_rpc_purchase_lookup_list_and_cancellation_follow_the_persisted_host_state` found no 50% quote. The Required CI gate failed because of these tests. The map RPC test was not among the failures.
- No database connection or graphical-client session was used.
