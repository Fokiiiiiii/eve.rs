# Alpha multi-login admission handoff

## Client and gameplay evidence

- V24.01 calls `connection.GetComputerHash()`, then `multiLoginBlocker.Login(computerhash)` only when the hash is not `None`; a false result opens `MultiLoginBlockedWindow` and returns false (`eve/client/script/ui/login/charSelection/characterSelection.py:620-640`). `CreateCharacter` returns before `CreateNewCharacter` on refusal (`:614-619`), and `EnterGameWithCharacter` returns before animation and `ConfirmWithCharID` (`:628-640`). Whole-repository Serena search found the service call and gate only in this file. Entry callers are auto-select (`:192`), the slot callback (`:486`), and `Confirm` (`:663-668`).
- EVE University says Alpha-to-Omega conversion is immediate: [Clone states, Omega clone](https://wiki.eveuniversity.org/Omega_clone) (line 109).
- EveJS 261028.1 resolves the conflicting session's clone grade inside `_findAlphaConflict` and returns the bool decision from `Handle_Login` (`server/src/services/login/multiLoginBlockerService.js:118-206`). Its enforcement remains environment-gated by default (`:54-62`); eve.rs's unconditional Alpha rule is the existing documented compatibility decision in `DEVIATIONS.md`.

## Implementation

- `MultiLoginBlockerService.Login` serializes conflict scan and registration with `_admissionGate` (`dotnet/src/Eve.Services.CharacterSelect/MultiLoginBlockerService.cs:27-49`). Records no longer keep a grade snapshot; conflicting sessions resolve their current grade through the injected callback while admission is serialized.
- Host passes the shared subscription clone-grade source (`dotnet/src/Eve.Host/Rpc/CoreRpc.cs:55-57,96`). `SubscriptionMgrService.CloneGradeOf` delegates to `OmegaLicenses.CloneGradeOf`, which derives the grade from the current stored expiry (`dotnet/src/Eve.Services.Core/SubscriptionMgrService.cs:56,72`); `AccountOmegaStore` reads the account subscription end through `IAccountSubscriptions`, and `PgAccountDirectory.SubscriptionEndsAt` queries `identity.accounts.subscription_end_at` on each call (`dotnet/src/Eve.Host/Hosting/AccountOmegaStore.cs:16-25`, `dotnet/src/Eve.Identity/PgAccountDirectory.cs:81-88`).
- Empty-hash bypass, case/space normalization, same-account exemption, Omega/Omega allowance, close cleanup, and the `PyBool` reply are preserved.

## Verification

- PASS: CharacterSelect service build and test-project build, each with zero warnings/errors.
- PASS: `MultiLoginBlockerTests`, 9/9.
- PASS: Host route registration test, 1/1.
- PASS: full `HostServerTests` class against `eve_p9` after copying static SDE schemas with `tools/dev/copy-sde-database.py`, 411/411 after merging current main, zero skipped.
- PASS: `PgAccountTests` through `tools/dev/db-lock.sh` on `eve_p9`, 19/19, zero skipped.
- PASS: `OmegaStateTests`, 8/8.
- PASS after merging current main: service, CharacterSelect tests, Host tests, Identity tests and Core tests built with zero warnings/errors; all focused tests above were rerun, including the 411 HostServer tests.
- The native V24 graphical client was not launched during this verification.

## Follow-up

Implementation commit `be8ba999` and merge commit `018edd0b` were pushed to `main` as `7351c60b`. The newest completed main CI before push was run 1554 and passed. Post-merge builds and tests are green. Selected agent-test run 1353 (`42186345267`) passed in 10.8 minutes with no failed tests. Push-triggered CI run 1555 (`42186290792`) passed all jobs, including headless end-to-end (21.4 minutes); `ci-status.py failures 7351c60b` found no failed tests or jobs.
