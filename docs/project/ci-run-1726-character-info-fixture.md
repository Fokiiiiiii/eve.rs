# Main CI 1726: character public-info fixture refresh

Recorded 2026-11-11 while investigating the main red gate. This records the diagnosis and local verification; it does not claim that GitHub CI is green.

## Diagnosis

Main CI #1725 passed at `feaf63ae`. Main CI #1726 failed at `583bd6dc` in the fast live test `test_a_new_character_is_described_as_the_reference_describes_it` for all four race cases. The failure log reported the old `GetPublicInfo` keys (`shortName`, `bounty`, `factionID`, `title`, `militiaFactionID`, `startDateTime`, `securityRating`, `empireID`, `solarSystemID`, `medal1GraphicID`, `stationID`, and `securityStatus`) as missing, the new keys (`careerID`, `careerSpecialityID`, `warFactionID`, `corporationDateTime`, and `age`) as unexpected, `gender` as `bool` instead of `int`, and `GetPublicInfo3` as an ObjectEx CRowset instead of the old list of KeyVals. Main CI #1730 at `bc2767ef` still reports the same failure. No fix had reached `origin/main` at the time of this check.

The fixture was stale. Commit `be1d401d9` changed `CharMgrService` to emit the current 17-field `util.KeyVal` for `GetPublicInfo` and the seven-column `CRowset` for `GetPublicInfo3`. Commit `7d177ffc0` corrected the cross-language marshal encodings used by those values. Both commits are in the ancestry of the failing `583bd6dc`; the intervening `f2eff5da4` merge brought them onto the cx3 line. The failing comparison still used `character_info.json`, recorded on 2026-09-29 before those wire changes. The latest EveJS behavior and the V24 client consumers support the new shapes; changing server behavior back or relaxing the comparison would be incorrect.

Evidence:

- Failure logs: [CI #1725](https://github.com/Fokiiiiiii/eve.rs/actions/runs/43020117608), [CI #1726](https://github.com/Fokiiiiiii/eve.rs/actions/runs/43024359089), [CI #1730](https://github.com/Fokiiiiiii/eve.rs/actions/runs/43045910771).
- `be1d401d9` changes `dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs`: `PublicInfo` emits the 17 fields, while `PublicInfo3Async` constructs a `blue.DBRowDescriptor` and a one-row `carbon.common.script.sys.crowset.CRowset`.
- EveJS snapshot `/home/user/evejs-261108.1`, `server/src/services/character/charMgrService.js:432-463` builds `GetPublicInfo3`; lines 465-496 build `GetPublicInfo`.
- The complete `/home/user/eve-logic` tree was searched for both methods. V24 reads `GetPublicInfo3` row attributes in `client/script/ui/shared/info/characterInfoWindow.py:64-75,194-198` and `client/script/ui/shared/info/infoContentBase.py:410-421,488`. `GetPublicInfo` fields are read by `client/script/ui/neocom/charsheet/characterOverviewPanel/characterOverviewPanel.py:47,90-95,141-143`, `client/script/ui/shared/preview.py:852-878`, and `client/script/ui/shared/info/panelStandings.py:121-127`; additional call/cache sites include `ccSvc.py:180`, `crimewatchSvc.py:180`, `dockedUI/inControlCont.py:41`, and `agents/agents.py:282,520`.

## School corporation data correction

Re-recording the character flow also exposed incorrect school-to-corporation IDs in `CreationCatalog`. V24 creation data reads `school.corporationID` (`/home/user/eve-logic/characterdata/schools.py:41-46,70-75`). The current EveJS school table is `/home/user/evejs-261108.1/tools/DatabaseCreator/staticTables/characterCreationSchools/data.json`. Its IDs, checked against the pinned official SDE build 3396210 in local `eve_p4`, are:

| School ID | Race ID | Corporation ID | SDE corporation | Faction | School station |
|---:|---:|---:|---|---:|---:|
| 31 | 4 | 1000165 | Hedion University | 500003 | 60014599 |
| 32 | 4 | 1000077 | Royal Amarr Institute | 500003 | 60007702 |
| 33 | 2 | 1000171 | Republic University | 500002 | 60014779 |
| 34 | 2 | 1000172 | Pator Tech School | 500002 | 60014809 |
| 35 | 1 | 1000045 | Science and Trade Institute | 500001 | 60004450 |
| 36 | 1 | 1000044 | School of Applied Knowledge | 500001 | 60004420 |
| 37 | 8 | 1000115 | University of Caille | 500004 | 60011566 |
| 38 | 8 | 1000169 | Center for Advanced Studies | 500004 | 60014719 |

The catalog and creation fixtures now use this mapping. The login, starter and character-info fixture values that depend on the selected school were updated with it. The existing catalog test now asserts all eight exact mappings.

## Fixture scope and remaining differences

`oracle/fixtures/scenarios/character_info.json` now contains current EveJS `GetPublicInfo` and `GetPublicInfo3` responses for all four races, while retaining the rest of the 2026-09-29 calls. `with_description` now handles the CRowset row shape, and `corporationDateTime` is treated as volatile. The GetGuests rows retained from the older scenario have their corporation IDs updated to the selected schools.

A diagnostic full re-record was also compared. It showed unrelated station-state and ContactList representation differences, so those snapshots were not replaced as part of this red-gate fix. In particular, the isolated current EveJS recording had no self row in `GetGuests`, while the retained scenario and eve.rs live result did. The reason is not established; session population and the intended current EveJS result remain unchecked. The ContactList CRowset versus older Rowset mismatch also remains outside this fix. These are known gaps, not passing parity checks.

## Verification

The local checks used a clean worktree based on `origin/main` at `bc2767ef4`, with the fix files copied in, and the required database `eve_p4` loaded with SDE build 3396210.

- Release `dotnet build` with warnings treated as errors: `Eve.Characters.Tests`, `Eve.Services.CharacterSelect.Tests`, and `Eve.Host.Tests` each built with 0 warnings and 0 errors.
- Database-backed tests against `eve_p4`: `CatalogTests` and `CreationTests`, 40 passed; after adding the exact eight-school mapping assertion, `CatalogTests`, 19 passed. Character-select filtered classes (`CharUnboundMgrTests`, `CharacterLogoffTests`, `PopulationCapTests`), 56 passed. Host focused classes/cases for public info, character manager, and public record membership, 11 passed. Each reported 0 skipped.
- Focused live headless test `test_a_new_character_is_described_as_the_reference_describes_it`: all four race cases passed (4 passed, 0 skipped) through `tools/dev/run-eve-live.sh`; the server stack built and ran locally.
- JSON parsing for the edited scenario fixtures and `git diff --check` passed.

This is headless verification, not a graphical V24 client test. The full live suite was not run. GitHub CI has not tested this fix because it is not on `main`; main CI #1730 remains red. Agent `cx4` is not authorized by the job rule to push a red-gate fix directly, so this worktree has not been pushed.
