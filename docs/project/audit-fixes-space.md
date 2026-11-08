# Space services audit fixes

This records the confirmed findings from `/home/user/reports/90-audit-eve-services-space-r14.md` and the fixes made against the V24.01 client source in `/home/user/eve-logic` and EveJS v0.12.9 in `/home/user/evejs-261028.1`.

## Findings

### P1: concurrent analyzer starts could pay out twice

**Confirmed.** Before the fix, analyzer validation happened before waiting for simulation acceptance, with no reservation shared across pilots. The settlement path could write loot whenever the target row existed, including after another attempt had already looted it.

V24.01 sends analyzer activation through the module activation flow in `eve/client/script/ui/hacking/hackingUISvc.py:18, 26, 55-64`; it does not serialize pilots. EveJS rejects a non-secure target and marks it `BEING_HACKED` while creating the attempt in `server/src/services/exploration/hackingMgrService.js:610-618, 688-691`. Its module operation also checks the target busy state in `server/src/services/dogma/dogmaService/moduleOperation.js:1563-1579`.

The Host now reserves a secure container in PostgreSQL before asking the simulation to start the analyzer. A partial unique index allows only one active reservation per container. A simulation refusal, exception, or session close releases a pending reservation. Settlement verifies the reservation owner, locks and checks the secure container, then commits the loot, state transition, and receipt in one transaction. Replaying a committed settlement returns its stored contents without creating loot again. Host startup cancels reservations whose in-memory boards were lost at process exit.

Coverage: `ContainerSiteEndToEndTests` checks simultaneous reservations, commit before notification, duplicate settlement and rollback/retry; `HackingMgrServiceTests` checks simulation refusal releasing the actual module-path reservation. A same-character guard also rejects two pending analyzer starts on different targets; its regression test was observed failing before the guard was added.

### P1: the win notification could precede loot persistence

**Confirmed.** V24.01 immediately consumes `EVENT_GAME_WON` to show the win and close the UI (`eve/client/script/ui/hacking/hackingUISvc.py:84-95`). EveJS commits the outcome before sending the terminal event (`server/src/services/exploration/hackingMgrService.js:890-923`).

The Host now retains terminal events as a pending outcome and sends them only after the site settlement returns successfully. A database failure leaves the attempt available for retry; after session close, a shared `LocalTimers` retry keeps trying settlement. The receipt makes a retry safe if the transaction committed but the response was lost.

Coverage: `ContainerSiteEndToEndTests` blocks notification until commit and injects a settlement rollback, then retries the same reservation. `HackingMgrServiceTests` checks pending terminal events survive a failed settlement and checks retry after session close.

### P2: a replacement session could control an old board

**Confirmed.** V24.01 delivers board actions through `hackingMgr` (`eve/client/script/ui/hacking/hackingUISvc.py:197-199, 262-281`). EveJS stores the owning session with the attempt and checks it for actions and quit (`server/src/services/exploration/hackingMgrService.js:639-650, 853-860, 1001-1008`).

The Host now binds each window and pending reservation to the creating `ClientSession`. RPC actions from another session cannot advance or end that board. Closing the owner session ends its attempt as lost; database settlement failures remain retryable.

Coverage: `HackingMgrServiceTests` checks that the replacement session cannot operate the old board, and that closing the original session settles and releases it.

### P2: `EVENT_CORE_CONTENTS` (400) was missing

**Confirmed.** V24.01 dispatches event 400 to `OnCoreContentsRevealed` and reads its contents for the core tooltip (`eve/client/script/ui/hacking/hackingUISvc.py:197-200`; `eve/client/script/ui/hacking/hackingTile.py:312-319`). EveJS adds the core coordinate and `(typeID, quantity)` pairs after loot settlement and before the win event (`server/src/services/exploration/hackingMgrService.js:875-887, 908-922`).

The Host adds event 400 from the committed settlement receipt immediately before `EVENT_GAME_WON`, using the destroyed core coordinate and the V24 `contentsList` tuple shape.

Coverage: `ContainerSiteEndToEndTests` checks event order, coordinates, tuple wire shape, and settled loot contents.

### P2: admitted PvP queues could remain forever

**Confirmed.** The V24.01 queue UI stays active after join and clears only on match found or matchmaking failure (`pvpFilaments/client/activation_window/pvpFilamentController.py:194-198, 299-310`). EveJS ticks the queue runtime from the scene (`server/src/space/runtime/scene/tick.js:2424-2427`); when no match runtime is present, it removes the queue with `UNKNOWN_ERROR` and sends `OnMatchMakingFailure` (`server/src/services/activity/pvpFilamentQueueRuntime.js:155-178`). The runtime clock ticks at 100 ms (`server/src/space/runtimeClock.js:24`).

The Host now revalidates a successful admission on the shared `LocalTimers` at 100 ms. It removes the record and sends one failure notification if the event is inactive, admission no longer passes, participants changed, or the match runtime is absent. Leave and disconnect cancel the timer. This preserves the current Host limitation that no PvP match runtime exists while avoiding a permanently queued client.

Coverage: `ActivityTravelRpcServicesTests` checks the delayed revalidation, the failure packet, duplicate-notification protection, and leave/disconnect cleanup. The pinned catalog currently has no active event to drive this queue through the real graphical client, so client rendering of this notification was not exercised.

## Findings skipped

None. All five findings in the audit report were confirmed against the implementation and source evidence above.

## Verification

**Checked:** Release builds with `-warnaserror` succeeded with 0 warnings for `Eve.Hacking`, `Eve.Services.Space`, `Eve.Host`, `Eve.Services.Space.Tests`, `Eve.Host.Tests`, and `Eve.Hacking.Tests`. On `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`, the selected Space service slice passed 373 tests with 0 skipped, including the database-backed concurrency, rollback/retry, settlement, restart, migration, and inventory-transition cases. Host composition and wire tests passed 4 with 0 skipped. `Eve.Hacking.Tests` board/game slice passed 26 with 0 skipped. `git diff --check` passed.

The added same-character pending-start test and the module-path refusal test each failed against the pre-fix implementation, then passed in the selected green test runs. The focused audit regressions are all included in the 373 Space tests.

**Not checked:** The real graphical V24.01 client and a live active PvP schedule were not run. EveJS was source-traced; its package has no test script for these flows, so its end-to-end runtime was not executed. The codebase MCP was unavailable; source inspection used `rg` and line-numbered file reads. The full local test suites were not run, per the repository test guidance.
