# Abyssal rebind bootstrap red gate

## Root cause

Run `43598431353` on `f4f4a29a` failed `sim::tests::abyssal_room_transfer_preserves_the_ship_and_rebinds_a_new_session_before_origin_return` at the assertion that rebind must produce at least three `DoDestinyUpdate` notifications for session 77. That expectation was stale: a newly bound/jumped ballpark receives its scene baseline in one `SetState`, followed by a ship-number update. The earlier `AddBalls2` was unnecessary for the room's static conduit and other entities already included in the full state.

The regression now checks the decoded wire notifications and their order: clock setup, `SetState`, ship numbers, then the bind acknowledgement. It verifies the full state's slim list and ball buffer contain the ego ship, room NPC, reward cache, and conduit. It also checks the expected agility, mass, and maximum-speed actions for the visible moving ships. The behavior change is test-only; the sim's existing `SetState` bootstrap is retained.

## Client and EveJS evidence

- V24.01 `eve/client/script/remote/michelle.py:968-1005` reads the full state, sets the ego, rejects slim items without balls, and marks the state valid. `:1256-1277` handles incremental `AddBalls2` chunks and skips slims already present. A complete `SetState` is therefore the ballpark baseline; the client does not require a duplicate `AddBalls2` for those objects.
- EveJS v0.12.9 source was inspected at `/home/user/evejs-261108.1`. `server/src/services/ship/beyonceService.js:4057-4091` bootstraps inside `Handle_MachoBindObject`; `server/src/network/packetDispatcher.js:618-631,688-700` awaits that handler before sending `CallRsp`. `server/src/space/runtime/scene/presentation.js:772-895,944-956` builds and sends the authoritative `SetState` before jump/login prime updates. `server/src/space/destiny/bootstrap/initialBallpark.js:48-52,78-101` only excludes entities carrying the `addBalls2` marker and incremental visibility scope. The abyssal conduit is created through `abyssalRoomMaterialization.js:156-180` and `abyssalRoomMarkers.js:174-228`, without that authored-static marker; `server/src/space/runtime/visibility.js:78-101` defines the incremental static categories.
- eve.rs `crates/eve-space/src/payload.rs:380-431` builds the ball buffer and slim list from the visible system bodies and ships. `crates/eve-space/src/scene/session.rs:286-390` sends the full state before subsequent ship-number updates for a newly entered scene. The pre-existing login/bind investigation in `docs/project/live-fix-ballpark-undock.md` records the broader V24 call flow and the distinct undock sequence.

The behavior matches the V24 parser and EveJS bind flow: the client receives a complete baseline before the bind call returns. The historical commit `e19162468` had already changed the ordinary jump regression to expect `SetState` followed by ship numbers; this abyssal rebind test retained the old three-update count.

## Other failures in the same CI run

- The Windows job failed its Rust workspace test step on the same abyssal assertion.
- The End to end job failed `test_a_corporation_unanchors_and_scoops_its_control_tower` while its first login handshake was receiving a server reply (`connection closed by the server`). The traceback ends before character selection or any ballpark bind, so this log does not tie that failure to the rebind. Its server-side cause was not established from the available failure output.
- A .NET shard failed `A_dock_returns_nothing_for_an_accepted_command_and_when_it_is_refused` in `SpaceSessions.DockAsync` at the MovementPolicy refusal path, before the sim Dock command. The focused local rerun passed; the CI-only cause remains undetermined.

These failures are not included in the test-only rebind change. No assertion was weakened or skipped.

## Verification

- Passed: `cargo fmt --all`.
- Passed: `cargo check -p eve-space -p eve-sim`.
- Passed: `cargo clippy -p eve-space -p eve-sim --all-targets -- -D warnings`.
- Passed: `cargo build --release -p eve-space -p eve-sim` with no warnings.
- Passed: the focused abyssal rebind simulation test (1 passed, no skips).
- Passed: full `cargo test -p eve-space` (1,024 unit tests, 40 integration tests, and doc tests; no skips).
- Passed on database `eve_p3`: registered Host ballpark bind test `CharacterHostTests.Space_login_binds_a_ballpark_that_contains_the_ego_ship` (1 passed, no skips), using the pinned SDE and Release `eve-sim` through `tools/dev/run-eve-live.sh`.
- Passed on `eve_p3`: `Eve.Services.Space.Tests.SpaceServiceTests.The_ballpark_bind_waits_for_the_simulation_scene_before_answering` and `AbyssalSpaceSessionsTests.Hidden_system_entry_and_relogin_set_both_system_ids_and_allow_real_ballpark_bind` (1 each, no skips).
- Passed: live headless undock test `test_a_ship_undocks_flies_and_docks_as_the_reference_did`.
- Not passed: live headless Abyssal combat test `test_solo_npc_combat_advances_three_rooms_and_returns_to_origin`. It stopped during setup because the local character's system did not match the test's Jita precondition (`30000142`); it did not reach Abyssal entry or rebind.
- The focused .NET docking test from the CI failure passed locally (1 passed, no skips).

Not checked: graphical V24.01 client behavior. EveJS code was inspected but not executed. The live Abyssal rebind scenario did not run to bind because of the local start-system mismatch.
