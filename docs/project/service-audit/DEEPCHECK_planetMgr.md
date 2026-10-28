# Deep check: planetMgr

## Whole-client methods and consumers

| Method | Request and V24 result use |
|---|---|
| GetPlanetsForChar() | eve/client/script/environment/planetSvc.py:66-72; no args. The returned CRowset is iterated and each planet.planetID drives local planet objects. Server rows include solarSystemID, planetID, planetTypeID, pin count and celestial index. |
| GetMyLaunchesDetails() | eve/client/script/ui/shared/planet/planetUISvc.py:169-173; no args. The CRowset is cached for the planetary UI and launch journal; rows carry launchID, solarSystemID, itemID, ownerID, planetID, status, launchTime and coordinates. |
| DeleteLaunch(launchID) | eve/client/script/ui/shared/neocom/journal.py:455-464 passes launchID positionally through uthread.new. Reply is ignored; the client removes the row locally and forces a launch-list refresh. |

GetPin calls in the same coverage report are CLIENT-LOCAL: the whole client calls BasePlanet.GetPin, not a planetMgr bound object.

## Host persistence and result shapes

PlanetComposition.cs:22 constructs PlanetMgrService with PlanetStore and pinned static-data/resource owners. GetPlanetsForChar and GetMyLaunchesDetails return CRowset objects with the V24 row descriptors. Launch list reads are scoped to the caller character and expire old launches. DeleteLaunch uses the caller character plus launchID and returns a bool; it cannot delete another owner’s launch.

EveJS v0.12.9 handlers are PlanetMgrService.Handle_GetPlanetsForChar at server/src/services/planet/planetMgrService.js:1063-1073, Handle_GetMyLaunchesDetails at :1462-1468, and Handle_DeleteLaunch at :1470-1474. Its runtime store is process-local. The Host handlers use the persistent PlanetStore and return the same row sequence and bool/None distinctions.

## Test evidence and limits

PlanetManagerRpcHostTests.cs:15-52 sends empty colony/launch reads and a missing launch delete through a registered Host socket; it is database-free and verifies the CRowset shapes plus false delete. The success read/delete/re-read and owner isolation test at :57-119 is a PgFact that requires PostgreSQL; this job excludes that database-backed path. The CSV’s IMPLEMENTED-TESTED label names that persistence wire test, but it was not exercised in this pass.

No PostgreSQL database or graphical V24 client was used. No code changed.
