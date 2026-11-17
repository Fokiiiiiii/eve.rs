# main CI 1832: watcher undock Destiny deliveries

## Findings

Run 1832 (`44021832645`, head `acb56fe0e9dd`) failed in Linux and Windows Rust tests at `others_match_reference::the_watcher_is_shown_the_actor_that_undocks_as_the_reference_showed_it`. It observed one `DoDestinyUpdate`; the committed EVE.js recording has two. The end-to-end and all .NET shards in that run succeeded.

The production change in `c97aaa214` combined the packaged `AddBalls2` introduction and station-undock movement prime into one notification. That erased the delivery boundary and `waitForBubble` flag that the reference sends to an already bound watcher.

The pinned EveJS reference (`261108.1`) was started locally with the pinned SDE build `3396210`; `record_character_scenarios.py --only others` completed at `2026-11-17T05:56:41Z`. The active recording and committed `oracle/fixtures/scenarios/others.json` produce the same sequence:

1. `waitForBubble=true`: `PackagedAction`, `SetBallAgility`, `SetBallMass`, `SetMaxSpeed`.
2. `waitForBubble=false`: `SetBallMassive`, `SetBallMass`, `SetBallVelocity`, `GotoDirection`.

The EveJS implementation keeps character and bubble delivery histories distinct in `server/src/space/destiny/batching/destinyHistory.js:401-480`, plans the character batch before bubble batches in `server/src/space/destiny/batching/parkUpdateBatcher.js:86-157`, and sends one notification per delivery in `server/src/space/destiny/dispatch/batchDelivery.js:195-229`. Its `visibility.js:653-676` builds the packaged add and movement actions at one stamp; sharing a stamp does not make them one notification.

V24.01 processes each entry of `DoDestinyUpdates` separately (`eve/client/script/remote/michelle.py:514-524`). `DoDestinyUpdate` expands `PackagedAction` and passes the flag to `FlushState` (`michelle.py:482-511`). `DoPreTick` stops while the first history entry has `waitForBubble=true` (`michelle.py:900-907`); when the same-stamp follow-up arrives, `FlushState` appends its actions and clears that wait (`michelle.py:1120-1137`). `AddBalls2` reads the ball stream and initializes each slim item (`michelle.py:1256-1278`). Thus the two-notification order and flag are client-visible synchronization behavior, not incidental packet grouping.

## Change

`crates/eve-space/src/crowd/ships.rs:225-236` again sends the introduction with `waitForBubble` set while the pilot is leaving the station, then sends the movement prime as a separate same-stamp update. The crowd test now checks both action lists, recipients, flags and stamp. The simulation test expects the enter reply followed by both watcher notifications. The recorded end-to-end comparison in `crates/eve-space/tests/others_match_reference.rs:182-233` remains strict and still checks the complete wire actions, wait flags, slim data and ball bytes.

## Checks

- EveJS live recording: completed; its action names and `waitForBubble` values match the committed fixture.
- Focused watcher/reference Rust test: passed after the change.
- Full `cargo test -p eve-space`: 1,081 passed, 0 failed, 0 ignored.
- `cargo +1.99.0 fmt --all -- --check`, `cargo +1.99.0 check --release -p eve-space`, and `cargo +1.99.0 clippy --workspace --all-targets -- -D warnings`: passed.
- `Eve.Services.Space.Tests` Release build: 0 warnings, 0 errors.
- `HackingMgrServiceTests`: 20 passed, 0 skipped on `eve_p3`.
- .NET space shards 9/32, 16/32 and 21/32: each 39 passed, 0 skipped on `eve_p3`.
- Fast server-stack/headless E2E: exit 0, no failures reported; pytest skipped 9 tests. The skips were one skill-training-speed case and the eight slow-only cases in movement, warp, jump (two), others, target, module and health.
- `test_others.py::test_two_ships_see_each_other_as_they_did_in_the_reference`: passed separately (1 test, 0 skipped) with `EVE_LIVE_SLOW=1`; the fast E2E skips this four-minute scenario.
- The live Host's static `sde` schema was build `3396210`; the standard startup script separately reported mission schema `sde_mission` build `3405148`.
- Graphical V24.01 client verification: not performed.
