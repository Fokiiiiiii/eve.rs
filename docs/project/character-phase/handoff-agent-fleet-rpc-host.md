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


## RPC-01609–01619 resolution

The V24.01 call and consumer trace across the client tree is:

| Row | Client evidence and consumed behavior |
|---|---|
| RPC-01609 | `eve/client/script/parklife/fleetSvc.py:421-423` forwards the bool setting to the bound fleet object; the return is ignored. |
| RPC-01610 | `fleetSvc.py:1799-1811` periodically sends `GetMyShipTypeID()`; no reply is consumed. |
| RPC-01611 | `fleetSvc.py:2369-2376` sends `GetData()`, clears the cached finder list, and wraps a returned advert as `FleetAdvertObject`. |
| RPC-01612 | `fleetSvc.py:1898-1910` uses a truthy result to raise `FleetApplicationReceived`; `ui/shared/fleet/fleetFinderEntry.py:68-73` invokes this flow from the finder entry. |
| RPC-01613 | `fleetSvc.py:1890-1892` consumes a fleetID-to-advert mapping, iterates its local dict with `iteritems()`, and constructs `FleetAdvertObject` values. `AskJoinFleetFromLink` checks the map at `fleetSvc.py:1918-1922`. |
| RPC-01614 | `fleetSvc.py:2393-2398` wraps a present advert or returns None; `GetCurrentAdvertForMyFleet` feeds the register window at `fleetSvc.py:1924-1939`. |
| RPC-01615 | `fleetSvc.py:2385-2391` wraps a present advert and clears the memoized finder list; client callers include remove/update and unregister flows. |
| RPC-01616 | `fleetSvc.py:2407-2412` wraps the updated advert; callers are `UpdateAllowedEntitiesInAdvert` and `OnContactChange` (the latter refreshes after contact edits). |
| RPC-01617 | `fleetSvc.py:2400-2405` wraps the updated advert; `UpdateAdvertInfoThread` calls it at `fleetSvc.py:1734-1752` after recomputing member count and access differences. |
| RPC-01618 | `fleetSvc.py:2378-2383` wraps the advert after leader change; callers are `TryUpdateFleetFinderAdvertWithNewBoss` and `OnFleetMemberChanged`. |
| RPC-01619 | This is a local Python dict operation, not an RPC: the receiver is the result of `GetAvailableFleetAds()` on `fleetSvc.py:1891`. Whole-project graph search found no `fleetProxy.iteritems` service caller. EveJS likewise returns the built advert map from `fleetProxyService.js:17-21` and defines no `iteritems` handler. No Host method was added for a client-local operation. |

EveJS v0.12.9 dispatches RPC-01611–01618 in `server/src/services/fleets/fleetProxyService.js:17-69`, returning the advert map, advert payload, bool, or null from the fleet runtime. Its bound-object `SetTakesFleetWarp` and `UpdateMemberInfo` handlers delegate to the fleet runtime in `fleetObjectHandlerService.js:350-357,377-383`. eve.rs already uses the durable `FleetStore` and Host advert callback for these operations; no gameplay behavior changed in this coverage slice.

The new `Fleet_finder_rpcs_are_dispatched_through_host_with_transport_kwargs` regression registers real `FleetObjectService` and `FleetProxyService` instances in `HostServer`, sends MachoNet calls with `machoVersion`, and checks their wire replies. It covers RPC-01609–01618; `GetAvailableFleetAds` returns a PyDict, `ApplyToJoinFleet` returns the client error object for a session without a character, and the empty-state mutations return None. It does not exercise persisted positive-state advert mutations, restart, notification delivery, or the graphical client. No PostgreSQL database was used.
