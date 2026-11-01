# RPC Validation Deepfix Handoff

## Gate checkpoint

The newest completed main CI run checked after merging origin/main is Run 1388 at `e847e383a731`, green. The preceding failure recorded in the upstream checkpoint was the moon-chunk docking test: `test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told` failed while its helper received `DockingRequestDenied`. The current main includes the helper adjustment that retries one such denial after waiting 11 seconds, while preserving the 120-second deadline and failing on a repeated or different refusal. Its evidence and client contract are retained in the merge history; this warp change does not alter that docking behavior.

## Warp input validation

### Client flow and wire contract

The V24.01 client delegates `CmdWarpToStuff(subject, subjectID, **kwargs)` from Michelle to the active remote ballpark (`/home/user/eve-logic/eve/client/script/remote/michelle.py:732-737`). Whole-tree callsite search found uses for bookmarks and menu actions, scan results and fleet warps, launches, dungeon sites and the profiling helper. Representative callers are `/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:408-421` and `/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:1421-1428`; bookmark warp distance defaults to 20,000 m and the displayed callers pass the selected nonnegative distance. Michelle returns None and does not consume a result (`michelle.py:732-737`). The service reports a rejected warp with the client-visible `CustomNotify` text “You cannot warp there right now.” (`dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:280`); accepted warp commands return None and the simulation drives the warp state.

### Defect and fix

The Host registers `CmdWarpToStuff` at `dotnet/src/Eve.Services.Space/BeyonceService.cs:394-395`. It parsed `minRange` at line 440 and rejected non-finite values, but let finite negative values through to `SpaceSessions.WarpAsync` or `WarpToPointAsync`. Rust sim `crates/eve-space/src/scene/warping.rs:18-33` accepts finite values and clamps negative point-warp ranges with `range.max(0.0)` at line 31; target warps clamp similarly at line 188. A malformed negative range therefore became an accepted zero-range warp.

The Host now rejects negative and non-finite ranges through the existing `WarpRefusal()` path (`BeyonceService.cs:441-443`). The wire regression in `dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:2031-2035`, within `A_warp_that_cannot_be_done_is_a_message_the_client_shows_and_one_of_another_kind_of_place_is_not_passed_on`, verifies `CustomNotify` and that the simulation receives no command. With the guard temporarily removed, the test failed because the sim command count became 5 instead of 4; with the guard restored it passed.

EveJS 261028.1 `/home/user/evejs-261028.1/src/server/src/services/ship/beyonceService.js:3402-3406` normalizes `minRange` but does not reject negatives, and the sim clamps them. This fix intentionally rejects the malformed client input at the RPC boundary. EVE University [Warp](https://wiki.eveuniversity.org/Warp) and [Warp Mechanics](https://wiki.eveuniversity.org/Warp_Mechanics) describe arrival distances and the minimum warp distance; neither gives negative arrival distance a gameplay meaning.

## Verification

- .NET build of `dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj`, including after merge: passed, 0 warnings/errors.
- Pre-merge related-class database run on `eve_p3` through `tools/dev/db-lock.sh`: `SpaceServiceTests`, `ScanProbeHostTests`, `IndustrialInvulnerabilityHostTests`, `StargateJumpWireTests`; 178 passed, 0 failed, 0 skipped. After merge, those plus `CynoJumpHostTests`, `StructureDockingTests`, `StructureHangarTests`, `StructureUndockingTests`, and `TetherPolicyTests` passed: 225 passed, 0 failed, 0 skipped.
- The merge-touched moon-extraction headless scenario passed against the live local stack: 1 passed. System Python reported unknown `timeout` and `timeout_method` pytest options because the optional pytest timeout plugin is absent.
- Pre-fix failure was reproduced as described above. No graphical-client run was made.

## Remaining audit scope

This increment audits one core movement RPC. It does not complete hostile, malformed or out-of-order input coverage for every Host service or every first-day and daily player flow. Continue with undock/dock, target/combat and the other movement RPCs. Existing inventory/loot, wallet, wormhole and NPC handoffs document adjacent gameplay work but do not establish complete RPC-validation coverage.
