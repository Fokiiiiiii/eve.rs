# Agent manager RPC coverage handoff: RPC-00864–00865

## Disposition

Both rows are client-side collection operations, not missing remote service methods:

- RPC-00864 `agentMgr.Index`: `Index` is called on the Rowset returned by `agentMgr.GetAgents`.
- RPC-00865 `agentMgr.get`: `get` is called on the nested mapping returned by `agentMgr.GetMessagesForEpicArcMissions`.

The server service calls already have real Host registration and implementations. Added a wire regression that sends both RPCs through a composed Host and checks the serialized reply shapes and consumed values.

## Client evidence

- `eve/client/script/ui/station/agents/agents.py:91-113` calls `GetAgents().Clone()`, adds optional fields, then uses `Index('agentID')` at line 108 and filters the indexed rows by station/system/corporation/faction/type.
- `eve/client/script/ui/shared/info/infosvc.py:2593-2607` is another `GetAgents` caller; it indexes by `agentID` and reads division values.
- `eve/common/script/sys/rowset.py:195-209` implements `Rowset.Index` locally. `rowset.py:363-370` and `457-464` likewise define local `get` behavior for rowset-derived objects.
- `eve/client/script/ui/shared/epicArcs/epicArcController.py:67-69` calls `GetMessagesForEpicArcMissions`, then `messages.get(messageType, {}).get(missionID, None)`. At lines 81-87 the consumer assigns chapter title message IDs and passes them to `SetChapterID`. The title category is `messages.epicMission.journalText.chapterTitle` (`eve/common/script/evemissions/common/epic_arcs_data.py:14`).
- No client service call to `agentMgr.Index` or `agentMgr.get` exists: these are ordinary Python methods on returned objects.

## EveJS comparison

EveJS v0.12.9 `server/src/services/agent/agentMgrService.js:787-833` builds the GetAgents Rowset, and `:894-904` returns the epic arc message mapping. `server/src/services/agent/missionAuthority.js:1063-1065` supplies that mapping; `agentMgrService.js:210-222` constructs its outer string-keyed category map and nested numeric mission mapping. eve.rs preserves the client-consumed shapes. Its GetAgents column set differs from EveJS's 19-column rowset; the existing compatibility record documents the observed eve.rs set and its consumers.

## Host implementation and verification

The registered implementation is assembled by `dotnet/src/Eve.Host/Composition/MissionComposition.cs:233-260`, exposed by `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:76-80`, and included in the character service list at `dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:66`. `dotnet/src/Eve.Services.Core/AgentMissionService.cs:44-76` dispatches the service calls; its GetAgents and epic-arc mapping implementations are at lines 149-157 and 221-234.

Regression test: `dotnet/tests/Eve.Host.Tests/AgentCatalogHostWireTests.cs` composes a HostServer with the real AgentMissionService registered in ServiceRegistry, opens a loopback client session, sends both RPC packets, and validates decoded wire results. Providers are deterministic in-memory test data; no database is used.

## Verification

- Passed: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -m:2 -p:UseSharedCompilation=false` (0 warnings, 0 errors on final build).
- Passed: focused `AgentCatalogHostWireTests.Agent_rowset_and_epic_arc_message_mapping_are_returned_through_registered_host_wire` (1 passed, 0 skipped).
- Passed: GitHub Actions run 40085965152, 368 Agent test; focused test passed, no failed tests/jobs.
- Push-triggered `ci.yml` run 40085959477 for commit 2593e5fa was in progress when last checked.
