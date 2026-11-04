# RPC Host coverage handoff: 02489, 02490, 02768-02770, 03003

## Result

A focused wire regression now drives the registered services through a real Host dispatcher, includes transport-added `machoVersion`, checks the stance response and notification, checks the starter eligibility boolean, and checks the reference rejection responses for shipcaster travel and Void Space exit. The previously present `ShipMovementRpcCoverageTests` was removed because its stance and Void Space requests could succeed as unknown-service errors rather than prove the actual routes.

The task's client wire coverage is resolved. Successful shipcaster travel and Void Space exit remain open because their authoritative runtime state is not implemented or registered. This change does not claim a playable travel transition.

## V24.01 whole-tree caller and consumer evidence

- **RPC-02489 `shipStanceMgr.SetShipStance`:** `eve/client/script/ui/inflight/shipstance.py:29-32` invokes with `(ship_id, stance_id)` and discards the ordinary RPC return. Every `OnStanceActive` consumer found by a whole-tree search is `eve/client/script/ui/inflight/ship_stance_button.py:53-56`, `eve/client/script/ui/shared/fitting/fittingController.py:429-432`, `eve/client/script/ui/station/base.py:268-271`, and `eve/client/script/ui/view/hangarView.py:707-710`; ship scene animation also consumes the stance at `eve/client/script/ui/shared/fitting/shipSceneContainer.py:324-326`. The fitting ghost path dispatches the same event at `eve/client/script/ui/shared/fittingScreen/ghostFittingSvc.py:1323-1326`. Host returns the previous stance and sends `OnStanceActive(shipID, stanceID)` for a changed state (`GameRpcServices.cs`); its production write provider uses the item store and transaction in `MissionComposition.cs:87-145`.
- **RPC-02490 `shipcasterTravelMgr.CmdJumpThroughShipcaster`:** the menu path is `menusvc.py:3069-3071`, the movement wrapper is `movementFunctions.py:601-604`, and `shipcasterSvc.py:17-20` passes `(shipcasterID, toSolarsystemID, toLandingPadID)` to `PerformSessionChange('jump', ...)`. It does not inspect an ordinary RPC reply.
- **RPC-02768 and RPC-02769 `starterShipcasterTravelMgr.CanCharacterJump`:** both rows map to the same zero-argument method at `shipcasterSvc.py:31-32`; that wrapper returns the boolean directly. Whole-tree search found no additional direct caller or consumer of `CanJumpStarterShipcaster`.
- **RPC-02770 `starterShipcasterTravelMgr.CmdJumpThroughStarterShipcaster`:** `menusvc.py:3074-3075` and the confirmation path `movementFunctions.py:607-616` lead to `shipcasterSvc.py:34-37`, which passes `(shipcasterID)` to `PerformSessionChange('jump', ...)`. It does not consume an ordinary RPC reply.
- **RPC-03003 `voidSpaceMgr.VoidSpaceEndGateActivation`:** `movementFunctions.py:587-589` passes `(itemID)` to `PerformSessionChange('voidToKnownJump', ...)`; the caller relies on session transition behavior, not a returned data object.

The client search also found shipcaster landing-pad and launcher data callers at `shipcasterSvc.py:22-29`; those response rows are outside this task.

## EveJS v0.12.9 comparison and current authority

- `shipStanceMgrService.js:35-49` returns `oldStanceID` after `shipStanceRuntime.js:424-572` updates stance state.
- `shipcasterTravelMgrService.js:23-33` rejects a shipcaster travel request when there is no active destination. `starterShipcasterTravelMgrService.js:31-37` likewise rejects starter travel when no destination exists. `shipcasterRuntime.js:210-213` returns true only for a character in the explicit starter-eligible set; an unseeded character returns false.
- `voidSpaceMgrService.js:82-88` rejects end-gate activation when there is no active Void Space origin state.

Host registers shipcaster routes from `dotnet/src/Eve.Host/Rpc/AgentRpc.cs:110`, and the Void Space route from `dotnet/src/Eve.Host/Rpc/ProgressRpc.cs:106`. Ship stance is registered in `dotnet/src/Eve.Services.Core/CoreServices.cs:80`. The shipcaster Host registrations have no beacon, faction, eligibility or travel provider; the Void Space registration has no runtime callback. Accordingly the regression checks EveJS's no-destination / no-origin refusal paths and does not fabricate a jump.

EVE University describes active Interstellar Shipcaster beacons as destination state (zero to three active, cycling destinations when multiple are active), and limits use to pilots enlisted with the relevant militia: https://wiki.eveuniversity.org/Shipcaster . eve.rs does not yet own that active beacon state or a successful shipcaster transition. No mechanics deviation from EveJS is introduced by this coverage change.

## Verification

- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- Focused Host wire test `Movement_rpcs_use_registered_host_routes_with_client_wire_shapes`: passed (1 passed, 0 failed, 0 skipped).
- The stance wire assertion supplies an in-memory callback state through the production service contract; it does not exercise the database write provider. That provider remains covered by the existing domain tests, while this change avoids PostgreSQL as requested.

- Dispatched `agent-test.yml` for `f335b402` with `Eve.Host.Tests` and the focused method filter: run `40243527358` succeeded, 0 failed tests/jobs and 0 skipped cases.
- Push-triggered `ci.yml` run `40243521339` for `f335b402` failed in the unrelated insurance wire test: the persisted price lookup returned `PyInt(0)` instead of `PyReal(1000000)`. Main addressed the identifier mismatch in `fd73decd` by quoting the mixed-case persisted table name in `InsuranceRpcHostWireTests.cs`. The later `ci.yml` run `40245093534` for `ac7158ca` was still queued at the last check; no pass is claimed here.
- The test is a Host socket/headless check, not a graphical-client run.
