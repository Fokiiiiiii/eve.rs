# Fleet object RPC Host coverage handoff

## Current status

Commit feae8354 was pushed to main. The production fleetObjectHandler is registered from CharacterServicesComposition.FleetRpc() in dotnet/src/Eve.Host/Composition/FleetComposition.cs; its handler is FleetObjectService (dotnet/src/Eve.Services.Space/FleetObjectService.cs). The Host test registers this production service in the actual HostServer registry and sends MachoNet calls over a socket with the transport-added machoVersion kwarg (dotnet/tests/Eve.Host.Tests/FleetObjectRpcWireTests.cs). It covers all 25 listed row occurrences, including all six GetWings callsites. Local build passed and the focused no-database wire test passed (1 passed, 0 skipped).

The test deliberately exercises the no-active-fleet state. It confirms real dispatch and its empty-state replies; it does not exercise persisted fleet mutation/read success, notification fanout, or restart. Agent-test run 40176463800 passed with no failed tests/jobs. Push-triggered CI run 40176456650 is queued. No PostgreSQL database was used.

## Whole-tree V24.01 client flow

The calls all originate in eve/client/script/parklife/fleetSvc.py; CBM whole-project search of home-user-eve-logic found the service methods, plus menu callers for Kick/Leave/MakeLeader and the JoinRequestField caller. The client flow and consumers are:

| Rows | Client caller and consumer |
|---|---|
| RPC-01584, RPC-01606 | fleetSvc.py:2294-2307: the setup dialog reads current max size for QtyPopup; SetFleetMaxSize submits the selected quantity. |
| RPC-01585 | fleetSvc.py:259-274: InitFleet consumes fleetID, members, wings, options, and motd, primes owner names, resets member locations, and scatters fleet initialization events. |
| RPC-01586 | fleetSvc.py:461-463: caches the returned requests for the join-request UI; ui/shared/fleet/entries.py:75-78 calls RejectJoinRequest. |
| RPC-01587, RPC-01607 | fleetSvc.py:1961-1979: SetRemoteMotd writes; GetMotd fills the local cache; OnFleetMotdChanged updates the cache and fleet chat window. |
| RPC-01588–01593 | fleetSvc.py:379-399, 1165-1172, 1302-1318: six server calls refresh self.wings; the hierarchy/UI reads wing and squad names, IDs, and squad maps. |
| RPC-01594 | fleetSvc.py:336-340, 259-274: CreateFleet calls Init with ship type, setup name and optional advert data, then consumes GetInitState. |
| RPC-01595, RPC-01598, RPC-01601 | fleetSvc.py:604-612, 919-921 and ui/services/menusvc.py:2697-2704: kick, leader change and request rejection are action calls; the direct replies are ignored. |
| RPC-01596 | fleetSvc.py:365-371, also menu caller ui/services/menusvc.py:2692-2694: leaves and clears local fleet state. |
| RPC-01597 | fleetSvc.py:2290-2296: submits the chosen setup; result is ignored. |
| RPC-01599–01600 | fleetSvc.py:513-545: MoveMember's truthy reply triggers OnFleetMemberChanging; MassMoveMembers' returned moved IDs trigger OnManyFleetMembersChanging. |
| RPC-01602 | fleetSvc.py:989-1162: validates/selects broadcast IDs and sends name, scope, item and type; notifications feed the fleet broadcast UI. |
| RPC-01603–01605, RPC-01608 | fleetSvc.py:426-449, 597-599: member preferences, auto-join squad and free-move options are written; the reply is ignored and the UI consumes subsequent fleet/member change notifications. |

## EveJS v0.12.9 comparison

server/src/services/fleets/fleetObjectHandlerService.js:173-411 dispatches these same getters, mutations and broadcasts to fleetRuntime.js and preserves the corresponding runtime result. In particular, fleetRuntime.js:1933-1979 returns true from MoveMember and the successfully moved character ID list from MassMoveMembers; those are consumed in the client flow above. In eve.rs, the handler is backed by FleetStore and Host callbacks, and mutation notifications are emitted from the persisted before/after snapshots. A positive-state Host regression for these replies and notifications remains outstanding. Serena replace_content and replace_in_files rejected edits with “No language servers available in the manager”; no fallback editor was used.

## Checks

- MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false — passed, 0 warnings/errors.
- MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter 'FullyQualifiedName~Fleet_object_rpcs_are_dispatched_through_host_with_transport_kwargs' -p:UseSharedCompilation=false — passed, 1 test, 0 skipped.
- Agent Actions run 40176463800 — passed, 0 failed tests/jobs.
- Push-triggered ci.yml run 40176456650 — queued when this note was updated.
- The graphical V24.01 client was not exercised.
