# System scene transition handoff

## Scope and client path

The gate-arrival owner is `dotnet/src/Eve.Services.Space/SpaceSessions.Movement.cs`; Rust simulation is under `crates/eve-space/src/sim/`. Nine deleted Rust files were already present in the worktree and remain untouched.

- V24.01 `eve/client/script/parklife/gameui.py:430-472,604-637` consumes session location changes and waits for the ship after switching inflight. `eve/client/script/remote/michelle.py:120-140,314-359` rebuilds the ballpark from `session.solarsystemid`.
- `eve/client/script/parklife/sceneManager.py:211-218,527-530,638-657` consumes `locationid`, `solarsystemid2`, and `solarsystemid` for scene selection and attributes.
- `eve/client/script/ui/services/viewStateSvc.py:242-362,487-502,635-658` changes primary view and reads overlay sets. `spaceToSpaceTransition.py:72-80` uses destination system and gate, called from `environment/effects/Jump.py:314`. `mapViewPanel.py:69-72` closes the dockable panel; no direct `stationid2` use was found under `eve/client/script`.
- EveJS `server/src/space/transitions.js:2035-2238` changes the destination session, attaches the scene with initial state deferred until bind, then flushes its notification plan. Bind/jump entry points are `server/src/services/ship/beyonceService.js:4195-4233,4293-4328`. Jump-out starts jump FX before `OnJumpQueueUpdate` (`transitions.js:4124-4321)); V24 client queue consumer is `ui/services/jumpQueue.py:93-106`.

## Changes

- Gate arrival clears `stationid`, `stationid2`, and `structureid` in the same ordered `ClientSession.Change` as destination `solarsystemid`, `solarsystemid2`, and `locationid`. Unchanged fields remain absent from the wire change.
- Host regression checks session packet and state, the `MachoBindObject` tuple response, and destination `DoDestinyUpdate` after bind.
- Slow headless `test_jump.py` checks the destination session snapshot, bind response, notifications, and destination scene/state against EveJS. `tools/dev/agent-test.py` supports selecting it with `slow:test_jump.py`.
- Rust simulation keeps the matching `Jumping` result with its held `DoDestinyUpdate`, so the Host emits the queue notification after jump FX.
- `TestFront.Packets.CallWithKwargs` now supports the concurrent CharMgr wire test while retaining the existing `Call` behavior.

## Verification

- Local Host session-change regression passed earlier: 1 passed, 0 skipped. Local Space build later exited when Roslyn `csc` returned 137 under shared resource pressure; no local database was used. Local Rust package fmt passed; workspace checks are blocked by the nine inherited deletions.
- Actions Host session-change `40083151979` passed (1, 0 skipped); Space jump-state `40083365205` passed. Rust 1.94.1 format/clippy/tests, .NET build, Windows headless live-client and other jobs passed in push run `40082922998`; its Host shard failed an unrelated wallet test fixture, subsequently fixed upstream.
- On `9349cae6`, required Host `40084107903`, Space `40084118944`, and wallet `40084131964` failed during solution build because `CharMgrPublicInfoHostTests.cs:53` called the missing `Packets.CallWithKwargs`. That helper has now been added in current main; the three test filters need rerunning on the new SHA.
- Slow live jump walk `40084153623` is still running. Earlier walk `40077070295` crossed systems and reported no mismatch in session snapshot, bind result, notification order, or destination scene/state, but failed the earlier warp update comparison and rest timing. No graphical V24.01 UI exception log was captured; the Windows workflow uses a headless client.
- No ci.yml run was found for `81852f14`. Inspect push CI for the new SHA after pushing.
