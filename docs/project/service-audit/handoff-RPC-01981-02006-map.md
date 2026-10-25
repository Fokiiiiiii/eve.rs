# Map RPC host coverage handoff (RPC-01981–02006)

## Change

Added `Map_reads_return_the_registered_wire_shapes_through_the_host` in `dotnet/tests/Eve.Host.Tests/MapServiceHostTests.cs`. It registers the real `MapService` in the Host `ServiceRegistry`, opens a socket session, sends Macho RPC packets through `PacketDispatcher`, and checks the decoded reply. It covers the six requested methods once each; repeated inventory rows map to the same registered route. The two industry calls include an activity ID plus the transport-added `machoVersion` argument.

The test uses the existing method behavior with no read hook or database access. Map RPCs are read-only, so these calls produce no mutation, persistence write, or notification. Their current empty results are preserved by this regression.

## Whole-client callers and reply consumers

- **GetIndustryCostModifier (RPC-01981–01984):** direct calls at `eve/client/script/ui/shared/maps/starModeHandler.py:1238`, `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:1062`, and `eve/client/script/ui/shared/mapView/filters/mapFilterCostIndex.py:20`. The two color handlers calculate min/max from the dictionary values; the filter hands that mapping to standard map coloring (`starModeHandler.py:1239-1248`, `mapViewColorHandler.py:1063-1072`, `mapFilterCostIndex.py:22-27`). The client expects per-system numeric values; an empty dictionary makes the star-mode min/max calculation fail.
- **GetIndustryJobsOverLast24Hours (RPC-01985–01990):** calls at `starModeHandler.py:1223`, `mapViewColorHandler.py:1050`, and `mapView/filters/mapFilterJobs24Hours.py:23`. Consumers check for rows and read `solarSystemID` and `noOfJobs` (`starModeHandler.py:1224-1232`, `mapViewColorHandler.py:1051-1060`, `mapFilterJobs24Hours.py:24-30`).
- **GetMyExtraMapInfo (RPC-01991–01996):** calls at `starModeHandler.py:768`, `mapViewColorHandler.py:798`, and `mapView/filters/mapFilterCorpMembers.py:27`. Consumers iterate `characterID` and `locationID` rows to group corporation members by system (`starModeHandler.py:769-780`, `mapViewColorHandler.py:799-816`, `mapFilterCorpMembers.py:28-45`).
- **GetMyExtraMapInfoAgents (RPC-01997–02002):** calls at `starModeHandler.py:800`, `mapViewColorHandler.py:818`, and `mapView/filters/mapFilterMyAgents.py:21`. Consumers index rows by `fromID`, then read `rank` for faction, corporation and agent standing checks (`starModeHandler.py:801-810`, `mapViewColorHandler.py:819-828`, `mapFilterMyAgents.py:22-36`).
- **GetRecentSovActivity (RPC-02003–02004):** `sovSvc.py:267` returns the map RPC result directly. The whole-tree consumers are `starModeHandler.py:160`, `mapViewColorHandler.py:165`, `mapView/filters/mapFilterSovChanges.py:75`, and the map tile path `starMapSvc.py:2533`; sovereignty change-list code iterates those rows.
- **GetSolarSystemVisits (RPC-02006):** calls at `starModeHandler.py:102`, `mapViewColorHandler.py:107`, and `mapView/filters/mapFilterVisited.py:16`. Consumers iterate rows and read `lastDateTime`, `solarSystemID`, and `visits` (`starModeHandler.py:103-115`, `mapViewColorHandler.py:108-128`, `mapFilterVisited.py:17-36`).

These are the complete direct callsites found by whole-tree search over `/home/user/eve-logic/eve/client`; the downstream SovService consumers are listed above.

## EveJS comparison

- `server/src/services/map/mapService.js:638-649` builds the industry-job rowset and cost-index dictionary. Jobs are aggregated from recent industry job state at `server/src/services/industry/industryRuntimeState.js:2304-2344`. Cost index uses all known SDE solar systems (`mapService.js:485-502`) and the configured cluster index (`server/src/services/industry/industrySystemCostIndex.js:36-38`).
- EveJS returns empty corporation-member and agent-standing rowsets at `mapService.js:651-659`, matching the shapes in this test.
- Recent sovereignty activity is read from sovereignty state at `mapService.js:696-699` and shaped with columns `solarSystemID, ownerID, oldOwnerID, stationID, changeTime` in `server/src/services/sovereignty/sovPayloads.js:199-211`; the source list is `sovState.js:3409-3412`.
- Visits are read from per-character map telemetry and filtered to public solar systems at `mapService.js:606-613`.

The regression confirms the eve.rs Host wire route and rowset headers. It does not establish parity for populated industry, sovereignty, agent-standing, or visit state. In particular, eve.rs currently returns an empty dictionary for cost indices, while the client min/max code requires at least one value; this remains a client-visible behavior gap and needs an authoritative industry-index source before it can be completed without inventing data.

## ID note

The current generated `CLIENT_CALL_MATRIX.csv` assigns different sequential IDs to these method/path pairs than the IDs supplied in this task. The service-audit handoff already records that generated IDs can shift; this note follows the task's method/path grouping and does not rewrite generated inventory output.

## Checks

- Passed: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` (0 warnings, 0 errors).
- Passed locally: focused `Eve.Host.Tests` filter `FullyQualifiedName~Map_reads_return_the_registered_wire_shapes_through_the_host` (1 passed, 0 skipped).
- Passed on Actions: focused agent-test run `40236372876` at `1b6c08cf`, no failed tests or jobs.
- Push `ci.yml` run `40236366754` is in progress; its result is pending.
- No PostgreSQL database or graphical client was used.
- The first `--no-restore` build attempt reported that this worktree had no NuGet assets; the normal build restored dependencies and passed.
