# Crate space audit fixes (90-audit-crate-space-r15)

## Verified findings

Both findings in the read-only audit were confirmed against the current code. Neither was a false positive.

### P2 — station docking approach did not dock on arrival

The V24.01 station menu path reaches `GetCloseAndTryCommand` in `/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:2981-2996`; `RealDock` in `menuSvcExtras/movementFunctions.py:503-518` sends one `bp.CmdDock` through `PerformSessionChange`. The autopilot callback in `parklife/autopilot.py:488-516` runs once and cancels its navigation task afterward. The station path has no arrival retry. Structures use a separate `structureDocking.Dock` call (`ui/services/structureDocking.py:64-81`).

EveJS 261028.1's `space/runtime/scene/pilotCommands.js:722-817` starts a station approach with `GotoPoint` and retains its target; `space/runtime/spaceRuntime/docking.js:155-217`, called from `scene/sceneFeatureHooks.js:280`, rechecks the arrival and enters the normal accept path. Its source also records retail observations where seven of nine approaches were accepted without a second `CmdDock` (`pilotCommands.js:722-725`). The Rust poll previously cleared the approach flag at range without calling `accept_docking`.

The scene now rechecks the existing simulation eligibility conditions at station arrival and sends the regular docking acceptance output. Structures retain their prior separate behavior. A new Rust regression test was added and run before the fix; it failed because arrival emitted no `OnDockingAccepted`. It passes with the fix. Additional Rust cases cover eligibility becoming invalid before arrival and a movement order cancelling the approach. No Host mutation path was added: the existing Host consumes the acceptance and readiness outputs, rechecks access and module conditions, persists the station transition, and sends the session and completion notifications (`dotnet/src/Eve.Services.Space/SpaceSessions.cs:1119-1135`, `SpaceSessions.Movement.cs:581-685`).

The live headless scenario checks one out-of-range `CmdDock`, the source-specific approach action, and the order `OnDockingAccepted` → `$sessionchange` → `OnDockingFinished`. The earlier `approach.json` trace remains labeled historical because it came from reference commit `b637d2a` and recorded a second request. eve.rs still uses its documented `FollowBall` approach while EveJS uses `GotoPoint`; this motion difference is retained in `docs/reference/compat/DEVIATIONS.md` (#20). A graphical V24.01 client run was not performed.

### P3 — reference scene tests passed without running when SDE was absent

`scene_matches_reference.rs` previously returned early whenever `scene_at` could not locate static data. The reference comparisons now require `Sde::locate()` and fail with a setup message if it returns `None`; the explicit `recorded_scene_comparisons_require_static_data` check makes this prerequisite visible. With the pinned SDE present, all eight tests pass. With `EVE_SDE_DIR` pointed at an empty directory, a selected comparison fails at the required-SDE check instead of being counted as a pass.

## Verification performed

- `cargo test -p eve-space --lib a_dock_request_from_out_of_range_is_accepted_on_arrival_without_a_second_command`: 1 passed. `cargo test -p eve-sim --test link a_station_dock_approach_is_accepted_on_arrival_without_a_second_command`: 1 passed.
- `cargo test -p eve-space --test scene_matches_reference`: 8 passed, 0 skipped with the default pinned SDE lookup under `.cache/sde`.
- Negative SDE check: with `EVE_SDE_DIR` pointed at an empty directory, the selected `binding_says_what_the_reference_said` test failed with `recorded scene comparisons require the pinned SDE`, instead of being counted as a pass.
- On database `eve_p7`, `SpaceServiceTests`: 178 passed, 0 skipped. On database `eve_p4`, `StructureDockingTests`: 6 passed, 0 skipped. These exercise the existing Host decision and persistence flow.
- The focused live headless docking case passed against eve.rs: one `race-1` case, exit status 0, with `EVE_LIVE_SLOW=1` and `EVE_MARKET_SEED=skip`. It verified the `FollowBall` approach and the client-visible acceptance, session transition, and completion sequence without a second `CmdDock`.
- An EveJS live run was attempted twice. Table generation completed, but the server exited before opening its port; retained output ended at `starting eve.js...` with no further error. The source comparison above is verified; a live EveJS comparison was not completed in this run.
- `cargo fmt --all -- --check`, `CARGO_INCREMENTAL=0 cargo check -j 2 -p eve-space -p eve-sim`, `CARGO_INCREMENTAL=0 cargo clippy -j 2 --workspace --all-targets -- -D warnings`, and `CARGO_INCREMENTAL=0 cargo build -j 2 -p eve-space -p eve-sim --release` passed after merging `origin/main`. The Release .NET Space test-project build passed with 0 warnings and 0 errors. `git diff --check` passed.
- After push, CI run 1664 for `8dcba66d6f47` failed on its first attempt only: `test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told` timed out waiting for `structureControl.TakeControl` at `client.recv`; the host logs contained no handler exception. V24.01 calls the RPC once (`/home/user/eve-logic/eve/client/script/ui/services/structure/structureControl.py:29-31`), and EveJS validates control, assumes it, and returns `null` (`/home/user/evejs-261028.1/server/src/services/structure/structureControlService.js:86-123`). The same E2E test passed alone and in the full `test_structures.py` file against `eve_p7`; CI attempt 2 passed. The timeout did not reproduce, so no production behavior or assertion was changed. The CI fast-live run reported 10 skips: 3 skill-speed cases and 7 slow-only cases; these were not counted as passes.
- `ci-status.py wait 42693468591` completed successfully on attempt 2: all jobs passed, with the above 10 fast-live skips reported separately.

## Not checked

- The graphical V24.01 client was not launched.
- The live EveJS docking scenario was not completed; both server attempts exited before opening the port.
- The local codebase-memory MCP and Serena tools required by `CLAUDE.md` were unavailable in this session; source inspection with file and line references was used instead.
