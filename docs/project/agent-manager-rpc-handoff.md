# Agent manager Host coverage reconciliation

Date: 2026-10-24

## Result

The listed coverage rows describe code already present on the current main base, or methods executed by the V24.01 client on returned Python objects. No new Host method is appropriate for this batch.

The production path is composed in dotnet/src/Eve.Host/Composition/MissionComposition.cs:233, where AgentMissionService is connected to the mission GameStore overlay, bookmark records, research state, the SDE agent catalog, and static mission data. dotnet/src/Eve.Host/Rpc/AgentRpc.cs:76-80 includes that same service in AgentRpcServices(); Name == "agentMgr", HasMethod, and CallAsync dispatch are in dotnet/src/Eve.Services.Core/AgentMissionService.cs:44-76. Catalog calls are read-only. Journal reads combine persisted mission records, character bookmarks, and research rows at AgentMissionService.cs:117-147. Character-scoped mission status and completed-career reads use the selected session character.

## Row disposition

| Rows | Finding |
|---|---|
| RPC-00744–00745 agentMgr.AddField | Client-side Rowset.AddField, not an RPC. agents.py:91-93 calls it on GetAgents' result; implementation is eve/common/script/sys/rowset.py:81-90. |
| RPC-00746–00750 agentMgr.Filter | Client-side Rowset.Filter, not an RPC. agents.py:108-112 calls it on the same local rowset; implementation is rowset.py:211-227. |
| RPC-00751 agentMgr.GetAgents | Implemented as a 15-column rowset from the SDE-backed catalog in AgentMissionService.cs:45-51, 149-157, 241-262. Remote callers: agents.py:91 and infosvc.py:2593. The client adds faction/system columns and indexes/filters locally at agents.py:92-112. The 15-column choice is recorded in docs/reference/compat/DEVIATIONS.md under “Agent GetAgents wire columns”. |
| RPC-00752 GetCompletedCareerAgentIDs | Implemented using selected-character mission state and caller-provided IDs, returning an ID-keyed boolean dict (AgentMissionService.cs:167-176). Client caller: agents.py:218. |
| RPC-00753 GetDisabledMissions | Implemented through the static mission authority callback (AgentMissionService.cs:211-219; MissionComposition.cs wires AgentMissionStaticAuthority.DisabledMissionIds). Client caller: agents.py:358-359. |
| RPC-00754 GetMessagesForEpicArcMissions | Implemented from loaded client mission-message maps (AgentMissionService.cs:221-234; MissionComposition.cs wires ReadEpicArcMessages). Client caller: epicArcController.py:67; the nested mapping is consumed at lines 68-69. |
| RPC-00755 GetMyEpicArcStatus | Implemented from current-character mission state (AgentMissionService.cs:236-244). Client caller: epicArcSvc.py:31; lines 38-44 pass the returned status map to EpicArcController. |
| RPC-00756 GetMyJournalDetails | Implemented from selected-character persisted missions, bookmark state, and research state, with bound calls filtered to that agent (AgentMissionService.cs:78-147; EveJS v0.12.9 agentMgrService.js:868-878). Client callers: journal.py:311, 324, 330; full and partial refreshes consume mission tuple field 6 at lines 312-314 and 334-338. |
| RPC-00758 GetSolarSystemOfAgent | Implemented from the agent catalog, returning system ID or None (AgentMissionService.cs:293-311; MissionComposition.cs wires ReadSolarSystemOfAgent). Client callers: agents.py:798-801 and infosvc.py:2020. |
| RPC-00759 agentMgr.Index | Client-side Rowset.Index, not an RPC. infosvc.py:2593 applies it to GetAgents(); implementation is rowset.py:195-209. |
| RPC-00760 agentMgr.get | Client-side mapping lookup, not an RPC. epicArcController.py:68-69 calls get on GetMessagesForEpicArcMissions()' result. |

## EveJS comparison and existing checks

EveJS v0.12.9 implements the corresponding server reads in server/src/services/agent/agentMgrService.js:787-904: GetAgents (787-833), GetSolarSystemOfAgent (835-838), GetCompletedCareerAgentIDs (840-848), GetDisabledMissions (850-852), GetMyJournalDetails (868-878), GetMyEpicArcStatus (888-892), and GetMessagesForEpicArcMissions (894-904). Rowset and dictionary methods are supplied by the client, not EveJS RPC handlers.

Existing regressions cover rowset columns and location scalar in dotnet/tests/Eve.Services.Core.Tests/AgentMissionServiceTests.cs:328-358, journal shape/state in lines 10-223, and non-empty disabled-mission/epic-arc static data in dotnet/tests/Eve.Services.Core.Tests/AgentMissionStaticAuthorityTests.cs:10-82. dotnet/tests/Eve.Host.Tests/AgentCatalogSdeTests.cs covers SDE-derived catalog data. AgentMissionDispatchTests exercises Macho packets through a registry in dotnet/tests/Eve.Host.Tests/AgentMissionDispatchTests.cs:51-139; it does not build the full Host composition. No graphical-client run is recorded by this reconciliation.

## Whole-client call and consumer evidence

The whole V24.01 source search found the remote calls above and downstream consumers in agents.py:91-112, 218, 358-359, 798-801; infosvc.py:2020, 2593; journal.py:311-338; epicArcSvc.py:31, 38-44; and epicArcController.py:67-69. Rowset.AddField, Filter, and Index definitions are in eve/common/script/sys/rowset.py:81-90, 195-227. These references account for all listed rows; no matching client service call exists for the local object methods.

## Verification and continuation

- Current source review found all server RPCs already wired; this batch required no handler changes.
- Actions run 40080641124 for Eve.Services.Core.Tests with filter FullyQualifiedName~Agent_mgr_ completed successfully for commit 9098d1a6. The selected test set did not access PostgreSQL.
- tools/dev/ci-status.py failures 9098d1a6 reported that no ci.yml run was found at the time of the check.
- Latest source checkpoint: 9098d1a6a4c4. This handoff refresh records the focused run outcome and is included in the following push.
