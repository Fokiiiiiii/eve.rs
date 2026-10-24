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
- The concurrent CharMgr host test adds `Packets.CallWithKwargs`; its duplicate helper definition has been removed after CI caught the merge.

## Verification

- Local Host session-change regression passed earlier: 1 passed, 0 skipped. Local Space build later exited when Roslyn `csc` returned 137 under shared resource pressure; no local database was used. Local Rust package fmt passed; workspace checks are blocked by the nine inherited deletions.
- Actions Host session-change `40083151979` passed (1, 0 skipped); Space jump-state `40083365205` passed. Rust 1.94.1 format/clippy/tests, .NET build, Windows headless live-client and other jobs passed in push run `40082922998`; its Host shard failed an unrelated wallet test fixture, subsequently fixed upstream.
- On `9349cae6`, Host `40084107903`, Space `40084118944`, and wallet `40084131964` failed compilation because `CharMgrPublicInfoHostTests.cs:53` called the then-missing `Packets.CallWithKwargs`.
- On `6fd08e75`, Host `40085095316`, Space `40085093681`, and wallet `40085094182` failed compilation because merging the concurrent helper change left two identical `CallWithKwargs` definitions. CI caught this duplicate and it is removed now. Slow walk `40085095367` was still running at handoff update.
- Earlier slow walk `40084153623` crossed systems and had no mismatch in session snapshot, rebind result, notification order, or destination scene/state. It failed the unrelated warp comparison: first update had no actions rather than `SetMaxSpeed/WarpTo/OnSpecialFX/SetSpeedFraction`, and rest was at 86.4s vs 82.86s in the recording. No comparison has been relaxed.
- Push CI `40085139075` on `0fff3636` has Rust 1.94.1 fmt/clippy/tests, Python and fixture checks passed; .NET build failed on the duplicate helper. End-to-end and Windows jobs were still running at update. No ci.yml run was found for `6fd08e75`.
- No graphical V24.01 UI exception log was captured. The Windows workflow uses a headless client.
