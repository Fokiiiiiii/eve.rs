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

The initial push-triggered CI run 40085520257 exposed a Space regression: `BeltRatsHostTests.A_refused_belt_spawn_is_reported_as_a_failure` expected a log entry for `NO_SCENE 30000142:0:0`, but the shared spawn helper suppressed it without a recovery callback. Updated `SpaceSessions.Npc.cs` to log that refusal when no callback handles it. The Space test project compile-check passed with 0 warnings/errors; focused Actions run 40089273203 passed, and its database-backed `PgFact` executed rather than skipped.

The final focused HostServer Actions run 40090276092 passed with no failed tests or jobs. An earlier dispatch using the literal `HEAD` failed at checkout before test execution; the rerun used commit `11c7a4f34805668b8016e8f36413ae3f01f6ec84`.

Push CI run 40090198052 failed in the unrelated live structure flow (`tools/headless-client/tests/test_structures.py:490`): `structureDeployment.Anchor` returned `CustomNotify` / `You must be in space to deploy a structure.` after the test's undock and warp setup. `.NET (space tests 2/2)` was cancelled after the overall gate failed. The fast live run also skipped eight slow cases and one skill-training-speed check; these are not passes. This structure-path failure has not been fixed. Focused HostServer run 40092661527 on commit `a0e22606c543` passed with no failed tests or jobs. `ci-status.py failures` found no `ci.yml` run for that commit at the time of the check; the earlier structure-path failure remains the only recorded push-gate result.
