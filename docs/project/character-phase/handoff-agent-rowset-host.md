# Agent rowset Host coverage handoff

## Status

Host wire regressions are implemented for the client operations represented by RPC-00837 through RPC-00843 and RPC-00864, plus the nested mapping lookup represented by RPC-00865. The first group operates on the GetAgents rowset returned by agentMgr; none of those names is a separate server RPC. RPC-00865 is the dict.get consumer of GetMessagesForEpicArcMissions.

## V24.01 client trace

eve/client/script/ui/station/agents/agents.py loads sm.RemoteSvc('agentMgr').GetAgents().Clone() at line 91. Lines 92–93 add client-local factionID and solarsystemID; lines 94–105 derive them from station metadata, in-space agent data and the corporation faction helper. It then builds:

| Row | Client expression | Evidence / behavior |
|---|---|---|
| RPC-00837 | AddField('factionID', None) | agents.py:92; populated at agents.py:105 |
| RPC-00838 | AddField('solarsystemID', None) | agents.py:93; populated from station/in-space data at agents.py:95-103 |
| RPC-00839 | Filter('stationID') | agents.py:108 |
| RPC-00840 | Filter('solarsystemID') | agents.py:109 |
| RPC-00841 | Filter('corporationID') | agents.py:110 |
| RPC-00842 | Filter('factionID') | agents.py:111 |
| RPC-00843 | Filter('agentTypeID') | agents.py:112 |
| RPC-00864 | Index('agentID') | agents.py:107; local lookup returns the indexed row in agents.py:116-120 |
| RPC-00865 | messages.get(messageType, {}).get(missionID, None) | epicArcController.py:67-68; consumes GetMessagesForEpicArcMissions() |

The second direct server GetAgents caller found in the whole client tree is eve/client/script/ui/shared/info/infosvc.py:2593; it indexes by agentID and reads each row's divisionID at lines 2597-2598. CBM's Agents.__GetAllAgents data-flow trace found the cache's local consumers, including all six indexed/filter views, lookup, career-agent checks, location lookup, epic-arc agent selection and dialogue operations. GetAgentByID has 13 direct callers across the client tree. Examples include standings (standingsUIUtil.py:250-260, standingsvc.py:201-230), journal (journal.py:703), agency (contentProviderEpicArcs.py:56), info windows (infosvc.py:2000) and epic-arc sorting (epicArcController.py:473-478). Those are local cache consumers, not RPC calls to agentMgr.GetAgentByID.

## EveJS comparison and eve.rs path

EveJS v0.12.9 server/src/services/agent/agentMgrService.js:787-833 returns a mutable rowset; it includes the 15 identity/type/station/corporation fields plus solarSystemID, dungeonID, spawnPointID and typeID. Its Handle_GetAgentByID at 1415-1423 returns a row and materializes it for the session. The client callsites above determine the fields needed for these nine client expressions; the indexed client fields are present in the rowset, and the two added fields are explicitly derived client-side.

eve.rs registers AgentMissionService through the Host registry. GetAgents reads the catalog callback, sorts by AgentId, and emits the 15-column rowset (dotnet/src/Eve.Services.Core/AgentMissionService.cs:57-63, 463-471, 555-572). Production supplies this callback from agentMissionAgentRows in dotnet/src/Eve.Host/Composition/MissionComposition.cs:193-195; those rows are built by AgentCatalog from SDE character/agent records in dotnet/src/Eve.Host/Composition/AgentCatalog.cs:17-20, 56-74. GetMessagesForEpicArcMissions maps the catalog's nested values to numeric inner keys (AgentMissionService.cs:500-516).

The existing docs/reference/compat/DEVIATIONS.md entry “Agent GetAgents wire columns” records the 15-column choice against EveJS 261018.1 and the existing service-level test. This batch adds Host socket checks for the 15-column sorted rowset and the epic-arc nested mapping so the actual registry, dispatcher, marshal encode/decode and reply path are covered.

## Verification

- MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore — passed, 0 warnings and 0 errors.
- MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter 'FullyQualifiedName~Agent_mgr_' -p:UseSharedCompilation=false — passed, 4 tests, 0 skipped. The filter includes two existing service tests and the two new Host socket tests.
- No PostgreSQL database was used.
- Graphical-client acceptance was not performed.

## Next

Commit with the requested author, fetch and merge origin/main, push HEAD:main, update this handoff with the pushed SHA and Actions run results, then dispatch the focused Eve.Host.Tests filter and inspect both the selected run and push-triggered CI.
