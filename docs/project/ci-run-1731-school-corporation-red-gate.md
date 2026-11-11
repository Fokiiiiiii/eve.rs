# Main CI 1731: school corporation references

Investigation and fix for the red main run after `97c76cf39` refreshed the character information references. This note records the source decision and the scope of the correction; it does not claim that CI has passed.

## Source decision

Keep the school corporation mapping introduced by `97c76cf39`. Restoring the previous table would make character creation disagree with the current EveJS school data and the pinned SDE.

V24.01 character creation selects and sends a `schoolID`: `characterCreationContainer_new.py:942-949,1298-1301` and `ccSvc.py:92-97`. The whole client uses `characterdata/schools.py` to expose `school.raceID` and `school.corporationID` (`:37-46,62-71`) and to fall back to the school corporation's station (`:115-123`). The client source tree does not include the referenced `schools.fsdbinary`; the actual V24 binary values could not be read directly in this checkout.

The current EveJS table at `/home/user/evejs-261108.1/tools/DatabaseCreator/staticTables/characterCreationSchools/data.json:7-87` gives these school-to-corporation values. The identifiers and station owners agree with NPC corporation records in the pinned SDE build 3396210 (`.cache/sde/eve-online-static-data-3396210-jsonl/npcCorporations.jsonl`):

| School | Race | Corporation | SDE corporation | SDE station |
|---:|---:|---:|---|---:|
| 31 | 4 | 1000165 | Hedion University | 60014599 |
| 32 | 4 | 1000077 | Royal Amarr Institute | 60007702 |
| 33 | 2 | 1000171 | Republic University | 60014779 |
| 34 | 2 | 1000172 | Pator Tech School | 60014809 |
| 35 | 1 | 1000045 | Science and Trade Institute | 60004450 |
| 36 | 1 | 1000044 | School of Applied Knowledge | 60004420 |
| 37 | 8 | 1000115 | University of Caille | 60011566 |
| 38 | 8 | 1000169 | Center for Advanced Studies | 60014719 |

The prior `CreationCatalog` table mapped school 35 to Deep Core Mining Inc. (1000006, station 60000610) and school 31 to Imperial Academy (1000166, station 60014629). Those corporations exist in the SDE, but they are not the corporations assigned to schools 35 and 31 in the current EveJS school table. Current `CreationCatalog.cs:26-32` and its exact-map catalog test agree with the EveJS values. During a local current-EveJS run, the created school-35 character was persisted with corporation 1000045. The recorder could not continue the undock scenario: current character selection put the initial AIR Civilian Astero in space and left the station ship hangar empty, so `record_undock` stopped before it could issue `Undock`. This runtime is supporting evidence for the corporation mapping, not a fresh undock trace.

## Changes

- Updated only corporation fields on the recorded character's own ships, identified by each fixture's `shipID` and creation `schoolID`. This fixes 93 stale slim values in 20 scenario references and 91 stale corporation IDs in encoded balls. The first correction found buffers where the pilot ship was the first ball; Rust `payloads_match_reference` then exposed more stale fields in packed buffers that include other balls before the pilot's ship. Race 1 school 35 now carries corporation 1000045; race 4 school 31 carries corporation 1000165. Unrelated NPC values, the two recorded `corpID: 0` slim entries, and one zero corporation field in an encoded ball were retained.
- Updated the CharacterInfo test rig's school-35 character default, school-station resolver, and expected corporation fields to 1000045. Explicit tests for arbitrary corporations such as Deep Core Mining remain unchanged.
- Updated the seeded structure, drill, moon-resource owner and expected extraction owner in `test_structures.py` to match the actual race-1 school corporation. The structure access assertions remain intact; the test pilot and refinery now belong to the same corporation, as required for the docking flow under test.

## Verification

Local verification used `eve_p3` with the pinned SDE build 3396210 loaded. After the Rust regression exposed the stale corporation field inside encoded balls, the affected checks below were rerun against the corrected fixtures.

- Release builds completed with 0 warnings and 0 errors for `Eve.Services.CharacterInfo.Tests`, `Eve.Characters.Tests`, and `Eve.Services.CharacterSelect.Tests`.
- Full focused .NET test projects passed: CharacterInfo 83/83, Characters 129/129, CharacterSelect 69/69; no failures or skips.
- After the packed-buffer correction, Rust checks passed: `cargo fmt --all -- --check`, `cargo check --workspace`, and `cargo clippy --workspace --all-targets -- -D warnings`; `cargo test -p eve-space --test payloads_match_reference` passed (2/2) and `cargo test -p eve-space --test others_match_reference` passed (5/5).
- The requested live headless files (`test_login_to_character.py`, `test_undock.py`, `test_movement.py`, `test_structures.py`) were rerun after the encoded-ball update and exited 0 with no failures. Three login cases were skipped by the test's skill-speed guard (two cases require the recorded 1.0 speed and one requires 60000.0); those cases are not counted as verified passes.
- A broader live run of the 20 scenario modules whose recorded own-ship slims were audited exited nonzero before the encoded-ball correction. Failures observed included aggression timer state, ammo/reload module attributes and unload replies, and target-lock notifications. That broader run was not repeated after the encoded-ball correction, so those modules remain unverified.
- Additional attempts to run the skill-speed guarded cases with a non-default multiplier got past the speed guard but failed their skill-point/notification comparisons. A full login-file run at multiplier 2 also failed expected attribute comparisons. No skill implementation or assertion was changed; those cases remain unverified here.
- CI run 1734 found a stale corporation ID in `others.json`; CI run 1735 then found additional stale IDs in packed `AddBalls2` data in `undock.json`. Both roots were stale recorded binary fields, not server output. The focused Rust payload and observer tests pass after correcting all identified packed-buffer entries. The CI run for the corrected commit is the remaining integration check.
- The current EveJS recorder could not produce the same undock trace because its startup state had the initial ship in space and no ship in the station hangar. The recorded corpIDs were instead audited against each fixture's character creation schoolID, the current EveJS school table, and the pinned SDE.

The main CI run for this fix remains the final integration check; its result should be appended after the push.

The local V24 source callsites and EveJS school table were inspected; the packaged V24 `schools.fsdbinary` itself and the graphical client were not tested. The current EveJS runtime's undock trace could not be recorded for the startup reason above.
