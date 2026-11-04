# RPC Host coverage handoff: cx5

## Current batch status

This batch was analyzed by the supplied service and client callsites. The numeric IDs do not match the current `docs/project/service-audit/EVE_RS_COVERAGE.csv`: the supplied 02292–02301 range maps to unrelated PVP methods in this checkout, and 02800, 02817–02818, 02829–02843 also map to different methods. Do not rewrite catalogue IDs based on the task's stale numbers. Also, two supplied entries are false positives: `planetMgr.GetPin` is a local planet object's method and `planetMgr.append` is a local list mutation.

Added real Host socket regressions:
- `StructureRpcHostCoverageTests.cs`, test `Structure_control_delivery_deployment_and_beacon_calls_use_registered_host_routes`: dispatches structure control, dropoff, delivery, anchor, and beacon calls through the real Host socket, registry, dispatcher, and packet codec. Calls include transport-added `machoVersion`.
- `PlanetManagerRpcHostTests.cs`, test `Planet_launch_tax_and_colony_reads_use_registered_host_routes`: exercises planet listing, launch listing/deletion, and tax read over the real Host socket with `machoVersion`.

Both focused tests passed locally (1 passed, 0 failed, 0 skipped each); the Host.Tests project built with 0 warnings and 0 errors. On Actions, structure test run 40232250318 and planet test run 40232249171 both succeeded with no failed tests or jobs. Push-triggered CI run 40232238340 was still in progress at last check. Structure behavior is backed by production Host composition and persistent providers in `dotnet/src/Eve.Host/Composition/StructureComposition.cs` and Host routes in `dotnet/src/Eve.Host/Rpc/StructureRpc.cs`. Planet routes are composed in `dotnet/src/Eve.Host/Composition/PlanetComposition.cs`; PI data is owned by PlanetStore. The tests assert wire shapes and route behavior. They do not replace the existing backend/live checks of state transitions.

## Client callers and consumers

- `planetMgr.DeleteLaunch`: `eve/client/script/ui/shared/neocom/journal.py:455-464` removes the local launch entry, calls DeleteLaunch, then refreshes launches. `planetUISvc.py:168-176` caches GetMyLaunchesDetails and invalidates on OnPILaunchesChange. EveJS `planetMgrService.js:1462-1474` implements launch listing/deletion. Host service is `PlanetMgrService`; test covers row result and no-character deletion edge.
- `planetMgr.GetMyLaunchesDetails` and `GetPlanetsForChar`: `planetUISvc.py:168-176` consumes launch rows; `environment/planetSvc.py:65-73` caches planet rows and builds planet objects. EveJS `planetMgrService.js:1063-1073,1462-1468`. Current Host returns typed CRowsets from PlanetStore.
- Supplied `planetMgr.GetPin` references at `BasePinContainer.py:609-610` and `planetUIPins.py:473-474` call local `GetPlanet().GetPin(pinID)`; whole-tree search found no RemoteSvc call. Supplied `planetMgr.append` at `environment/planetSvc.py:150-164` mutates a local list; whole-tree search found no RemoteSvc call. No route or fake success was added.
- `planetOrbitalRegistryBroker.GetTaxRate`: `ui/shared/info/infosvc.py:2065-2078` renders the percentage/access-denied state; `ui/shared/planet/importExportUI.py:382-387` updates import/export tax. EveJS `planetOrbitalRegistryBrokerService.js:58-64`; Host delegates to PI ownership state. Test covers the no-character None response.
- `structure.JettisonStructureFuel`: `ui/services/menuSvcExtras/invItemFunctions.py:75-80` sends selected item IDs through the structure bound object. EveJS `structureService.js:343-379,381-547` validates ownership, control, role, fuel-bay membership and fuel type, then creates a corporation jetcan, moves items, spawns the entity, and compensates if spawning fails. eve.rs has no corresponding method/provider. This remains the sole true missing implementation in this batch; do not call it complete.
- `structureCargoDelivery.DropOffItems`: `ui/structure/dropbox/dropboxController.py:116-121` sends item IDs, ship ID, and structure ID, then uses returned moved IDs to update the UI and notify success. EveJS `structureCargoDeliveryService.js:363-423`. Host provider is wired in StructureComposition; socket test checks return IDs.
- Structure control: `ui/services/structure/structureControl.py:28-104` calls TakeControl, BoardStructure, ReleaseControl, EjectFromStructure and GetStructurePilot; `structureController.py:72-86`, `lobbyWnd.py:356-357`, and `controlButton.py:54-59,88-107` consume pilot state and trigger controls/session changes. EveJS `structureControlService.js:81-198`. Host registers and wires the persistent control backend; socket test checks reply/dispatch.
- `structureCynoBeaconMgr.CmdJumpToStructureBeacon`: `parklife/cynoTravelSvc.py:53-55` initiates the jump with structure and solar-system IDs. EveJS `structureCynoBeaconMgrService.js:676-678`; Host wires the actual travel transition. Socket test includes both args.
- `structureDeliveries.Deliver`: `ui/services/menuSvcExtras/invItemFunctions.py:135-153` sends structure ID, character ID and item IDs; return is ignored. EveJS `structureDeliveriesService.js:166-231`; Host uses the delivery provider and test covers dispatch.
- `structureDeployment.Anchor`: `ui/services/structure/structureDeployment.py:172-173` sends ten positional fields, then cleans up/reloads the directory. EveJS `structureDeploymentService.js:20-44`; Host wires StructureAnchorAuthority. Socket test checks positional arguments and result.

## Other pre-existing WIP

The worktree also contains earlier interrupted ship/dogma/beyonce Host test files noted in the previous handoff. They were included in commit 8eab5d09 and the Host.Tests project build passed. Their earlier Actions runs stopped before build/test because setup-dotnet downloads returned HTTP 409; the current focused jobs above do not cover those tests.

## Not done / verification limits

- Implement and persist `JettisonStructureFuel` end to end, including safe corporation jetcan creation, item custody, spawn compensation, notifications and focused regression.
- Await and inspect push-triggered CI run 40232238340.
- No database or graphical client was used in this batch. Existing headless/backend checks are not a substitute for graphical-client verification.
