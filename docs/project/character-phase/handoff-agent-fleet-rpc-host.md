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

server/src/services/fleets/fleetObjectHandlerService.js:173-411 dispatches these same getters, mutations and broadcasts to fleetRuntime.js and preserves the corresponding runtime result. In particular, fleetRuntime.js:1933-1979 returns true from MoveMember and the successfully moved character ID list from MassMoveMembers; those are consumed in the client flow above. In eve.rs, the handler is backed by FleetStore and Host callbacks, and mutation notifications are emitted from the persisted before/after snapshots. A positive-state Host regression for these replies and notifications remains outstanding.

## Checks

- MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false — passed, 0 warnings/errors.
- MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter 'FullyQualifiedName~Fleet_object_rpcs_are_dispatched_through_host_with_transport_kwargs' -p:UseSharedCompilation=false — passed, 1 test, 0 skipped.
- Agent Actions run 40176463800 — passed, 0 failed tests/jobs.
- Push-triggered ci.yml run 40176456650 — queued when this note was updated.
- The graphical V24.01 client was not exercised.


## RPC-01609–01619 resolution and whole-tree audit

The caller and consumer scan covers all of `/home/user/eve-logic/eve/client/script`, using CBM graph searches plus Serena literal searches. The relevant trace is:

| Row | Client calls and consumers |
|---|---|
| RPC-01609 | `fleetSvc.py:421` sends the setting to `self.fleet`; `fleetSvc.py:422-423` ignores the reply. |
| RPC-01610 | `fleetSvc.py:1799-1811` schedules `UpdateMemberInfo(GetMyShipTypeID())`; the return is ignored. |
| RPC-01611 | `fleetSvc.py:1858-1868` registers an advert with `GetData()`; `fleetSvc.py:2368-2376` converts the advert object to a dict and wraps a returned dict as `FleetAdvertObject`, then clears the available-ad cache. |
| RPC-01612 | `fleetSvc.py:1897-1910` calls the proxy and raises `FleetApplicationReceived` only for a truthy result. Callers in the full client tree: `fleetSvc.py:1918-1922` (fleet link), `ui/shared/fleet/fleetFinderEntry.py:67-73`, `ui/shared/fleet/fleetfinder.py:365-373`, and `ui/shared/agencyNew/ui/contentPagesUI/fleetupUI/joinFleetConfirmation.py:18-24`; the confirmation passes `autoAccept=True`. |
| RPC-01613 | `fleetSvc.py:1889-1895` expects a mapping and turns each advert dict into `FleetAdvertObject`. Whole-tree consumers: `ui/shared/fleet/fleetfinder.py:480` and `ui/shared/agencyNew/ui/contentPagesUI/fleetupUI/fleetupSearchCont.py:123`; link joining verifies the fleet ID at `fleetSvc.py:1916-1922`. The finder UI's advert data uses the wrapped fields in its fleet entry widgets. |
| RPC-01614 | `fleetSvc.py:1923-1940` handles None as no current advert and otherwise adds local standing and route data. Whole-tree UI callers: `ui/shared/fleet/fleetFinderEntry.py:54`, `ui/shared/fleet/fleetfinder.py:156`, `ui/shared/agencyNew/ui/contentPagesUI/fleetupUI/fleetRegisterController.py:16-24`, and `ui/shared/agencyNew/ui/contentPagesUI/fleetupFleetEntries.py:183`. |
| RPC-01615 | `fleetSvc.py:938-939`, `1853-1854`, and `1885-1886` remove adverts in leader-change, unregister, and setup flows. `fleetSvc.py:2384-2391` wraps a present advert or returns None and clears the available-ad cache. |
| RPC-01616 | `fleetSvc.py:1368-1369` refreshes allowed entities after contact changes; `fleetSvc.py:1780-1785` calls the update and stores the returned advert as current state. `fleetSvc.py:2406-2412` wraps the optional advert payload. |
| RPC-01617 | `fleetSvc.py:1733-1752` updates the advert after recalculating member count and access-list differences, stores a present reply, and ignores None. Wrapper: `fleetSvc.py:2399-2405`. |
| RPC-01618 | `fleetSvc.py:944-954` refreshes the advert after the boss changes; the member-change handler at `fleetSvc.py:1258-1283` triggers it. The wrapper at `fleetSvc.py:2377-2383` returns a `FleetAdvertObject` for a payload or None. |
| RPC-01619 | `fleetSvc.py:1890-1892` invokes Python's `iteritems()` on the local mapping returned by RPC-01613. Whole-tree search found no proxy service caller for `iteritems`; it is not a remote method and requires no Host registration. |

EveJS v0.12.9 dispatches the two bound-object methods through `fleetObjectHandlerService.js:350-357,377-383`. `fleetProxyService.js:17-69` dispatches RPC-01611–01618 into `fleetRuntime.js`: available-ad filtering is at `2608-2620`; add/remove/get/update-advert operations are at `2622-2728`; join requests and invites are at `2730-2797`. These return a fleet-ID mapping, advert dict or null, or the join-request boolean; the runtime emits `OnFleetOptionsChanged` on advert-registration changes and `OnFleetJoinRequest` for applications requiring approval. eve.rs composes both production services in `FleetComposition.cs:196-212`, passes advert operations to `FleetStore.FleetFinderAdvertOperationAsync`, and sends registration-change notifications from `FleetProxyService.cs` after the persisted mutation. No intentional client-visible difference is recorded.

Local verification on this handoff update: Host test project build passed (0 warnings/errors); focused `Fleet_finder_rpcs_are_dispatched_through_host_with_transport_kwargs` passed (1 passed, 0 skipped). The test exercises the registered service objects through HostServer's MachoNet socket path with the transport `machoVersion` kwarg and covers the empty-state return shapes for RPC-01609–01618. It does not establish positive persisted-advert state, restart behavior, or notification delivery. No PostgreSQL database or graphical client was used. Agent-test workflow run 40180130687 for commit `e2338bf8` passed with no failed tests or jobs. `ci-status.py failures e2338bf8` returned `No ci.yml run found`; that push-triggered workflow has no result to record.
