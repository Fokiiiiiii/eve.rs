# Agent rowset Host coverage handoff

## Resolution

All nine rows are already covered by the two real agentMgr read routes. RPC-00837–00843 and RPC-00864 name client-local operations on the Rowset returned by GetAgents; they are not separate Macho methods. RPC-00865 names Python dict.get on the nested mapping returned by GetMessagesForEpicArcMissions. There is no missing Host route for those local operations.

Production registers AgentMissionService through CharacterServicesComposition.AgentRpcServices -> AgentRpc -> CharacterServiceList -> HostComposition. Its HasMethod/CallAsync pair exposes GetAgents and GetMessagesForEpicArcMissions (dotnet/src/Eve.Services.Core/AgentMissionService.cs:55-109). GetAgents reads the agent catalog callback and creates the rowset (AgentMissionService.cs:149-157, 555-572); production data comes from AgentCatalog/SDE through MissionComposition (dotnet/src/Eve.Host/Composition/MissionComposition.cs:193-195; dotnet/src/Eve.Host/Composition/AgentCatalog.cs:17-20, 56-74). Epic Arc message data comes from the mission catalog callback and is returned with numeric mission keys (AgentMissionService.cs:495-506, 283-296).

## V24.01 callers and reply consumers

Whole-tree searches in /home/user/eve-logic found two direct GetAgents callers:

- eve/client/script/ui/station/agents/agents.py:91 clones the reply. Lines 92-105 add client-local factionID and solarsystemID and derive them from corporation faction data, station metadata or in-space agent data.
- That same method builds the ID index and five filters at agents.py:108-113. Their row mapping is: RPC-00864 Index('agentID') at line 108; RPC-00843 Filter('stationID') at 109; RPC-00842 Filter('solarsystemID') at 110; RPC-00840 Filter('corporationID') at 111; RPC-00841 Filter('factionID') at 112; RPC-00839 Filter('agentTypeID') at 113. RPC-00837 and RPC-00838 are AddField('factionID', None) and AddField('solarsystemID', None) at lines 92-93.
- The index feeds GetAgentByID and the other agent lookup/dialogue methods in agents.py:116-285. CBM found 22 callers of __GetAllAgents and 13 internal GetAgentByID consumers. The station, solar-system, corporation, faction and type filters feed the matching getters at lines 131-154; external uses include agencyUtil.py:47,51, contentProviderAgents.py:115, dockedUI/controllers/stationController.py:97-98 and systemMapSvc.py:958-960.
- The other direct GetAgents caller is eve/client/script/ui/shared/info/infosvc.py:2593. It indexes by agentID and reads divisionID at line 2597.

The only direct GetMessagesForEpicArcMissions caller found in the whole client tree is eve/client/script/ui/shared/epicArcs/epicArcController.py:67. RPC-00865 is the local messages.get(messageType, {}).get(missionID, None) at lines 67-68. _ConnectAllNodes consumes chapter-title IDs at lines 72-73; _ConstructNode reads in-progress and completed IDs at lines 83-88 and passes them to EpicArcMissionNode, whose name and hint methods use those message IDs. Missing mapping keys yield None by the client expression.

## EveJS comparison and wire checks

EveJS v0.12.9 AgentMgrService.Handle_GetAgents returns a mutable Rowset with 19 columns (server/src/services/agent/agentMgrService.js:787-833). Its Handle_GetMessagesForEpicArcMissions returns the nested mapping, built with numeric mission keys (agentMgrService.js:894-904, 210-222). eve.rs returns the 15 columns used by the GetAgents client paths; the existing compatibility record “Agent GetAgents wire columns” documents that choice. No behavior difference was introduced for this batch.

AgentMgrHostTests.Agent_mgr_GetAgents_rowset_reaches_the_client_through_host_registration, AgentMgrReadWireTests.Agent_mgr_read_rpcs_are_registered_on_the_host_and_return_client_consumed_shapes and AgentEpicArcHostTests.Agent_mgr_epic_arc_messages_preserve_the_nested_get_lookup_shape_through_host_rpc exercise the actual HostServer TCP dispatcher, ServiceRegistry and MarshalCodec. They check the GetAgents rowset and nested message map rather than calling handlers directly. Production route registration is covered by the Host composition path above.

## Verification

- The focused local Host wire test invocation targeted the GetAgents rowset and Epic Arc nested mapping cases. It was interrupted after over eight minutes of shared-machine compilation; it produced no test result and is not counted as a pass.
- No PostgreSQL database is used for these static read routes.
- The graphical V24.01 client was not launched.
