# Agent manager RPC coverage handoff

Date: 2026-10-24

## Disposition

No missing agentMgr server method was found in this batch. The production Host constructs AgentMissionService with the mission state overlay, character bookmarks, research rows, SDE agent catalog and static mission data in `dotnet/src/Eve.Host/Composition/MissionComposition.cs:233-260`; `AgentRpc.cs:76-80` returns that service in `AgentRpcServices()`, and `CharacterServiceList.cs:66` includes `AgentRpc()` in the Host service list. `AgentMissionService.cs:44-76` advertises and dispatches all seven server methods below.

## Row disposition and client evidence

| Rows | Finding and V24.01 use |
|---|---|
| RPC-00837–00838 `agentMgr.AddField` | Local `Rowset.AddField`, not an RPC. `agents.py:92-94` adds `factionID` and `solarsystemID` to a cloned GetAgents result; Rowset implementation is `eve/common/script/sys/rowset.py:81-90`. |
| RPC-00839–00843 `agentMgr.Filter` | Local `Rowset.Filter`, not an RPC. `agents.py:109-113` creates filters for station, solar system, corporation, faction and agent type; implementation is `rowset.py:211-227`. |
| RPC-00844–00847 `agentMgr.GetAgents` | Implemented as an SDE-backed 15-column rowset (`AgentMissionService.cs:149-157,241-262`). Whole-tree remote callers are `agents.py:91` and `infosvc.py:2594`. The first clones it, adds client-derived columns, then indexes/filters it at `agents.py:92-113`; the second indexes by agent ID and reads division IDs at `infosvc.py:2593-2607`. |
| RPC-00848–00849 `GetCompletedCareerAgentIDs` | Implemented from the selected character's mission state; returns an ID-keyed boolean mapping (`AgentMissionService.cs:167-176`). Caller `agents.py:219` merges it into the cache, read by `IsCareerAgentCompleted` at `agents.py:223-226`. |
| RPC-00850–00851 `GetDisabledMissions` | Implemented by the static mission authority (`AgentMissionService.cs:211-219`, wired in `MissionComposition.cs`). Caller/property is `agents.py:358-360`. |
| RPC-00852–00853 `GetMessagesForEpicArcMissions` | Implemented from loaded mission-message mappings (`AgentMissionService.cs:221-234`, wired in `MissionComposition.cs`). `epicArcController.py:67-69` reads the nested `messageType -> missionID -> messageID` mapping and accepts missing keys as `None`. |
| RPC-00854–00855 `GetMyEpicArcStatus` | Implemented from selected-character mission state (`AgentMissionService.cs:236-244`). `epicArcSvc.py:31-35` passes the mapping into EpicArcController; its status consumers are at `epicArcController.py:38-44`. |
| RPC-00856–00859 `GetMyJournalDetails` | Implemented from selected-character missions, bookmarks and research rows; bound-agent calls filter to that agent (`AgentMissionService.cs:78-147`). The full caller is `journal.py:312`; the partial path makes bound calls at `journal.py:324` and falls back to the global call at `journal.py:331`. Consumers iterate result list 0 and mission field 6 at `journal.py:312-314,334-338`; list 1 is merged for partial refreshes at `journal.py:345-348`. The outer result is a 2-tuple of mission and research lists. |
| RPC-00862–00863 `GetSolarSystemOfAgent` | Implemented from the agent catalog, returning a system ID or `None` (`AgentMissionService.cs:293-311`, `MissionComposition.cs` wires the catalog reader). Remote caller/cache is `agents.py:799-802`; agent info consumes it at `infosvc.py:2020-2022`. |

These references come from whole-tree searches of V24.01 `/home/user/eve-logic`: each remote call is accounted for above. `AddField` and `Filter` have no remote service call; their row objects are operated on locally. No new route for those names is appropriate.

## EveJS comparison

EveJS v0.12.9 implements the matching reads in `server/src/services/agent/agentMgrService.js`: GetAgents 787-833, GetSolarSystemOfAgent 835-838, GetCompletedCareerAgentIDs 840-848, GetDisabledMissions 850-852, GetMyJournalDetails 868-878, GetMyEpicArcStatus 888-892 and GetMessagesForEpicArcMissions 894-904. EveJS GetAgents sends 19 columns; eve.rs sends the 15 columns consumed by the observed client flow. That compatibility decision is already recorded in `docs/reference/compat/DEVIATIONS.md` under “Agent GetAgents wire columns”. Rowset methods remain client-side in both flows.

## Checks and limits

`dotnet/tests/Eve.Services.Core.Tests/AgentMissionServiceTests.cs` covers selected-character completion state, journal shapes and state, agent rowset columns, and solar-system scalar results; `AgentMissionStaticAuthorityTests.cs` covers non-empty static mission and epic-arc data; `AgentCatalogSdeTests.cs` covers SDE-derived agent records. New `dotnet/tests/Eve.Host.Tests/AgentMissionReadWireTests.cs` registers the real AgentMissionService in the actual ServiceRegistry and dispatches all seven read methods through PacketDispatcher; it checks GetAgents' rowset, character-scoped completion map, disabled list, epic-arc maps, 2-tuple journal, and location scalar. It also checks AddField and Filter are not server methods. The test does not construct full Host composition or the graphical client.

Local `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` succeeded with 0 warnings and 0 errors. The focused `AgentMissionReadWireTests` passed (1 passed, 0 failed, 0 skipped). No database was used.

## Actions

Earlier focused run 40082739789 for `Eve.Services.Core.Tests`, filter `FullyQualifiedName~AgentMissionServiceTests`, succeeded with no failed tests or jobs. The new focused `Eve.Host.Tests` run for `FullyQualifiedName~AgentMissionReadWireTests` is pending dispatch for this commit. The prior `ci-status.py failures` checks found no `ci.yml` run for their tested SHAs.
