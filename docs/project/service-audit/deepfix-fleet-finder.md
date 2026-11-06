# Fleet finder and fleet service audit handoff

Date: 2026-11-02

## Completed fix: authoritative advert member count

The Fleet Finder count is client-visible and affects the advert's join-limit hint. The V24.01 client schedules `UpdateAdvertInfo` with `len(self.members)` in `/home/user/eve-logic/eve/client/script/parklife/fleetSvc.py:1730-1747`, and sends the RPC through its wrapper at `2369-2403`. Finder list results are converted at `1890-1892`; the window displays the count at `/home/user/eve-logic/eve/client/script/ui/shared/fleet/fleetfinder.py:431-449,480-482`, and its entry hint compares `numMembers` with `advertJoinLimit` at `/home/user/eve-logic/eve/client/script/ui/shared/fleet/fleetFinderEntry.py:68-87`. The client update is delayed by 60 seconds (constant at `fleetSvc.py:55-57`), so it cannot be the authoritative source after a membership transition.

The Host previously accepted the supplied member count in advert updates and returned the serialized count when reading adverts. This let a stale or forged count mislead Finder users and left the boss's advert stale after membership changes. `FleetStore.Adverts.cs` now derives the count from `fleet_members` on writes and reads, including finder listings. The focused PostgreSQL test covers a forged input count and leave/join transitions through the finder and owner's advert reads.

EveJS 261028.1's `fleetRuntime.js:2832-2861` also trusts the supplied count. eve.rs intentionally diverges: the client normally supplies its local member-list size, but authoritative persisted membership gives the correct result during the client's delayed update and is consistent with the client UI's count and limit semantics. The EVE University Fleet interface page describes the advertised member limit and its admission effect: https://wiki.eveuniversity.org/Fleet_interface.

Verification: `dotnet build dotnet/src/Eve.Services.Space -p:UseSharedCompilation=false` succeeded. On `eve_p7`, the focused test passed (1 passed, 0 failed, 0 skipped), and `SpaceServiceTests` passed (154 passed, 0 failed, 0 skipped).

## Broadcast path checked; server rate limit remains to address

The client sends global broadcasts through the bound fleet object and bubble/system broadcasts through `fleetMgr`; the entry points and client throttle are `/home/user/eve-logic/eve/client/script/parklife/fleetSvc.py:961-1008`. Its throttle is 2 seconds for repeating a broadcast and one third of that for a different broadcast (`fleetSvc.py:57,972-980`). EveJS applies the same per-character policy server-side in `/home/user/evejs-261028.1/src/server/src/services/fleets/fleetRuntime.js:2631-2654,2679-2709`. eve.rs routes are in `dotnet/src/Eve.Services.Space/BeyonceService.cs:2114-2155,2400-2452`; no equivalent Host rate check was found. A caller that bypasses the client can therefore send broadcasts faster than the client UI permits. This audit did not add a limiter or regression test for this path.

## Fleet boost notification path checked; host path not completed

V24.01 registers `OnFleetBoosted` and displays the successful-boost dialog with `moduleTypeID` and `numBoosted` in `/home/user/eve-logic/eve/client/script/parklife/dbuffClient.py:10-20`. EveJS emits this notification from `/home/user/evejs-261028.1/src/server/src/space/runtime/commandBurst.js:348-367`. A matching `OnFleetBoosted` implementation was not found in eve.rs during this pass. The command-burst simulation and its recipient/ordering rules still need a complete trace and focused wire test.

## Remaining audit work

The fleet advert/list/apply path, broadcast paths and client fleet-boost notification consumer were traced. This pass did not finish an end-to-end audit of every fleet advertisement eligibility/error transition, broadcast notification ordering, or command-burst boost mechanics. Do not treat those areas as complete based on this note.
