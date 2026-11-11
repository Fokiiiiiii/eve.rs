# Main CI run 1738 failure audit

## Failure

Main CI run 1738 at `bcdf319bd91f` failed `test_a_corporation_unanchors_and_scoops_its_control_tower` in the live headless client. After 90 seconds of scoop retries, `ship.Scoop` still returned `TargetTooFar`.

The test sent `CmdFollowBall(tower, 1_000)` once, then retried only `Scoop`. The host log from a focused local run recorded `the simulation refused command 5 ... IN_WARP`. The RPC itself returned no error, so the test did not know that movement had been refused. That local run passed only because the ship happened to be within scoop range; it did not prove that the follow command was accepted.

## Source evidence

The V24.01 client defines `InWarp()` as the ship Ball's mode being `DSTBALL_WARP` (`/home/user/eve-logic/eve/client/script/remote/michelle.py:433-435`) and the autopilot waits while `InWarp()` is true before executing its next system-navigation action (`/home/user/eve-logic/eve/client/script/parklife/autopilot.py:500-507`). EveJS v0.12.9 changes the Ball to `STOP` at warp dropout (`/home/user/evejs-261108.1/server/src/space/destiny/simulation/evolve.js:1075-1086`), and its follow/orbit guard rejects an order only while `entity.mode === "WARP"` (`/home/user/evejs-261108.1/server/src/space/runtime/destinyMovementEngine.js:231-247`).

The Rust simulation represented both the actual warp and its long post-drop coast as a single active flight. It refused movement while that flight existed, unlike EveJS after `stopBall` changes the dropped ship to `STOP` and clears its warp target (`/home/user/evejs-261108.1/server/src/space/destiny/simulation/evolve.js:1075-1086, 236-241`). It now permits steering after its `Coasting` phase begins, preserving the settled position and velocity and transferring control to the new course (`crates/eve-space/src/scene/movement.rs:191-211`; the post-drop coast is `crates/eve-space/src/flight.rs:205-237`). It continues to refuse steering while aligning or in warp.

The client-facing `SetBallMassive(ship, 0)` is sent two seconds before the simulated dropout and is not evidence that the warp has ended. The Host previously cleared `_warping` when it later saw a client-facing `Stop` Destiny action; that can be much later than the actual drop because eve.rs sends that `Stop` at the end of the coast. The simulation now reports the actual `Milestone::Dropped` to the Host as SimLink kind 162 (`WarpDropped`, protocol 97). The Host clears only its own in-warp gate on this event. This internal message adds no client RPC or notification.

## Correction

The live test waits for this ship's warp-dropout `SetBallMassive(ship, 0)` action and lets the scheduled two-second transition pass before dropping the tower at the warp endpoint. Once unanchoring completes, it sends `CmdFollowBall` and checks the exact Destiny action `(shipID, towerID, 1000)`, then verifies the scoop and inventory/state transition. This ties the test to the actual simulated dropout and accepted steering instead of treating an RPC `None` as proof that the simulation accepted movement.

## Validation

- The prior focused live run passed, but its host log showed `FollowBall` was refused with `IN_WARP`; the result was not valid evidence that following worked.
- The first correction attempt waited for a final `Stop` action; the focused run exceeded its 300-second test limit while waiting. A second attempt confirmed repeated `FollowBall` orders were still issued during `IN_WARP` for 90 seconds, so the test now waits for the client-visible dropout action instead.
- Rust `cargo check -p eve-space --release`, `cargo clippy -p eve-space --release -- -D warnings`, and `cargo fmt --all` passed. The focused coast-follow regression and SimLink round-trip test each passed (1 passed, no skips).
- Release builds for `Eve.Space`, `Eve.Services.Space`, `Eve.Space.Tests`, and `Eve.Services.Space.Tests` passed with 0 warnings and 0 errors. The `SimCodecTests` class passed all 499 tests; the `SpaceServiceTests` and `ScanProbeHostTests` classes passed all 200 tests after updating the obsolete assertion that a client `Stop` clears server warp state.
- The focused live control-tower Drop, unanchor, FollowBall, and Scoop walk passed against `eve_p3` (1 passed, no skips). It verified the exact `(shipID, towerID, 1000)` Destiny action and the durable hull-to-cargo transition.
- Main CI run 1741 exposed a missing Rust fixture-replay arm for the new `WarpDropped` kind. Added that arm to `crates/eve-space/tests/simlink_fixtures.rs`; the failing `every_valid_frame_encodes_to_its_bytes_and_decodes_back` regression now passes locally. The follow-up main CI run is pending.
- Graphical V24.01 client behavior was not checked.
- The EveJS behavior was source-traced but its server flow was not independently executed in this run.
