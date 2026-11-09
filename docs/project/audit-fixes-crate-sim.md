# Crate simulation audit fixes (r15)

Reviewed 2026-11-09 against the audit report `/home/user/reports/90-audit-crate-sim-r15.md`, the V24.01 client source in `/home/user/eve-logic`, EveJS 261028.1 in `/home/user/evejs-261028.1`, the current Host path, and ADR 0007.

## Confirmed and fixed: station docking approach did not finish on arrival (P1)

V24.01's ordinary station action calls `bp.CmdDock(itemID, session.shipid)` once from `RealDock` (`eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:503-518`). A repository-wide search of the client found no retry path. `OnDockingAccepted` is consumed by the movement display and docking notification UI (`eve/client/script/parklife/spaceMgr.py:511-518`, `eve/client/script/ui/view/dockingHeroNotificationService.py:23-35`); neither issues another dock command.

EveJS 261028.1 starts a station approach with `GotoPoint` and retains its target (`server/src/space/runtime/scene/pilotCommands.js:720-763`). After the scene tick, it checks whether the ship arrived and calls the ordinary docking acceptance checks (`server/src/space/runtime/scene/pilotCommands.js:765-808`, `server/src/space/runtime/spaceRuntime/docking.js:146-217`, `server/src/space/runtime/scene/sceneFeatureHooks.js:273-280`). Its source records nine out-of-range station approaches, seven of which completed without a second client `CmdDock` (`pilotCommands.js:722-727`). This is why the older EveJS v0.12.9 request-again behavior is not retained for the V24 client flow.

The existing eve.rs trajectory model is retained here: it issues `FollowBall` at the docking range (`crates/eve-space/src/scene/docking.rs`) and the shared motion model settles a follower just inside that range (`crates/eve-space/src/motion.rs:127-138`). That differs from EveJS's station-centre `GotoPoint`. The new sim-link test verifies that this model reaches its docking threshold and completes the state transition; it does not establish equal flight paths or timing in the graphical client.

Before the fix, `Scene::poll` cleared `docking_approach` when the ship entered range. It did not call `accept_docking`, and the existing scene test required a second `CmdDock`. The new code rechecks the simulation-owned dock restrictions at arrival, clears the pending approach, and starts the regular docking delay when allowed. A new movement order still cancels the approach. An arrival that has become blocked is dropped without emitting an unrelated command refusal. After the regular four-second delay, the Host still makes the authoritative timer, module, and structure-access checks before allowing `Docked` (ADR 0007; `SpaceSessions.Movement.cs:560-603`).

The initial `Approaching` reply has already completed the outstanding `CmdDock`: `SimClient` removes and resolves that request in `SimClient.cs:379-386`. Automatic acceptance therefore sends `OnDockingAccepted` and the destiny stop update, but no second command-4 reply. The Host's later `DockingReady` message runs the normal final checks.

Regression coverage:

- `crates/eve-sim/tests/link.rs::a_station_dock_approach_is_accepted_on_arrival_without_a_second_command` covers the sim-link flow from one out-of-range command through arrival, `OnDockingAccepted`, the stop update, Host approval, and `Docked`.
- `crates/eve-space/src/scene/tests.rs::a_dock_request_from_out_of_range_is_accepted_on_arrival_without_a_second_command` covers the scene state and stop actions.
- `a_new_movement_order_cancels_a_pending_station_dock_approach` and `a_station_dock_approach_that_arrives_while_blocked_is_not_accepted` cover cancellation and a restriction that changes during the approach.

## Confirmed and fixed: a second Hello bypassed protocol-version validation (P3)

`eve-sim::serve_connection` checked `PROTOCOL_VERSION` on the first frame, but passed later frames to `Sim::handle_with_frame_len`. The generic dispatch acknowledged any later `Message::Hello` with the server's version, regardless of the supplied version (`crates/eve-sim/src/lib.rs:47-77`, `crates/eve-space/src/sim/dispatch.rs:951-956` before the fix). ADR 0007 defines Hello as the sim-link version handshake.

The ready connection now rejects every second Hello with `InvalidData` before dispatch. The link regression first completes a valid handshake, sends a Hello with a mismatched version, and asserts that the stream closes without a `HelloAck`.

## Checked and not treated as a defect: persistence belongs to the Host

The audit did not identify a persistence defect, and source inspection confirms why: ADR 0007 assigns database and location ownership to the Host. `SpaceSessions` handles `SimDocked` by calling `FinishDockingAsync`; that path calls `_ships.DockAsync`, changes the character session, and sends the item and docking notifications (`dotnet/src/Eve.Services.Space/SpaceSessions.cs:1118-1121`, `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:609-660`). No duplicate Rust persistence store was added.

## Verification limits

The EveJS 261028.1 package has no project-level test script or docking-approach test suite, so its flow was source-traced rather than run. No graphical V24.01 client session was run. Rust link tests are headless protocol tests. The codebase-memory-mcp and Serena tools named in `CLAUDE.md` were not available in this session; repository reads and searches used shell `rg` and line-numbered source output instead.
