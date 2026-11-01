# Deep RPC validation fix: warp destination range

## Verified flow

The client delegates `CmdWarpToStuff(subject, subjectID, **kwargs)` from Michelle to the active remote ballpark (`eve/client/script/remote/michelle.py:732-737`). Whole-tree search found its callsites in bookmark/menu warps, scan-result and fleet warps, launch and dungeon actions, and the profiling helper. Representative callers are bookmark warps (`eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:408-421`) and scan-result warps (`eve/client/script/ui/services/menusvc.py:1421-1428`); the client supplies nonnegative arrival distances, with the bookmark default of 20,000 m. The call is fire-and-forget: the client wrapper returns None (`michelle.py:732-737`) and does not parse a result. The service maps rejected sim results to `CustomNotify` “You cannot warp there right now.” (`dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs:280`); accepted warps return None and the sim later drives the warp state.

The Host-side `[Method("CmdWarpToStuff")]` registration is at `dotnet/src/Eve.Services.Space/BeyonceService.cs:394-395`. It parses `minRange` at `:440` and previously rejected only non-finite values. It forwarded a finite negative distance to `SpaceSessions.WarpAsync` / `WarpToPointAsync`. In the Rust sim, `crates/eve-space/src/scene/warping.rs:18-33` accepts finite values and clamps negative point-warp distance with `range.max(0.0)` at `:31`; target warps likewise clamp in `warp_target_point` at `:188`. Thus malformed RPC input was silently turned into a successful zero-distance request.

## Change

The Host RPC now rejects negative or non-finite `minRange` through its existing `WarpRefusal()` path (`BeyonceService.cs:441-443`), preserving the client-visible refusal shape and preventing any simulation command. The regression assertion is in `dotnet/tests/Eve.Services.Space.Tests/SpaceServiceTests.cs:2031-2035`, in `A_warp_that_cannot_be_done_is_a_message_the_client_shows_and_one_of_another_kind_of_place_is_not_passed_on`: it checks the wrapped `CustomNotify` and that the sim command count is unchanged. With the new assertion present and the guard temporarily removed, the test failed because the simulation command count was 5 instead of 4; with the guard restored it passed.

EveJS 261028.1 `server/src/services/ship/beyonceService.js:3402-3406` normalizes `minRange` numerically but does not reject negative values; the current sim clamps them. This fix intentionally treats negative distance as malformed at the client RPC boundary rather than preserving that permissive behavior. EVE University’s Warp article describes arrival distances as selected warp-in distances and the minimum warpable distance; it does not define a negative arrival distance: https://wiki.eveuniversity.org/Warp and https://wiki.eveuniversity.org/Warp_Mechanics.

## Verification

- `dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- Final related-class database run on `eve_p3` through `tools/dev/db-lock.sh`: `SpaceServiceTests`, `ScanProbeHostTests`, `IndustrialInvulnerabilityHostTests`, and `StargateJumpWireTests`; 178 passed, 0 failed, 0 skipped. The focused test also passed separately (1/1).

## Audit scope

This increment covers one core movement RPC. It does not audit every Host service or every first-day/daily player flow; continue with dock/undock, target/combat and the other movement RPCs before treating the larger RPC-validation audit as complete. Existing inventory/loot and wallet handoffs document adjacent work but do not establish complete RPC hostile-input coverage. No real graphical-client run was made.
