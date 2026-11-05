# Character expert-system deletion purge (CX8)

## Result

`PgSkillStore.ClearCharacterAsync` now deletes rows from `character_expert_systems` in the existing caller-owned transaction. `HostCharacterDeletionPurge.PurgeAsync` already passes that same connection and transaction to the skill store; no new transaction or migration was added.

The registered-Host deletion test seeds future-expiry grants for the deleted character and a retained character, then verifies only the deleted character's row disappears. A separate Host test triggers an error while deleting the later `character_neural_remaps` table and confirms PostgreSQL rollback restores the expert-system row. The existing expiry test remains unchanged and passes.

## Evidence

- PostgreSQL table definition: `dotnet/src/Eve.Skills/PgSkillStore.cs:111-122`; the table has no character foreign key. Cleanup is in `ClearCharacterAsync` at lines 336-351.
- Host purge transaction: `dotnet/src/Eve.Host/Composition/CharacterDeletionPurge.cs:47,56`.
- Success and retained-character coverage: `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:941-1145`.
- Rollback test: `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:1150-1205`.
- Existing lapse behavior: `dotnet/tests/Eve.Host.Tests/CharacterHostTests.cs:829-858`.
- V24.01 client: `eve/client/script/ui/login/charSelection/characterSelection.py:822-870` checks the deletion timer, asks for confirmation, calls `remoteCharacterSvc.DeleteCharacter` at line 847, then handles an error or refreshes selection. The prepare reply supplies the countdown at lines 863-865; cancel is called at line 870. `characterSlots.py:536-545,679-688` consumes the same timer for the slot UI. The whole-client search found no other caller of this deletion RPC; `characterSettingsSvc.py:55` calls the separate `DeleteCharacterSetting` method.
- Expert-system response contract: `ExpertSystemMgrService.GetMyExpertSystems` reads through `PgExpertSystemStore.ForCharacterAsync` and filters expired grants, returning an empty dict without a session character or a dict keyed by type ID with installed/expiry FILETIMEs (`dotnet/src/Eve.Services.Skills/ExpertSystemMgrService.cs:30-44`, `dotnet/src/Eve.Skills/ExpertSystems.cs:145-146,192-199`). The V24 client caches this map per character and refreshes it on `OnExpertSystemsUpdated` (`expertSystems/client/service.py:26-48`). Its consumers use active IDs or expiry values: skill unlock and ship-tree checks (`expertSystems/client/util.py:21-26,64-68`, `eve/client/script/ui/shared/shipTree/shipTreeShipGroup.py:90-94`), expert-system indicators and activation choices (`expertSystems/client/ui/icon/associated.py:54-58`, `expertSystems/client/insider.py:14-20`), and character-sheet/fanfare state (`expertSystems/client/ui/character_sheet/home/controller.py:122-123`, `expertSystems/client/ui/character_sheet/state_icon.py:32-33`, `expertSystems/client/ui/fanfare.py:75-80`). `skills/client/skillController.py:57-59` forwards the same service call. This change leaves the reply and notification contract untouched.
- EveJS 261028.1: `src/server/src/services/character/characterDeletionRuntime.js:619-670` clears the in-memory expert-system state and the character-keyed persisted table path during `purgePrivateCharacterState`.

## Verification after merge

- Merged `origin/main` at `9d0c28b9602c`; the deletion implementation is in task commit `b020138c`.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/src/Eve.Skills/Eve.Skills.csproj -m:1 -p:UseSharedCompilation=false`: passed, 0 warnings and errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -m:1 -p:UseSharedCompilation=false`: passed, 0 warnings and errors.
- On `eve_p8`, using `tools/dev/db-lock.sh`, `CharacterHostTests` passed 33/33 and `SpaceServiceTests` passed 160/160; both had 0 failures and 0 skips. `CharacterHostTests` includes the deletion, rollback and existing lapse tests. The Space test project built during its test run.
- Latest completed main CI before this task push: run `1521` for `2417e284c79d`, all jobs green. `origin/main` later advanced through documentation-only commits `c8274d9d` and `9d0c28b9`.
- The graphical V24.01 client was not exercised.

## Current worktree

The implementation, retention, rollback and expiry evidence above is current after the merge and local checks. Two unrelated existing worktree edits remain untouched: `docs/project/service-audit/DEEPCHECK_calendar.md` and `tools/headless-client/tests/test_structures.py`.
