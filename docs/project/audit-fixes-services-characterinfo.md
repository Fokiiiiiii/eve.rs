# CharacterInfo services audit fixes

`/home/user/reports/90-audit-services-characterinfo-r15.md` was checked against the Host and service implementations, V24.01 client source in `/home/user/eve-logic`, and EveJS 261108.1 in `/home/user/evejs-261108.1` before changes.

## Findings

### P2 — Abrupt disconnect did not persist character logoff time

**Confirmed.** Before the fix, `SessionActor` closed the session before its `SessionClosedAsync` callback (`dotnet/src/Eve.Host/HostServer.cs`), while the Host's disconnect cleanup only removed presence. Only the explicit `UserLogOffCharacter` path persisted `LastLogoffAt`. EveJS persists character logoff state from its socket-disconnect path (`server/src/network/tcp/index.js:257-264`, `server/src/services/_shared/sessionDisconnect.js:312-355`) before its offline transition (`server/src/services/online/onlineStatusRuntime.js:140-151`). V24.01 corporation member tracking consumes `lastOnline` at `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_tracking.py:127-139`.

Host now persists `LastLogoffAt` before `ClientSession.Close()` can announce offline presence, and only when the closing session is still the character's current session. This leaves a prior explicit logoff timestamp intact instead of writing a second, later time.

### P3 — AssignLabels created unknown personal contacts

**Confirmed.** The old `CharMgrService.AssignLabels` created a `CharMgrContact` when the ID was absent. EveJS `server/src/services/character/charMgrService.js:1035-1055` updates only a contact already in the address book. V24.01 `eve/client/script/ui/shared/neocom/addressBook/addressbookService.py:1096-1116` calls `AssignLabels`, then opens `AddToAddressBook` for a single unknown ID; that flow sends `AddContact` only after confirmation (`:705-816`). Cancelling therefore must leave the contact absent.

`AssignLabels` now updates only existing contacts. After confirmation, the client creates the contact through `AddContact` and re-sends its label assignment. Service and Host tests cover both the unknown-ID case and the confirmed-add case, including Host restart persistence.

## Verification

The DB-backed Host tests use `eve_p5` with the pinned SDE. Both new E2E regressions failed before their respective fixes and passed afterward. The P2 E2E runs the real Host TCP front, checks the offline notification and persisted timestamp, checks corporation `lastOnline`, and verifies explicit logoff is not overwritten during close. The P3 Host regression checks the `GetContactList` wire-shaped result before and after `AddContact`, and after restart.

No reported finding was false, so none were skipped. Graphical-client verification was not performed. CBM/Serena MCP-based source navigation was unavailable; source paths and line evidence were checked directly.

## Test and build results

- Before P2 fix: the new DB-backed Host disconnect E2E failed because the old three-day `LastLogoffAt` remained visible after the offline notification.
- After P2 fix: `CharacterDisconnectHostTests` passed (1/1, 0 skipped).
- Before P3 fix: the new DB-backed Host test failed because `AssignLabels` inserted the missing contact into `GetContactList.addresses`.
- After P3 fix: the two focused Host label tests passed (2/2, 0 skipped); the `CharMgrTests` class passed (33/33, 0 skipped).
- `HostServerTests` class passed (440/440, 0 skipped).
- `CharacterHostTests` class passed (69 passed, 1 skipped, 0 failed). The one skip is `Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry`, which requires `EVE_ABYSSAL_SIM_BINARY` and `EVE_ABYSSAL_SIM_SDE`; those optional simulation inputs were not configured. It is not counted as a pass.
- Release builds with warnings treated as errors succeeded with 0 warnings and 0 errors for `Eve.Services.CharacterInfo`, `Eve.Host`, `Eve.Services.CharacterInfo.Tests`, and `Eve.Host.Tests`.
- The code changes were not tested with a graphical client. CI for current `main` was green at the latest observed run before push; commit-specific CI remains to be watched after push.
