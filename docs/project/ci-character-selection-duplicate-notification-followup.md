# Character selection notification follow-up (2026-11-09)

## Finding

Main CI run `42630209868` failed in the live headless selection tests because the first notification had changed to `OnSubscriptionTimeUpdateServer`. Main run `42636881594` later passed after the tests were changed to expect both that notification and a server-sent `OnCharacterSelected`. Rechecking the complete client and EveJS selection paths shows that the latter expectation preserved a duplicate server event.

At the time of this follow-up, `origin/main` was `7f9b36e9d`. Its intervening commit `b73b738bb` changed the recorded-walk assertion to expect the subscription notice followed by `OnCharacterSelected`; it did not change the selection implementation. No later main commit had removed the server event, and no cx14 agent was active in the current agent session.

## Evidence

- V24.01 calls `SelectCharacterID` inside `PerformSessionChange` (`/home/user/eve-logic/eve/client/script/ui/login/charSelection/characterSelection.py:705-714`). When the resulting session change contains `charid`, `GameUI.OnSessionChanged` calls its local `OnCharacterSelected` (`/home/user/eve-logic/eve/client/script/parklife/gameui.py:433-455`), and that method scatters the local event (`gameui.py:504-505`).
- The V24.01 PLEX panel subscribes to `OnSubscriptionTimeUpdateServer` and refreshes Omega time in response (`/home/user/eve-logic/eve/client/script/ui/shared/neocom/charsheet/plexPanel.py:22-26,160-163`).
- EveJS v0.12.9 sends `OnSubscriptionTimeUpdateServer` at the start of `SelectCharacterID`, before the session change (`/home/user/evejs-261028.1/server/src/services/character/charService.js:1543-1545`). Its `OnCharacterSelected` support is explicitly opt-in (`characterState.js:4235-4236,4262-4263`); the selection flow flushes its plan without setting that option (`charService.js:1817-1820`). A source search found no EveJS server caller enabling `sendOnCharacterSelected`.
- Before this correction, eve.rs also sent `OnCharacterSelected` from `CharUnboundMgrService.SelectCharacterID` before applying the `charid` session change (`dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs:426` on main). That duplicates the event the V24 client scatters when it receives the session change.

The evidence supports sending the subscription update first and then the normal session-change packet. The V24 client generates its local `OnCharacterSelected` event from that session change; the server should not send a second copy.

## Correction

- Removed the explicit server `OnCharacterSelected` send during character selection.
- Updated the Host and character-selection tests to pin the remaining wire packet order and counts.
- Updated live headless selection checks to require `OnSubscriptionTimeUpdateServer` first and require that the server notification stream contains no `OnCharacterSelected`. Recorded session and skill packets are still compared against their fixtures.

## Verification

- `Eve.Services.CharacterSelect` and `Eve.Services.CharacterSelect.Tests` Release builds passed with warnings treated as errors.
- Focused database-backed character-selection tests on `eve_p6`: 56 passed, 0 failed, 0 skipped.
- Focused Host test slice: 63 passed, 0 failed, 1 skipped. The skipped abyssal simulation case was subsequently run with the built `eve-sim` and pinned SDE and passed separately (1 passed, 0 skipped).
- The three targeted live selection checks, including the recorded walk, all four race cases, and named-argument character entry: 6 passed, 0 failed, 0 skipped.
- The extended queue-finishes-while-away skill-speed check is not a pass. It skipped at the configured speed because the measured speed was `57000`, and a forced-speed attempt reached later skill-state/attribute differences. Its notification assertion was updated in this follow-up and still needs a clean focused rerun under the intended nightly setup.
- The Host test project built with the pre-existing untracked `dotnet/tests/Eve.Host.Tests/InventoryAuditHostWireTests.cs` excluded. Building with that local file included fails at line 194 because it passes `PgModuleStateStore` where `IReadOnlyList<ICharacterEntryHook>` is required. That unrelated worktree file was left untouched.

The headless tests exercise the registered eve.rs Host and packet flow. The graphical V24.01 client was not run. Main CI for this correction is pending.
