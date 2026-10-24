# agentMgr local collection coverage handoff

## Classification

RPC-00827 `agentMgr.Index` and RPC-00828 `agentMgr.get` are client-side collection operations, not RemoteSvc endpoints; Host should not register them.

V24.01 `Rowset.Index` builds an `IndexRowset` locally (`eve/common/script/sys/rowset.py:195-209`). The agents UI calls `sm.RemoteSvc('agentMgr').GetAgents()`, clones/enriches the rowset, then indexes it locally at `eve/client/script/ui/station/agents/agents.py:91-115) (Index at line 108). The only other direct `GetAgents()` caller in the whole `eve/**/*.py` tree is `eve/client/script/ui/services/infosvc.py:2593); it indexes locally and reads `divisionID` at line 2595.

The only direct `GetMessagesForEpicArcMissions()` caller in the whole `eve/**/*.py` tree is `eve/client/script/ui/shared/epicArcs/epicArcController.py:67). Line 69 uses Python dictionary `.get(messageType, {})` and `.get(missionID, None)` locally. The controller reads those values for chapter titles in `_ConnectAllNodes` (lines 71-81) and in-progress/completed labels in `_ConstructNode` (lines 83-88); missing keys intentionally yield None. Thus the listed lowercase `get` is also local.

## EveJS and Host comparison

EveJS v0.12.9 implements the parent RPCs: `Handle_GetAgents` returns a mutable Rowset (`server/src/services/agent/agentMgrService.js:787-833); `Handle_GetMessagesForEpicArcMissions` returns the nested message dictionary (`:894-904), constructed with numeric mission keys (`:210-222). It has no service methods for `Index` or lowercase `get`.

eve.rs exposes `GetAgents` and `GetMessagesForEpicArcMissions` in `AgentMissionService.HasMethod/CallAsync` (`dotnet/src/Eve.Services.Core/AgentMissionService.cs:55-109`); the reply builders are `ReadAgentsAsync` and `ReadEpicArcMessagesAsync` (`:457-466, 495-506`). Production Host includes this service in `CharacterServicesComposition.AgentRpcServices` (`dotnet/src/Eve.Host/Rpc/AgentRpc.cs`), adds that list in `CharacterServiceList` (`dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs`), and registers the resulting character services in `HostComposition` (`dotnet/src/Eve.Host/Hosting/HostComposition.cs:260-262`).

EveJS v0.12.9 returns 19 GetAgents columns; eve.rs returns the first 15. Both direct V24.01 callers above consume fields among those first 15: the agents UI adds its own system/faction fields, and the info view reads `divisionID`. The four trailing EveJS fields are not consumed by these paths.

## Change and checks

Updated `HostServerTests.Agent_mgr_read_rpcs_are_registered_on_the_host_and_return_client_consumed_shapes` in `AgentMgrReadWireTests.cs` to assert `Index` and lowercase `get` are absent from the real `AgentMissionService` method table before registering it with the running HostServer. The test sends actual Macho calls through the HostServer and checks `GetAgents` and `GetMessagesForEpicArcMissions` replies; it uses in-memory callbacks. Existing service shape tests are `AgentMissionServiceTests.Agent_mgr_agents_and_location_match_the_reference_rowset_and_scalar` and `AgentMissionServiceTests.Agent_mgr_epic_arc_status_and_message_maps_use_numeric_nested_keys`.

Local verification: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -m:1 -p:UseSharedCompilation=false` passed (0 warnings/errors); focused `HostServerTests.Agent_mgr_read_rpcs_are_registered_on_the_host_and_return_client_consumed_shapes` passed (1 passed, 0 skipped). No graphical V24.01 client was launched.

The initial push-triggered CI run 40085520257 was cancelled after `Eve.Services.Space.Tests.BeltRatsHostTests.A_refused_belt_spawn_is_reported_as_a_failure` failed at `BeltRatsHostTests.cs:124`: the simulated reply was `NO_SCENE 30000142:0:0`, but the Host log was empty. In the merged shared spawn helper, `NO_SCENE` was suppressed even when the caller supplied no scene-recovery callback. Updated `SpaceSessions.Npc.cs` so a caller callback handles that case when present; otherwise the refusal is logged. The Space test project compile-check passed with 0 warnings/errors. Focused follow-up Actions run 40089273203 passed with no failed tests or jobs. Its `agent-test.yml` job provisioned PostgreSQL and loaded the pinned SDE; `PgFact` therefore ran the named belt-spawn regression rather than skipping it. `ci-status.py failures` found no push-triggered `ci.yml` run for commit `a1b0b4728472`, so that gate has no result. The updated HostServer RPC test is awaiting its Actions run.
