# RPC Validation Deepfix Handoff

## Gate checkpoint

The newest completed main CI run checked after merging origin/main is Run 1388 at `e847e383a731`, green. The preceding failure recorded in the upstream checkpoint was the moon-chunk docking test: `test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told` failed while its helper received `DockingRequestDenied`. The current main includes the helper adjustment that retries one such denial after waiting 11 seconds, while preserving the 120-second deadline and failing on a repeated or different refusal. Its evidence and client contract are retained in the merge history; this warp change does not alter that docking behavior.

## Warp input validation

### Client flow and wire contract

The V24.01 client delegates `CmdWarpToStuff(subject, subjectID, **kwargs)` from Michelle to the active remote ballpark (`/home/user/eve-logic/eve/client/script/remote/michelle.py:732-737`). A whole-tree search found direct call sites at `util/profiling.py:317`, `ui/shared/addressBookEntries.py:155`, `ui/shared/neocom/journal.py:452`, `ui/shared/infoPanels/listSurroundingsBtn.py:381`, `ui/shared/agencyNew/contentPieces/escalationContentPiece.py:74`, `ui/services/menusvc.py:1427,2737,2747,2757`, `ui/services/menuSvcExtras/movementFunctions.py:418,432,451,462,488`, `parklife/transmissionMgr.py:142`, `objectives/client/objective_task/dungeon.py:341,369`, and `jobboard/client/features/dungeons/escalation_sites/job.py:57` (all under `/home/user/eve-logic`). These cover bookmarks, launches, item/character/menu warps, epic and external dungeon instances, moon mining points, operation sites, and fleet warps. The separate autopilot RPC is `parklife/autopilot.py:464`; Michelle's external dungeon convenience method delegates at `remote/michelle.py:740`. Calls are issued for side effects; Michelle returns None without reading a result (`remote/michelle.py:732-741`). Bookmark warp distance defaults to 20,000 m and selected distances are forwarded (`ui/services/menuSvcExtras/movementFunctions.py:408-421`). The service reports a rejected warp with the client-visible `CustomNotify` text “You cannot warp there right now.” (`dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:280`); accepted warp commands return None and the simulation drives the warp state.

### Defect and fix

The Host registers `CmdWarpToStuff` at `dotnet/src/Eve.Services.Space/BeyonceService.cs:394-395`. It parsed `minRange` at line 440 and rejected non-finite values, but let finite negative values through to `SpaceSessions.WarpAsync` or `WarpToPointAsync`. Rust sim `crates/eve-space/src/scene/warping.rs:18-33` accepts finite values and clamps negative point-warp ranges with `range.max(0.0)` at line 31; target warps clamp similarly at line 188. A malformed negative range therefore became an accepted zero-range warp.

The Host now rejects negative and non-finite ranges through the existing `WarpRefusal()` path (`BeyonceService.cs:441-443`). The wire regression in `dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:2031-2035`, within `A_warp_that_cannot_be_done_is_a_message_the_client_shows_and_one_of_another_kind_of_place_is_not_passed_on`, verifies `CustomNotify` and that the simulation receives no command. With the guard temporarily removed, the test failed because the sim command count became 5 instead of 4; with the guard restored it passed.

EveJS 261028.1 `/home/user/evejs-261028.1/src/server/src/services/ship/beyonceService.js:3402-3406` normalizes `minRange` but does not reject negatives, and the sim clamps them. This fix intentionally rejects the malformed client input at the RPC boundary. EVE University [Warp](https://wiki.eveuniversity.org/Warp) and [Warp Mechanics](https://wiki.eveuniversity.org/Warp_Mechanics) describe arrival distances and the minimum warp distance; neither gives negative arrival distance a gameplay meaning.

## Verification

- .NET build of `dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj`, including after merge: passed, 0 warnings/errors.
- Pre-merge related-class database run on `eve_p3` through `tools/dev/db-lock.sh`: `SpaceServiceTests`, `ScanProbeHostTests`, `IndustrialInvulnerabilityHostTests`, `StargateJumpWireTests`; 178 passed, 0 failed, 0 skipped. After merge, those plus `CynoJumpHostTests`, `StructureDockingTests`, `StructureHangarTests`, `StructureUndockingTests`, and `TetherPolicyTests` passed: 225 passed, 0 failed, 0 skipped.
- The merge-touched moon-extraction headless scenario passed against the live local stack: 1 passed. System Python reported unknown `timeout` and `timeout_method` pytest options because the optional pytest timeout plugin is absent.
- Pre-fix failure was reproduced as described above. No graphical-client run was made.

## Merge follow-up: docking test fixture

The next origin/main merge changed the NPC station docking range to 500 m (`crates/eve-space/src/scene/mod.rs:83-88`), consistent with EVE University [Warp Mechanics](https://wiki.eveuniversity.org/Warp_Mechanics). The existing targeting test placed the docking pilot 1,000 m from the surface but still expected immediate acceptance. The first merged `cargo test -p eve-space dock` run caught this: `targeting::tests::a_ship_that_docks_is_lost_when_it_has_docked` received `Approaching` instead of `Accepted`. The fixture now places that ship 400 m from the surface (`crates/eve-space/src/targeting/tests.rs:585`); the test still verifies that completing the dock drops the target. The exact test and the dock-filtered set pass after this fixture correction.

After merging origin/main at `a9fe2540`, `cargo +1.94.1 check -p eve-space`, `cargo +1.94.1 fmt --all -- --check`, and workspace clippy with `-D warnings` passed. The dock-filtered Rust tests passed (40 unit tests and 3 reference tests). `SpaceServiceTests` and `StructureUndockingTests` passed on `eve_p3` (155 passed, 0 failed, 0 skipped). Main CI Run 1389 at `a9fe2540` completed red because `targeting::tests::a_ship_that_docks_is_lost_when_it_has_docked` still expected immediate acceptance from 1,000 m after NPC station range changed to 500 m. The worktree fixture now places the pilot 400 m from the surface; the exact test and dock-filtered set pass locally. RED GATE therefore blocks unrelated pushes until main has a green completed CI run.

Origin/main's fix commit `75e89ae1` was merged after Run 1389. The conflict was resolved in favor of the same 400 m fixture, safely inside the 500 m NPC station range. After this merge, the exact targeting test passed; the dock-filtered tests passed (40 unit and 3 reference tests); the warp-filtered Rust tests passed (17 unit and 3 reference tests); and `cargo +1.94.1 check -p eve-space`, `fmt --all -- --check`, and workspace clippy passed. The Space tests project built with 0 warnings/errors; `SpaceServiceTests` and `StructureUndockingTests` passed on `eve_p3` (155 passed, 0 failed, 0 skipped). Run 1390 for `75e89ae1` completed successfully.

## Push checkpoint

The code branch was pushed to main at `8df1b530d306` after CI Run 1390 completed green and `git fetch origin main && git merge origin/main` reported up to date. Push-triggered CI Run 1391 (`41354321450`) completed successfully; no failed tests or jobs were reported. A documentation checkpoint was then pushed as `37e93e95` after merging origin/main at `0978698f`.

## Warp destination geometry and overflow

### Evidence and behavior

The client delegates target warps through `CmdWarpToStuff`; representative bookmark callers supply a selectable arrival range with a default of 20,000 m (`/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:408-421`). Client-facing failures are `CustomNotify`, and Michelle does not consume an RPC return value (`/home/user/eve-logic/eve/client/script/remote/michelle.py:732-737`). The accepted movement state is therefore observed through the space simulation notifications.

In `crates/eve-space/src/scene/warping.rs:83-101`, the sim now computes the distance to the raw destination, refuses a stop range greater than that distance, and rejects non-finite intermediate destination coordinates or total distances before mutating `Course`, `Flight`, or `warp_target`. Previously a positive range larger than the destination distance placed the warp endpoint behind the ship and emitted a warp. Very large finite coordinates could overflow the Euclidean distance and be accepted into warp state.

Two regression tests cover those cases and assert refusal without starting warp (`crates/eve-space/src/scene/tests.rs:1034-1049,1066-1077`). Both were reproduced as failing before the corresponding guards and passed after. The client-visible Host adapter maps simulation refusal to the existing generic warp notification (`dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:280`).

EveJS 261028.1 `server/src/services/ship/warpCommands.js:424-457,994-1235` clamps negative stop ranges but checks only the minimum warp distance; it does not reject a range beyond the target or overflowed coordinates. eve.rs deliberately rejects those invalid geometries before state mutation. This is a defensive deviation: EVE University [Warp](https://wiki.eveuniversity.org/Warp) documents the 150 km minimum and arrival ranges, but does not define warps whose stop point lies beyond the destination. No client caller in the whole-tree search requests a stop range larger than its selected destination distance or supplies non-finite coordinates.

`cargo +1.94.1 test -p eve-space a_warp` passed after both guards. `cargo +1.94.1 check -p eve-space`, `cargo +1.94.1 fmt --all -- --check`, and workspace clippy with `-D warnings` passed. No graphical-client run was made.

## CmdDock accepted response shape

The only direct V24.01 `CmdDock` caller found by whole-tree search is `movementFunctions.RealDock` (`eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:502-519`), reached from the station menu via `menusvc.DockStation` (`eve/client/script/ui/services/menusvc.py:2990-2995`). It calls `PerformSessionChange('dock', bp.CmdDock, itemID, session.shipid)` at `movementFunctions.py:516` as a statement and does not read the RPC result. The bound Host method registers `CmdDock` and forwards the result of `space.DockAsync` (`dotnet/src/Eve.Services.Space/BeyonceService.cs:813-822`). EveJS 261028.1 `server/src/services/ship/beyonceService.js:4210-4281` explicitly returns `null` on accepted docking; the client wire value is `None`.

`SpaceSessions.DockAsync` previously returned the sim's acceptance timestamp as a `PyLong`, even though the simulation's later `SimDocked` event drives the actual transition and the client ignores the call result. Its accepted branch now returns `None` (`dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:25-52`). The focused registered Host regression is `SpaceServiceTests.A_dock_returns_nothing_for_an_accepted_command_and_when_it_is_refused` (`dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:3033-3045`). Before the service fix, changing the assertion to the client-required `PyNone` failed: expected `PyNone`, actual `PyLong` timestamp `134351527136350000`. After the fix the same test passed, and the DB-backed `SpaceServiceTests` plus `StructureUndockingTests` passed on `eve_p3` (155 passed, 0 failed, 0 skipped). This is a wire-shape correction only; the accepted `Dock` sim command and subsequent event-driven state transition remain in place.

## Remaining audit scope

This increment covers invalid warp inputs and the accepted `CmdDock` reply shape, with their registered Host tests and sim transition checks. It does not complete hostile, malformed or out-of-order input coverage for every Host service or every first-day and daily player flow. Continue with undock, target/combat and the other movement RPCs. Existing inventory/loot, wallet, wormhole and NPC handoffs document adjacent gameplay work but do not establish complete RPC-validation coverage.
