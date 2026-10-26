# Host RPC coverage handoff: RPC-01981–RPC-02006

## Client callers and consumers

A whole-tree search of the V24.01 `/home/user/eve-logic` checkout found these callers:

| Rows and RPC | Callers and client use |
|---|---|
| RPC-01981–RPC-01984 `map.GetIndustryCostModifier(activityID)` | `eve/client/script/ui/shared/maps/starModeHandler.py:1239-1251` computes the min/max cost map and displays each system's percentage; `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:1063-1070` passes the system-to-index dictionary to map coloring; `eve/client/script/ui/shared/mapView/filters/mapFilterCostIndex.py:21-29` colors the returned mapping and formats the value as a percentage. The filter subclasses select manufacturing, research time/material, copying, invention and reaction activity IDs. |
| RPC-01985–RPC-01990 `map.GetIndustryJobsOverLast24Hours(activityID)` | `starModeHandler.py:1224-1235`, `mapViewColorHandler.py:1051-1059` and `mapFilterJobs24Hours.py:24-35` consume rows with `solarSystemID` and `noOfJobs`; both map view paths guard empty results. |
| RPC-01991–RPC-01996 `map.GetMyExtraMapInfo()` | `starModeHandler.py:769-778`, `mapViewColorHandler.py:799-812` and `mapFilterCorpMembers.py:28-43` group each row's `characterID` by `locationID`; the map-view code restricts locations to solar systems. |
| RPC-01997–RPC-02002 `map.GetMyExtraMapInfoAgents()` | `starModeHandler.py:801-833`, `mapViewColorHandler.py:819-848` and `mapFilterMyAgents.py:22-61` index the rowset by `fromID`, read `rank` for faction/corporation/agent standings, then use agent eligibility and station/system data to render available agents. |
| RPC-02003–RPC-02004 `map.GetRecentSovActivity()` | `sovSvc.py:268-270` returns the result. Its consumers are `starModeHandler.py:161-181`, `mapViewColorHandler.py:166-186`, `mapFilterSovChanges.py:76-103` and `starMapSvc.py:2534-2538`. Sovereignty coloring filters entries with `stationID is None` and reads `solarSystemID`, `ownerID` and `oldOwnerID`; tile layout receives the full change list. |
| RPC-02006 `map.GetSolarSystemVisits()` | Whole-tree callers: `starModeHandler.py:103-117`, `mapViewColorHandler.py:108-122` and `mapFilterVisited.py:17-36`. The visited filter reads `lastDateTime`, `solarSystemID`, and `visits`, sorts visits, computes the maximum, and builds per-system colors and hints. |

These are the complete method-name callsites returned by the whole-tree V24.01 search; the paths above also show where each reply is used.

## EveJS and Host state comparison

- EveJS v0.12.9 `server/src/services/map/mapService.js:638-659` builds the industry-job rowset and cost-index mapping, and returns empty corp-member and agent-standing rowsets. `mapService.js:696-699` builds recent sovereignty activity from its list function.
- EveJS `mapService.js:606-613` obtains solar-system visits for the session character. `server/src/services/map/mapTelemetryState.js:171-213,255-290` records and aggregates visit data; `server/src/space/runtime/scene/sessions.js:481-490` records a visit when a session attaches to a system.
- eve.rs registers the real `map` route as an `IncursionAnomalyService` with `MapService` fallback in `dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:41-52`. Industry reads and recent sovereignty activity are connected through the Host read provider in `dotnet/src/Eve.Host/Composition/IndustryComposition.cs:600-622`. Corporation member rows are derived from current corporation sessions in `DungeonRpc.cs:45-50`; the service filters invalid IDs, deduplicates and sorts them in `dotnet/src/Eve.Services.Station/StationService.cs:346-362`.
- Agent standings retain the EveJS empty-rowset behavior and its consumed columns. Solar-system visits currently return the correct empty rowset shape, but eve.rs has no visit recorder/read source wired to this route; unlike EveJS, it does not yet return a character's visit history. The wire regression records that limitation and does not claim visit-state parity.

These are read-only calls. They do not mutate state, persist a write, emit notifications, or require a post-response transition; the map UI consumes their returned rows directly.

## Regression and checks

Added `Map_personal_and_industry_replies_follow_registered_host_routes_with_client_kwargs` to `dotnet/tests/Eve.Host.Tests/MapRpcHostWireTests.cs`. It opens a real Host TCP session, calls the registered `map` route with transport-added `machoVersion`, checks both industry activity IDs 1 and 3, validates the cost dictionary and job rowset, checks live corporation-member output, verifies sovereignty columns and values, and asserts the current empty agent and visit rowsets. Its read provider fixtures are test-only; the configured data source points at port 1 and no PostgreSQL connection is opened.

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings, 0 errors.
- Focused test `FullyQualifiedName~Map_personal_and_industry_replies_follow_registered_host_routes_with_client_kwargs`: passed, 1 passed, 0 failed, 0 skipped.
- No database or graphical-client session was used. The test verifies wire routing and shapes, not live database queries or visit history accumulation across a character's travel.
- After merging `origin/main` at `d5414e3f`, the same focused test passed again on the merged tree (1 passed, 0 failed, 0 skipped).
- After the subsequent upstream merge `473b4da9`, the focused test was rebuilt and passed again (1 passed, 0 failed, 0 skipped).

## CX9 whole-client review and focused verification (2026-10-26)

No production code change was needed: the existing focused regression already covers these rows through the Host socket and registered map fallback. The test sends the transport-added `machoVersion` keyword, calls industry methods for activity IDs 1 and 3, checks populated cost/job/sov replies and corporation-session rows, and verifies the agent/visit rowset headers with their current empty results (`dotnet/tests/Eve.Host.Tests/MapRpcHostWireTests.cs:181-315`). The RPCs are reads, so these calls do not mutate or persist state or emit notifications.

A fresh whole-client `search_code` over `home-user-eve-logic` found every direct caller for the six methods: cost index in `mapFilterCostIndex.py`, `mapViewColorHandler.py`, `starModeHandler.py`; industry jobs in `mapFilterJobs24Hours.py`, `mapViewColorHandler.py`, `starModeHandler.py`; corporation members in `mapFilterCorpMembers.py`, `mapViewColorHandler.py`, `starModeHandler.py`; agents in `mapFilterMyAgents.py`, `mapViewColorHandler.py`, `starModeHandler.py`; recent sovereignty in `sovSvc.py`; visits in `mapFilterVisited.py`, `mapViewColorHandler.py`, `starModeHandler.py`. The downstream consumers and exact line references are recorded above: cost values at `starModeHandler.py:1239-1251`, `mapViewColorHandler.py:1063-1070`, `mapFilterCostIndex.py:21-29`; job row fields at `starModeHandler.py:1224-1235`, `mapViewColorHandler.py:1051-1059`, `mapFilterJobs24Hours.py:24-35`; member grouping at `starModeHandler.py:769-778`, `mapViewColorHandler.py:799-812`, `mapFilterCorpMembers.py:28-43`; agent rank use at `starModeHandler.py:801-833`, `mapViewColorHandler.py:819-848`, `mapFilterMyAgents.py:22-61`; sovereignty rows at `sovSvc.py:268-270`, `starModeHandler.py:161-181`, `mapViewColorHandler.py:166-186`, `mapFilterSovChanges.py:76-103`, `starMapSvc.py:2534-2538`; visit fields at `starModeHandler.py:103-117`, `mapViewColorHandler.py:108-122`, `mapFilterVisited.py:17-36`.

EveJS comparison remains as documented above: `mapService.js:638-659,696-699`, and visit aggregation/recording at `mapTelemetryState.js:171-213,255-290` and `sessions.js:481-490`. The current eve.rs visits reply retains the client rowset shape but has no visit-history state source; the regression verifies its present empty result, not populated visit parity.

Verified locally on 2026-10-26: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --filter 'FullyQualifiedName~Map_personal_and_industry_replies_follow_registered_host_routes_with_client_kwargs'` passed (1 passed, 0 failed, 0 skipped). No database or graphical client was used. Focused Actions and push-gate checks are pending for the CX9 commit.

## Push-gate follow-up

The push-triggered `ci.yml` run `40244510313` for `ba1cc8ee49d2fea67c25d52b5646b7cc765e1319` failed only in `InsuranceRpcHostWireTests.Insurance_rpc_purchase_lookup_list_and_cancellation_follow_the_persisted_host_state`: its fixture expected `PyReal(1_000_000)`, while the service returned `PyInt(1_000_000)`.

This was a test expectation error. `dotnet/src/Eve.Services/Py.cs:27-28` maps integral numeric values to `PyInt`, matching EveJS `server/src/network/tcp/utils/marshal.js:160-175`, which encodes integer-valued JavaScript numbers as integers. The V24.01 insurance UI consumes the result numerically: `eve/client/script/ui/station/insurance/base_insurance.py:172-186` compares and uses it to calculate quote rows. The test now expects `PyInt(1_000_000)`; production behavior is unchanged.

The updated `Eve.Host.Tests` project build passed with 0 warnings and 0 errors. The database-backed insurance test was not exercised locally; no database was used. The corrected database-backed insurance test passed in focused Actions run `40249225940` (PostgreSQL 18 service). Push-triggered `ci.yml` run `40249214500` for `c3e7fc7d0f4f35740dde155f4b4bd6e7b3a01514` succeeded with no failed tests or jobs.
