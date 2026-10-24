# charUnboundMgr Host coverage handoff

## Status

The 25 requested RPC rows are 10 shared charUnboundMgr methods (the duplicate row pairs refer to old/new client generations). The existing service methods and production composition were traced. This increment adds a real Host registry/socket/dispatcher/wire regression for three database-independent methods: GetCohortsForUser, GetQAStarterSystemIDs and ToggleValidation. The remaining stateful methods still need Host coverage; this batch is not complete.

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

The client makes no additional uses of these direct replies beyond the consumers listed above. The method pairs for old/new character creation are distinct callsites with the same RPC contract.

## EveJS and eve.rs comparison

EveJS v0.12.9 handlers are in `server/src/services/character/charService.js`: GetNumCharacters 1369-1376; UpdateCharacterGender 1378-1409; UpdateCharacterBloodline 1411-1456; GetCohortsForUser 1458-1461; SelectCharacterID 1525-1848; PrepareCharacterForDelete 1850-1857; GetValidRandomName 1871-1875; GetQAStarterSystemIDs 1877-1881; ToggleValidation 1883-1902; ValidateNameEx 1904-1912. Production eve.rs composition is `dotnet/src/Eve.Host/Composition/CharacterServicesComposition.cs`; method implementations are `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs`. The tested replies match EveJS and the client: empty cohort list, sorted starter-system ID list from the creation catalog, and Boolean validation-toggle transitions.

The new test `dotnet/tests/Eve.Host.Tests/CharUnboundMgrReadWireTests.cs` registers the actual service through the test Host registry and sends calls through the socket dispatcher and marshal codec. It includes transport-added `machoVersion`, checks each decoded reply, and exercises both toggle states. It does not substitute a handler. The other seven methods access persisted character state through `PgCharacterStore` and related stores. No PostgreSQL database was used as required; their success, rejection, persistence and follow-up transitions still need focused database-backed Host scenarios.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false` — passed with 0 errors and one transient output-file retry warning from overlapping local builds.
- Focused test `FullyQualifiedName~Char_unbound_mgr_stateless_reads_and_validation_toggle_are_registered_on_the_host` — passed, 1 test, 0 skipped.
- No PostgreSQL database or graphical client used.

## Continuation

Commit and push this increment, then record the SHA and Actions result here. Before marking RPC-01139–RPC-01163 covered, add database-backed Host tests for GetNumCharacters, GetValidRandomName, PrepareCharacterForDelete, SelectCharacterID, UpdateCharacterBloodline, UpdateCharacterGender and ValidateNameEx, including their persisted effects and client follow-up state.
