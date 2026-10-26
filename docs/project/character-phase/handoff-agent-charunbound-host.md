# charUnboundMgr Host coverage handoff

## Status

The 25 requested RPC rows are 10 shared charUnboundMgr methods (the duplicate row pairs refer to old/new client generations). Concrete handlers and production composition were traced. Host wire tests now cover four of the ten methods: GetCohortsForUser, GetQAStarterSystemIDs and ToggleValidation in this increment, plus ValidateNameEx in `dotnet/tests/Eve.Host.Tests/CharUnboundMgrValidationHostTests.cs`, merged from origin/main.

The stateful methods GetNumCharacters, GetValidRandomName, PrepareCharacterForDelete, SelectCharacterID, UpdateCharacterBloodline and UpdateCharacterGender still need Host coverage. This batch is not complete.

Our focused increment passed locally and on Actions at implementation SHA `b4a43cfe5724`; Actions run `40094016933` passed. The follow-up handoff push merged origin/main at `8a4a51db754d`. Push-triggered `ci.yml` run `40094011027` for b4a43cfe was still in progress at the last status check; the status tool had not listed a `ci.yml` run for 8a4a51db.

## Client callers and reply consumers

CBM searches over the complete V24.01 `home-user-eve-logic` project found:

| Method | All direct caller sites found | Consumer behavior |
|---|---|---|
| GetCohortsForUser | `gatekeeper/client/service.py:37` | Passed to gatekeeper user initialization. |
| GetNumCharacters | `eve/client/script/ui/login/characterCreationContainer.py:907`; `characterCreationContainer_new.py:755` | Both compare the count with 1 before offering tutorial skip. |
| GetQAStarterSystemIDs | `eve/client/script/ui/services/ccSvc.py:102-112` | Sort IDs, prime location names, display choices, return the selected ID or None. |
| GetValidRandomName | `eve/client/script/ui/login/charcreation/steps/sections/chooseNameSection.py:96-103`; `charcreation_new/steps/sections/chooseNameSection.py:83-90` | Cache the returned string as valid, set the edit field and controller name callback. |
| PrepareCharacterForDelete | `eve/client/script/ui/login/charSelection/characterSelection.py:861-868` | Treat return as filetime, show queue confirmation when truthy, refresh slot. |
| SelectCharacterID | `eve/client/script/ui/services/ccSvc.py:186-188`; `eve/client/script/util/webtools.py:121`; normal selection flow `eve/client/script/ui/login/charSelection/characterSelection.py:705-725` | Run inside session-change flow; subsequent flow uses the bound session character. |
| ToggleValidation | `eve/client/script/ui/login/charcreation/steps/characterCustomization.py:308-314`; `charcreation_new/steps/characterCustomization.py:286-292` | Boolean reply sets the button label to Validation On/Off. |
| UpdateCharacterBloodline | `eve/client/script/ui/services/ccSvc.py:135-142` | Updates client session bloodline; optionally refreshes selection and paperdoll caches. |
| UpdateCharacterGender | `eve/client/script/ui/services/ccSvc.py:115-123` | Updates client session gender and invalidates paperdoll/public-info caches. |
| ValidateNameEx | `eve/client/script/ui/login/charcreation/steps/sections/chooseNameSection.py:197-203`; `charcreation_new/steps/sections/chooseNameSection.py:184-190` | Cache validation result by name. |

The old/new creation flows have distinct caller locations with shared contracts. The listed callsites are the direct calls found for these methods and their reply consumers.

## EveJS and eve.rs comparison

EveJS v0.12.9 handlers are in `server/src/services/character/charService.js`: GetNumCharacters 1369-1376; UpdateCharacterGender 1378-1409; UpdateCharacterBloodline 1411-1456; GetCohortsForUser 1458-1461; SelectCharacterID 1525-1848; PrepareCharacterForDelete 1850-1857; GetValidRandomName 1871-1875; GetQAStarterSystemIDs 1877-1881; ToggleValidation 1883-1902; ValidateNameEx 1904-1912. Production eve.rs composition is `dotnet/src/Eve.Host/Composition/CharacterServicesComposition.cs`; handlers are in `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs`. Tested replies match EveJS and the client: empty cohort list, catalog-derived sorted starter-system ID list, Boolean toggle transitions and numeric name-validation code.

`CharUnboundMgrReadWireTests.cs` registers the actual service through the Host test registry and sends calls through the socket dispatcher and marshal codec. It includes transport-added `machoVersion`, checks decoded replies and exercises both toggle states. It does not substitute a handler. The merged `CharUnboundMgrValidationHostTests.cs` similarly checks the numeric name-validation code through Host dispatch. No PostgreSQL database was used.

## Verification

- Local `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` — passed, 0 errors; one transient output-file retry warning came from overlapping local builds.
- Focused Host test `Char_unbound_mgr_stateless_reads_and_validation_toggle_are_registered_on_the_host` — passed locally, 1 test, 0 skipped.
- Selected Actions run `40094016933` for that test — passed, no failed tests or jobs.
- Push CI for `b4a43cfe` run `40094011027` remained in progress at last check. No PostgreSQL or graphical-client check was performed.

## Continuation

Complete the six remaining methods with Host tests for valid/error inputs, persistent effects and client follow-up state. The user explicitly disallowed PostgreSQL for this job; no database-backed path was exercised. Reconcile run `40094011027` and check for CI for the merged push `8a4a51db754d`.


## P0 client verification batch 6 (2026-10-26)

- Reviewed all 29 rows RPC-01131–RPC-01159 against the whole V24.01 client tree. The client callsites, reply consumers, and Host handlers are tabulated in `docs/project/service-audit/CLIENT_VERIFIED.md` under batch 6.
- EveJS 261018.1 handler locations were checked for all 12 distinct methods in `server/src/services/character/charService.js` (deletion delegates to `characterDeletionRuntime`). The inspected Host reply types agree with client consumption. No discrepancy was found in the owner-log report: it lists no charUnboundMgr calls or failures.
- MATCHES: GetCharacterLockType, GetCohortsForUser, GetQAStarterSystemIDs, ToggleValidation, ValidateNameEx. NO-TEST: GetCharacterInfo, GetNumCharacters, GetValidRandomName, PrepareCharacterForDelete, SelectCharacterID, UpdateCharacterBloodline, UpdateCharacterGender. Existing service-level tests do not substitute for Host wire coverage.
- No PostgreSQL database was used. Attempts to extend the existing stateless Host test with extra calls hit its per-session RPC budget (ErrorResponse); those assertions were removed, leaving that test source unchanged. No local test result is claimed for this batch.
- The audit updater help exposes only client-root, repo-root, and queue-dir options; it has no status_evidence option, so the CSV was left unchanged. The 03-allsvc-batch queue was not regenerated.
- Next: add focused real-Host tests for the seven methods still marked NO-TEST, then run the focused Host checks on Actions. This batch is not complete.


### Verification probe detail

A temporary Host test tried to exercise GetNumCharacters/GetCharacterInfo with null character-store dependencies. The Host returned ErrorResponse, and the temporary test was removed; no live store or database was substituted. This result does not establish a service mismatch because the probe lacked the character store needed for an authenticated Host session. The documented NO-TEST verdicts remain open.


## Actions update after push 6f2b9285

- Selected Host run `40260450720` passed for `Char_unbound_mgr_stateless_reads_and_validation_toggle_are_registered_on_the_host` (0 failed tests/jobs). This is existing stateless Host coverage; it does not close the seven NO-TEST methods.
- `python3 tools/dev/ci-status.py failures 6f2b9285` reported no `ci.yml` run for the pushed SHA at the follow-up check.
- Local temporary probes with null character-store dependencies returned ErrorResponse for account-backed reads; those test additions were removed. No database or graphical client was used.


## Latest push

- The handoff update was pushed with merge SHA `143facac` after merging the current `origin/main` changes.
- `python3 tools/dev/ci-status.py failures 143facac` reported no `ci.yml` run for that SHA.
- The selected Host run remains `40260450720` on `6f2b9285`, passed. Batch 6 remains incomplete until the seven NO-TEST methods have representative real-Host coverage.
