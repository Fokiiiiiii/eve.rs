# System scene transition handoff

## Scope and current source paths

This checkout was advanced through `origin/main` at the start of the task. The task changes were pushed on main through merge commit `b7ede94a`, which also incorporates current origin/main. The requested source layout has since been split: gate-jump completion is in `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs`; the current Rust simulation lives under `crates/eve-space/src/sim/`. The nine pre-existing deleted files in the worktree were preserved and are unrelated to this change.

## Client path and wire contract

- `eve/client/script/parklife/gameui.py:430-472` routes session changes using `stationid`, `structureid`, and `solarsystemid`; `gameui.py:604-637` switches to inflight on the solar-system change and waits for the ship in the ballpark.
- `eve/client/script/remote/michelle.py:120-140` rebuilds the ballpark from `session.solarsystemid`; `michelle.py:314-359` requests formations and treats the ballpark as unavailable until ready.
- `eve/client/script/parklife/sceneManager.py:211-218` observes `locationid`; `sceneManager.py:527-530` selects the scene from `solarsystemid2`; `sceneManager.py:638-657` applies solar-system scene attributes from `solarsystemid`.
- `eve/client/script/ui/services/viewStateSvc.py:242-362` activates primary views and updates overlays; `viewStateSvc.py:487-502` routes `ChangePrimaryView`; `viewStateSvc.py:635-658` reads overlay sets from the active views.
- `eve/client/script/ui/view/spaceToSpaceTransition.py:72-80` initializes a gate transition from the destination system and gate; `eve/client/script/environment/effects/Jump.py:314` is its caller.
- `eve/client/script/ui/shared/mapView/mapViewPanel.py:69-72` closes the dockable panel and clears its `mapView`. A script-wide search found no direct `stationid2` consumer under `eve/client/script`; EveJS also intentionally omits it from the undock notification (`server/src/space/transitions.js:646-666`).
- EveJS completes gate arrival by changing the character session, attaching the destination scene with initial state deferred until bind, then flushing the session notification plan (`server/src/space/transitions.js:2035-2238`). Its beyonce jump and bind entry points are `server/src/services/ship/beyonceService.js:4195-4233` and `4293-4328`. At jump-out, EveJS starts the jump FX before sending `OnJumpQueueUpdate` (`server/src/space/transitions.js:4124-4321`, especially 4285-4296); the V24.01 consumer stores that queue state in `eve/client/script/ui/services/jumpQueue.py:93-106`.

## Changes

- Gate arrival now clears `stationid`, `stationid2`, and `structureid` through the same ordered `ClientSession.Change` call as the destination `solarsystemid`, `solarsystemid2`, and `locationid`. Unchanged fields stay absent from the wire notification.
- `HostServerTests` now checks the session-change packet and server session values, the `MachoBindObject` tuple response, and that destination `DoDestinyUpdate` messages follow the bind response.
- The slow headless jump walk checks the destination session snapshot and bind response shape against the EveJS recording. `tools/dev/agent-test.py` now accepts `slow:test_jump.py` to run one selected slow walk with the slow-test flag enabled.

## Verification

- Passed locally: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore --filter 'FullyQualifiedName~session_change' -p:UseSharedCompilation=false -m:2` (1 passed, 0 skipped). This also built the changed Space service project.
- Passed Actions run `40070546520`: the required `Eve.Host.Tests` session-change filter ran 1 test, 0 skipped.
- Failed push CI run `40070537682`: the fast live Astrahus anchoring test received `You must be in space to deploy a structure` at `tests/test_structures.py:482`; the Space test shard was cancelled after the live job failed. This is outside the jump scenario and no causal link to the session change was found.
- The broad slow Actions run `40073392170` failed in `test_crab_walk.py` before producing a JUnit report, so it did not establish the jump walk result.
- Focused run `40074755417`: Host test 1 passed, 0 skipped; live jump test ran but failed the reference comparison. It saw the first warp update later than the fixture’s first event and saw `OnJumpQueueUpdate` before the jump-out `DoDestinyUpdate` (the fixture orders these the other way). In `crates/eve-sim/src/lib.rs`, tick alignment held the `DoDestinyUpdate` to the second boundary while letting `Jumping` complete the Host command immediately; the Host then sent the queue notification first. The current change holds the matching `Jumping` result with that update so the Host sends the queue notification afterwards.
- Local Rust checks: package fmt passed. Workspace fmt, `cargo check -p eve-sim`, and workspace clippy cannot compile the worktree because the pre-existing deleted `crates/eve-space/src/targeting` module breaks `eve-space`. The deleted files were left untouched.
- Push CI run `40077058438` found a Clippy `if_same_then_else` warning in the notification-order fix; commit `5e694c21` combined the identical branches. On that commit, Rust tests, Rust 1.94.1 format/clippy, .NET build, Windows live-client, and end-to-end jobs passed. The required Host session-change filter passed in run `40077826023` (1 passed, 0 skipped).
- The push CI run `40077808041` failed `StargateJumpWireTests.Bound_client_warp_then_stargate_jump_uses_the_wire_and_clears_warp_state` because `IsWarping` remained true after the jump RPC; the other .NET test failure was `TradeAuthorityTests.Stage_refuses_an_item_outside_the_hangar_or_not_owned_by_the_offering_pilot`. The jump cleanup now runs after the awaited self-destruct and contraband cleanup, immediately before the queue update. The focused Space Actions run did not return the test output in its log tail, and was rerun with diagnostic command codes in the assertion.
- Focused live run `40077842646` failed after starting the live walk. In earlier run `40077070295`, the walk crossed systems and its session snapshot, rebind result, notification order, and destination scene/state checks produced no reported mismatch; it failed the earlier warp update comparison and rest timing (86.12 s vs 82.86 s). The Windows live-client job passed on `40077808041`.
- Local .NET build of `Eve.Services.Space` was terminated when `csc` exited with code 137 under shared resource pressure. No local database was used. A graphical V24.01 UI exception log was not captured.

- After `b7ede94a`, the required Host filter, focused Space warp-state test, and slow jump walk were dispatched again; results are pending.
