# Character skills, clones, implants, and insurance deep-fix handoff

Date: 2026-11-01
Worktree: `/home/user/wt-cx3`

## Result

Fixed a player-facing clone-jump defect: clones installed in an accessible Upwell structure are now valid jump targets. The server resolves the structure's solar-system metadata, settles the jump and implant transfer in PostgreSQL, updates the character's persisted location, moves the live session to the structure, and invalidates clone caches after the committed state change. Structure targets use the confirmation error handled by the V24.01 client. The current location now survives Host restart and character reselection: `characters.structure_id` is persisted, and selection restores `structureid`, `locationid`, and solar-system session fields while leaving station fields `None`.

No client, EveJS, or EVE University deviation was required.

## Evidence and decision

- The V24.01 character sheet sends `CloneJump(destination, cloneID)`: `eve/client/script/ui/shared/neocom/charsheet/jumpClonesPanel.py:604-606`. Its clone service gets the jump service moniker and clone state from session location and consumes `timeLastJump`: `eve/client/script/ui/services/clonejumpsvc.py:64-78`.
- The client performs `CloneJump(..., cost, False)`; on `JumpCheckIntoStructure`, it prompts and retries with confirmation `True`: `eve/client/script/ui/services/clonejumpsvc.py:192-235`.
- After the state change the client clears the main clone/implant caches through `OnJumpCloneCacheInvalidated` (`clonejumpsvc.py:294-298`); ship and station cache notifications are handled at `:301-310`. The Host retains its existing global and location cache invalidation order after the store transaction commits.
- EVE University says clones may be installed and activated in player-owned structures, and structure clone bays may be access-controlled: [Jump clones](https://wiki.eveuniversity.org/Jump_clones), “Clone bay”, “Clone installation”, and “Clone activation” (sections at lines 120-166 in the retrieved page).
- EveJS 261028.1 validates structure service access, presents the same structure confirmation error, and transfers the live session to a structure: `/home/user/evejs-261028.1/src/server/src/services/station/jumpCloneRuntime.js:1451-1486,1827-1922,1940-2038,2425-2501`.
- Before this fix `HostJumpCloneStore.JumpAsync` returned `CLONE_NOT_FOUND` for every target with `LocationKind != "station"`, although the install flow already persisted structure clones. The repaired store resolves a live structure and its solar system before committing.
- The character store now maps and writes the nullable structure location (`dotnet/src/Eve.Characters/Characters.cs:41,70,106,401-411`); migration `dotnet/src/Eve.Characters/Migrations/0016_character_structure_location.sql:1` adds the column. Character selection restores the EVE session location shape at `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs:362-370`.
- Regression coverage asserts persisted `StructureId` and then restarts Host and calls registered `SelectCharacterID`: `dotnet/tests/Eve.Host.Tests/JumpCloneSettlementHostWireTests.cs:228-265`. This confirms the state survives restart, not just a live-session update.

## Change and regression coverage

Changed:
- `dotnet/src/Eve.Services.Core/JumpCloneService.cs`
- `dotnet/src/Eve.Host/Composition/HostJumpCloneStore.cs`
- `dotnet/src/Eve.Host/Composition/CharacterComposition.cs`
- `dotnet/src/Eve.Characters/Characters.cs`
- `dotnet/src/Eve.Characters/Migrations/0016_character_structure_location.sql`
- `dotnet/src/Eve.Services.CharacterSelect/CharUnboundMgrService.cs`
- `dotnet/tests/Eve.Host.Tests/JumpCloneSettlementHostWireTests.cs`
- `dotnet/tests/Eve.Characters.Tests/StoreTests.cs`

The registered Host regression exercises the client confirmation error and retry, jumping from a station to a structure clone, the ISK fee, the active session's structure fields, the replacement clone stored at the origin, persisted location, and Host restart plus character reselection. The new assertion failed when the former structure-rejection behavior was temporarily restored, then passed after that rejection was removed.

Checks run on the local PostgreSQL lane `eve_p3`:
- Host.Tests build: passed, 0 warnings and 0 errors.
- Focused Host clone settlement/read tests: 1 passed, 0 failed, 0 skipped.
- Host clone settlement, character-selection and character-host tests: 23 passed, 0 failed, 0 skipped.
- Eve.Services.Core clone service/ship-install tests: 7 passed, 0 failed, 0 skipped.
- Eve.Characters JumpCloneStoreTests: 5 passed, 0 failed, 0 skipped.
- Migration upgrade plus JumpCloneStoreTests: 6 passed, 0 failed, 0 skipped. The first push CI run found that `StoreTests.Home_station_cooldown_migration_preserves_existing_rows` expected the pre-0016 migration list; updated the expected list to include `0016_character_structure_location` and reproduced the fix locally on `eve_p3`.
- Eve.Characters.Tests build after the migration assertion fix: passed, 0 warnings and 0 errors.
- `git diff --check`: passed.

The wrong Host test filter attempt and the deliberate before-fix failure are not counted as passes. Push CI run 1375 exposed the migration expectation mismatch; commit `43ece20c` added the missing migration to the expected list. The exact migration test plus JumpCloneStoreTests passed locally (6 passed, 0 skipped), push CI run 1377 passed with no failed jobs, and the selected Host/Characters Agent Test run passed. No graphical-client session was performed.

## Other area records and remaining verification

- Skills: `docs/project/character-phase/handoff-RPC-skill-host.md` records client callers/consumers, error shapes, mutation path, and prior regression checks. The callsite for immediate training is `eve/client/script/ui/services/skillsvc.py:771-773`; queue reads and updates are in `eve/client/script/ui/services/skillQueueSvc.py:75-117,188-255,600-604,748-767`.
- Implants and clone lifecycle: `docs/project/character-phase/handoff-CX6-clones-implants-insurance-repair.md` records client consumers and prior clone, implant, pod-death, and cache checks.
- Insurance: `docs/project/service-audit/DEEPCHECK_insurance.md` records the whole-client RPC caller/consumer matrix, wire contracts, errors, persistence and prior restart checks. Its transaction lock is `dotnet/src/Eve.Host/Persistence/Insurance/PersistenceInsuranceWriteModel.cs:296-306`; a concurrent same-ship purchase test was not run in this pass.

The skills, implant, and insurance implementations were not changed in this increment. Their prior evidence is documented above; this pass did not rerun their test classes, and no graphical-client validation was performed.
