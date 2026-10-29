# Fleet, corporation, and Local System Chat notification pass

## Status

This pass fixes three client-visible notification issues:

- A pilot who voluntarily leaves a fleet no longer receives an echo of their own OnFleetLeave. Other members still receive the single characterID argument.
- Corporation member changes now go on the wire as three positional RPC arguments: corporationID, memberID, and the change dictionary. The corpID broadcast and direct clientID delivery use the same shape.
- Local chat membership snapshots are sent to the entrant only. In delayed Local, the first speaker gets the snapshot before their message; existing occupants do not get JoinNotice.

No broader fleet, corporation, or gateway feature is marked complete by this pass.

## Whole-client evidence

### Fleet

The whole eve-logic fleetSvc.py scan found its fleet-object calls in that file. The API inventory is: GetInitState 258, Init 335, GetFleetID 337, LeaveFleet 368; ChangeWingName 407, ChangeSquadName 417, SetTakesFleetWarp 421, SetAcceptsConduitJumpsValue 425, SetAcceptsRegroupValue 429, SetOptions 448, GetJoinRequests 460; MoveMember 517, MassMoveMembers 530, CreateWing 576, DeleteWing 583, CreateSquad 588, DeleteSquad 593, SetAutoJoinSquadID 597, MakeLeader 603, KickMember 610, DisbandFleet 616; GetFleetComposition 899, RejectJoinRequest 919, SendBroadcast 988; GetWings 1164, 1170, 1301, 1306, 1311, 1316; UpdateMemberInfo 1806, FinishMove 1815, SetMotdEx 1960, GetMotd 1966, GetFleetMaxSize 2204, 2269, 2297, LoadFleetSetup 2293, SetFleetMaxSize 2305.

The notification-producing client paths are LeaveFleet at fleetSvc.py:365-371, MakeLeader and KickMember at :601-612, and the fleet broadcast dispatch at :982-989. Their client consumers define these shapes and transitions:

- OnFleetJoin at fleetSvc.py:1203-1215 reads member.charID and role/job data, updates the member map, then scatters OnFleetJoin_Local.
- OnFleetLeave at :1225-1249 clears local fleet state for self, removes other members and scatters OnFleetLeave_Local.
- OnFleetMemberChanged at :1258-1292 consumes 13 server arguments, scatters 12 local arguments, and updates role/job state.
- OnFleetBroadcast at :1541-1556 consumes six arguments: name, scope, characterID, solarSystemID, itemID, typeID.
- OnFleetMove at :1812-1815 performs the fleet session change and FinishMove.

The whole-tree local-event consumers were checked: join/leave in ui/shared/fleet/fleet.py:245,265; fleetbroadcast.py:188,194; userentry.py:293,303; fleetupSearchCont.py:269,274; overviewWindow.py:505,510; targetInBar.py:313,318; bracketMgr.py:842,846; xmppchatuserentry2.py:226,236; chat/client/member_list_entry.py:226,236. Member changes are consumed in fleet.py:290, fleetbroadcast.py:200 and fleetWatchList.py:304. Broadcasts are consumed in fleetbroadcast.py:236, fleetWatchList.py:99 and fleetBroadcastCont.py:203.

EveJS v0.12.9 leaves with notifyTarget:false in server/src/services/fleets/fleetRuntime.js:1845-1864. Its notification builder sends OnFleetMemberChanged through notifyFleetMemberChanges at :829-883, uses the same 13-argument builder for member changes at :811-827, and sends six OnFleetBroadcast arguments at :2523-2553.

### Corporation

Relevant client actions are UpdateMember in eve/client/script/ui/services/corporation/base_corporation.py:368-370 (also bulk UpdateMembers at :372); KickOutMember at :402-413 and KickOutMembers at :468; member invitation/application updates at :575-576 and UI acceptance at eve/client/script/ui/shared/neocom/corporation/corp_ui_applications.py:749. A self-kick leaves the corporation through the same KickOutMember call.

The whole-tree search found three OnCorporationMemberChanged consumers: base_corporation.py:98,374-375 forwards the three arguments; bco_members.py:58-88 applies corporationID old/new pairs to the member list and :228-244 updates records; ui/shared/dockedUI/offices.py:182-186 reloads when the changed member is the current character.

EveJS v0.12.9 constructs [corporationID, characterID, change] in server/src/services/corporation/corporationNotifications.js:859-912 and passes that tuple to the session notification at :648-673. The .NET adapter now emits those tuple items as three top-level RPC arguments (CorporationNotificationAdapter.cs:31-45). Production CorpRegistryService calls the adapter after persisted member updates, application acceptance, member departure, kicks, and role changes (CorpRegistryService.cs:361, 1014, 1067, 1177-1186, 1222, 1252-1270, 1303).

### Local System Chat

The full eve-logic search found the request/notice protocol usage in chat/client/local_chat/messenger.py and generated schema files. It subscribes to MessageBroadcastNotice, LeaveNotice, MembershipListNotice, MembershipRefreshedNotice and JoinNotice at messenger.py:40-44; maps notice payload fields into signals at :47-69; sends BroadcastMessageRequest and empty BroadcastMessageResponse at :72-77; and requests membership with GetMembershipListRequest/GetMembershipListResponse at :78-80. channel_controller.py:158-182 sends optimistically and clears pending messages from the response; :190-203 ignores the sender's echoed message; :204 onward replaces membership; :258-285 applies join/leave; :307-322 requests membership recovery.

The official generated package is eve_public.chat.local.api (requests_pb2.py:14-19; notices_pb2.py:15-30). GetMembershipListResponse has repeated members field 1 and solar_system field 2. BroadcastMessageRequest has message field 1; BroadcastMessageResponse is empty. Notices use: MembershipList members 1/system 2; Join member 1/system 2; Leave character 1/system 2; Message author 1/system 2/message 3. Fully-qualified type names include the package exactly once.

EveJS v0.12.9 targets the join snapshot to the joining character and emits local-join only for non-delayed systems (server/src/_secondary/chat/chatRuntime.js:1495-1522). Leave removes the speaker at :1524-1549; observers are refreshed at :1557-1570; messages emit at :1701-1713. The gateway routes membership lists to the target character and join/leave/message notices to the system at server/src/_secondary/express/gatewayServices/localChatGatewayService.js:423-522.

## eve.rs changes and verification

Production changes are in FleetObjectService.cs:877-964, CorporationNotificationAdapter.cs:31-45 and PublicGateway/LocalChatGateway.cs:116, 201, 229-239. Host regressions are in FleetObjectRpcWireTests.cs, CorporationClientRpcWireTests.cs and LocalChatGatewayTests.cs. The Host tests use two live sessions; the LSC test opens real HTTP/2 notice streams.

Verified locally:
- Combined touched regressions: 11 passed, 0 failed, 0 skipped.
- LocalChatGatewayTests: 8 passed, 0 failed, 0 skipped.
- The whole Eve.Host.Tests shard was also run earlier with EVE_PG_URL removed: 534 passed, 104 failed, 227 skipped (865 total). Two of those failures were stale LocalChat type-name/frame assertions; the full LocalChatGatewayTests class passed after they were corrected. The whole shard has not been repeated after that correction, so its remaining result is unresolved.
- No graphical V24.01 client session was exercised. Fleet and corporation Host tests drive the production notification publisher/adapter through Host wire probes with before/after snapshots; they do not exercise PostgreSQL-backed writes or restart behavior.
- The initial notification pass was pushed as d7f64014 (main tip 7505fb9f). Selected agent-test run 40505887538 passed with no failed tests or jobs. Push-triggered ci.yml run 40505868630 failed. Its in-scope failure was SpaceServiceTests.Fleet_invites_and_membership_publish_client_notifications_and_session_changes expecting a voluntary leaver to receive OnFleetLeave; the assertion now checks that the leaver receives no echo, matching EveJS fleetRuntime.js:1845-1864 and the Host wire test. Local checks after this correction: Eve.Services.Space.Tests project build succeeded with 0 warnings/errors; Eve.Host.Tests Voluntary_fleet_leave focused test passed (1 passed, 0 failed, 0 skipped), with no database URL. Other failures in that CI run concern station result shapes, unrelated space RPC results and structure-office numeric types; those services are outside this pass. The selected agent-test run 40511539257 for follow-up commit bc879e1f passed with no failed tests or jobs. Push-triggered ci.yml run 40511526306 completed with failures. Its failure list no longer includes the fleet leave regression; remaining failures are station result shapes/lookup, non-fleet space RPC result types, corporation office behavior and structure-office numeric type.

## Chat member-query follow-up (2026-10-29)

V24 `xmppchatsvc.py:1580-1592` and `:1599-1609` issue `muc#admin` affiliation and role queries and consume matching item JIDs with `GetUsersFromResponse`; `xmppchannelsettings.py:347-352, 391-392, 510-516, 552-558` uses them for allowed, blocked, operator and owner lists. Its mutations use `SetAffiliation` at `:368, 374, 437, 530, 536, 572, 578`, and channel forms use `GetChannelConfig` / `ConfigureChannel` at `xmppchatsvc.py:1492-1506, 1625-1630` and `xmppchannelsettings.py:249-256, 279-284`.

eve.rs now answers read-only `muc#admin` get queries for accessible rooms when the requester is a player-room owner or chat staff. It filters visible current occupants by their server-derived owner/admin/member affiliation or moderator/participant role; other filters and all set queries remain unsupported and receive the existing protocol error. `XmppLocalRoomTests.Muc_admin_member_queries_return_live_members_and_server_roles` verifies the wire response and refusal for an ordinary member. No Macho `LSC` service callsite was found in the whole V24 client tree; native LSC discovery/join/leave remains outside the client flow.

Host build passed with 0 warnings/errors. The focused member-query test passed (1 passed, 0 failed, 0 skipped). The no-database Eve.Host.Tests shard run completed with 608 passed, 100 failed, 229 skipped; the failures were in unrelated Host fixtures and database-backed tests were not run. No graphical-client walk or database test was performed.
