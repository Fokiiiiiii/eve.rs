# Deep check: fleet finder advert registration

## Scope

This pass follows advert registration and editing from the V24.01 client through the fleet proxy, persisted fleet state, and member notifications. It does not claim completion of the overview settings, bookmarks/folders, or local chat portions of the broader audit.

## V24.01 client path

- The whole-client graph lists exactly two callers of `FleetSvc.AddFleetFinderAdvert`: `FleetSvc.RegisterFleet` and `FleetSvc.CreateAndRegisterFleet`. Registration checks fleet membership and boss status, calls the advert RPC, then finalizes the UI flow (`eve/client/script/parklife/fleetSvc.py:1861-1875`).
- The service call clears the caller's cached fleet advertisements after Add and Remove. `GetMyFleetFinderAdvert` is read from four client call paths: current advert, member change, register-window open, and boss-change update (`fleetSvc.py:2369-2398`; CBM inbound trace).
- Every fleet member consumes `OnFleetOptionsChanged`: `FleetSvc.OnFleetOptionsChanged` replaces its options, invalidates the fleet-ad cache when registration changes, and scatters `OnFleetOptionsChanged_Local` (`fleetSvc.py:1321-1333`). The Fleet Finder advert panel consumes `OnFleetFinderAdvertChanged` and reloads when visible (`eve/client/script/ui/shared/fleet/fleetfinder.py:179-182`).
- The UI obtains the current advert through `GetCurrentAdvertForMyFleet`; fleet search refreshes from the fleet advert collection and join action proceeds through the fleet finder UI (`fleetSvc.py:1890-1896`; `fleetfinder.py:353-375`).

## Behavior comparison and correction

- EveJS 261028.1 `addFleetFinderAdvert` replaces the advert, sets `isRegistered`, and notifies fleet members with `OnFleetOptionsChanged` on every Add, even when the fleet was already registered (`server/src/services/fleets/fleetRuntime.js:2778-2791`). The proxy returns the advert payload (`fleetProxyService.js:31-34`).
- Host registers the callback and member-session resolver in `dotnet/src/Eve.Host/Rpc/FleetRpc.cs:65-70`. The persisted advert mutation commits before `FleetProxyService.MutateFleetAdvertAsync` emits member notifications (`dotnet/src/Eve.Services.Space/FleetStore.Adverts.cs:41-46, 177-190`; `FleetProxyService.cs:117-131`).
- Defect: eve.rs previously sent `OnFleetOptionsChanged` only when `isRegistered` toggled. Re-registering to edit an active advert therefore omitted the event the reference server sends. The proxy now sends it for every successful Add, and retains the toggle check for other operations.
- The PostgreSQL regression drives the real `FleetProxyService` call path, decodes notifications from each member session, and checks both initial registration and editing an existing advert (`dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:4464-4590`). Against the old condition, the second Add failed at the member notification assertion; with the fix, it passes.

## Checks and remaining scope

After merging current `origin/main`, the changed Space project built with 0 warnings and errors. The focused PostgreSQL regression on `eve_p6` passed (1 passed, 0 skipped), and the Host wire dispatch test passed (1 passed, 0 skipped). Before this feature commit, the newest completed main CI run was green (run 1420, commit `97c11ad86ea2`). No graphical-client session was used.

Overview settings, bookmark creation and movement, personal/shared folder transitions, fleet advert search visibility and join errors, and local chat channel state still need their own full client-to-Host audit and regression coverage.
