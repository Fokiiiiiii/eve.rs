# Drone state notices

## Sources checked

- V24.01 client snapshot `/home/user/eve-logic`, commit `feaf63ae1`.
- EveJS reference `/home/user/evejs-261108.1`, commit `1dbb571`.
- eve.rs source and the `Eve.Services.Space.Tests` / `eve-space` drone tests in this worktree.

## Wire event versus client events

The client does not expect the server to send `OnDroneStateChange2` or `OnDroneControlLost` by those names. Its Michelle service receives `OnDroneStateChange(itemID, ownerID, controllerID, activityState, typeID, controllerOwnerID, targetID)`. If neither the character nor current ship matches the supplied owner/controller, Michelle clears its cached drone state and scatters local `OnDroneControlLost`; otherwise it updates the cached row and scatters local `OnDroneStateChange2(itemID, oldActivityState, activityState)` (`eve/client/script/remote/michelle.py:1496-1518`).

Those local events have visible consumers: `clientDogmaLocation.py:1392-1411` unloads dogma state on control loss and refreshes drone dogma on a state change; `tactical.py:892-903` refreshes the tactical drone list; `bracketsAndTargets/targetInBar.py:738-755` updates drone weapons on targets; `ui/inflight/drone.py:272-281,456-458` and `ui/inflight/drones/droneEntry.py:205-218` refresh the drone window and its state/activity text. `godma.py:577-585,2277-2291` forwards the events and updates its inventory cache.

The EveJS server source contains no sender for either derived event name. It sends `OnDroneStateChange` with the seven fields above, falling back to a `charid` notification when it cannot queue the event in the Destiny history (`server/src/services/drone/droneRuntime.js:1832-1893`). The same delivery path sends `OnDroneActivityChange(droneID, activityID, activity)` (`:1948-1976`). The client itself scatters the two events from Michelle, so sending either derived name directly would bypass the state update that creates them.

## Transition audit

| Transition | EveJS and V24.01 behavior | eve.rs before this change |
| --- | --- | --- |
| Launch | EveJS sends `OnDroneStateChange` before the launched drone's ball is added (`droneRuntime.js:5154-5175`). The client then creates its drone row and refreshes dogma. | Rust places `OnDroneStateChange` before `AddBalls2` (`crates/eve-space/src/drones/launch.rs:128-138,171-210`); the test asserts that order (`crates/eve-space/src/drones/tests.rs:238-243`). |
| Engage / mine / return | EveJS emits the old wire event when authoritative activity or target state changes (`droneRuntime.js:3076-3080,4199-4272,5270-5297`). A rejected engage against a target beyond control range returns before changing drone state (`:5384-5387`). | Drone commands and work transitions call `told_state`, which emits `OnDroneStateChange` and clears/updates activity (`crates/eve-space/src/drones/launch.rs:213-233,270-293,405-460`; `drones/work.rs:86-110,309-312`). |
| Scoop | EveJS batches a state row with null owner/controller/activity fields before the ball removal (`droneRuntime.js:4801-4818`). | `take_in` sends the same all-null state action before `RemoveBalls` (`crates/eve-space/src/drones/work.rs:326-365`); the Rust test checks the notice and removal ordering (`drones/tests.rs:875-915`). |
| Drone death | EveJS tells the controller the drone is idle and uncontrolled, then clears activity (`droneRuntime.js:2229-2250`). | Rust sends the matching seven-field `OnDroneStateChange` and the three-field activity clear (`crates/eve-space/src/death.rs:110-133`); the test checks the exact payload (`drones/tests.rs:762-800`). |
| Control released / abandoned | EveJS clears controller state and sends `OnDroneStateChange` with owner/controller zero, idle state, type ID and no target, then clears activity (`droneRuntime.js:6074-6114`). Controller-loss cleanup uses this path (`:6148-6173`); leaving the drone's grid bubble also releases it through this path (`:8107-8118`). | Rust changed the drone link and emitted `Out::DroneReleased` (`crates/eve-space/src/drones/threats.rs:223-256`). The Host moved the record to `_abandoned` but sent no client notice (`dotnet/src/Eve.Services.Space/SpaceDrones.cs`, previous `OnReceived` branch). This was the missing client-visible path. |
| Control range / active-drone limit | An engage order outside control range is refused without losing control (`droneRuntime.js:5384-5387`); already-tasked drones can continue past the controller range (`:4185-4190`). The active-drone cap is checked when launching and reconnecting (`:4953-4995,6198-6232`); the reference has no automatic release notice for a lower cap. | Rust likewise refuses an out-of-range engage before assigning the task (`crates/eve-space/src/drones/launch.rs:417-446`). The Host checks the active limit during launch/reconnect (`SpaceDrones.cs:194-228,468-477`). No automatic limit-drop notice was found. |

## Change made

`SpaceDrones.OnReceived` now sends the control-release state when `SimDroneReleased` moves a launched record to `_abandoned` (`dotnet/src/Eve.Services.Space/SpaceDrones.cs:667-670`). It sends `OnDroneStateChange` to `charid` first with `(droneID, 0, 0, idle, typeID, 0, None)`, then `OnDroneActivityChange` with `(droneID, None, None)` (`:750-756`). Michelle derives `OnDroneControlLost` from the first event; the activity clear removes the old assist/guard label. This matches EveJS's state-change shape and order without sending the client-local event names as wire events.

The regression test goes through the registered `CmdAbandonDrone` entity method, injects the simulation's `SimDroneReleased`, checks both exact notices and their arguments/order, then reconnects through `CmdReconnectToDrones` (`dotnet/tests/Eve.Services.Space.Tests/DroneHostTests.cs:180-219`). Before the Host change, it failed because the notice list was empty.

## Validation and limits

- Release builds of `Eve.Services.Space` and `Eve.Services.Space.Tests`: passed with 0 warnings and 0 errors.
- Database-backed focused regression test on `eve_p4`: passed, 1 passed / 0 skipped.
- Related `DroneHostTests` and `MutatedDroneHostTests` classes on `eve_p4`: passed, 29 passed / 0 skipped.
- The pinned SDE build `3396210` was loaded into `eve_p4` (61 tables, 668,618 records).
- No real graphical-client session was run. EveJS was source-traced but its server flow was not executed in this audit.
