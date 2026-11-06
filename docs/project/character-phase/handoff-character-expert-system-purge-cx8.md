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

## CX8 biomass settlement follow-up (2026-11-06)

### Result

Character deletion now settles corporation assets and ISK in the existing purge transaction. A player-corporation character transfers wallet balance to division 1 with journal reference type 9 (Inheritance), moves owned asset trees to hangar division 1 when an office exists, and destroys assets when no office exists. NPC-corporation balance and assets are destroyed. Player-corporation members receive durable mail through the existing mail owner and durable new-mail notifications through the existing notification path.

The original corporation ID is captured in the character tombstone migration before the corporation changes to Doomheim. A rollback leaves the original assets, wallet balance and corporation ID intact for a later retry; a failure after commit can retry through the same persisted operation and event markers without duplicate wallet transfers, journal rows, mail or notifications. The CEO guard, queue timer, DeleteCharacter/Prepare/Cancel reply shapes, and selection refresh remain intact.

### Evidence

- EVE University [Accounts](https://wiki.eveuniversity.org/Accounts) specifies the player-corporation wallet, assets, office and member-mail outcomes, and NPC-corporation destruction. eve.rs follows that wiki. EveJS 261028.1 `server/src/services/character/characterDeletionRuntime.js:716-799` is the executable comparison; it omits these settlement rules.
- V24.01 `eve/client/script/ui/login/charSelection/characterSelection.py:812-859` checks the biomass deadline, calls `remoteCharacterSvc.DeleteCharacter`, displays an error or refreshes character selection. `:861-868` calls Prepare and uses its returned deadline; `:870-873` calls Cancel. Whole-client search found no other deletion RPC caller.
- Mail consumers are `eve/client/script/ui/services/mail/mailSvc.py:1197-1206,1213-1219`, `notifications/common/formatters/newMail.py:17-18` (notification data keys `senderName`, `subject`, `msg`), and `eve/client/script/ui/services/mail/notificationSvc.py:403-416` (stores/processes received notification).
- Implementation: `dotnet/src/Eve.Characters/Migrations/0022_character_deletion_corporation.sql`, `dotnet/src/Eve.Host/Composition/CharacterDeletionPurge.cs`, `dotnet/src/Eve.Host/Persistence/Mail/CharacterBiomassMailNotifications.cs`, and the existing wallet/mail/inventory/corporation owners.
- Local builds passed: `Eve.Host` and `Eve.Host.Tests`, zero warnings and errors. After the latest code-bearing merge of `origin/main` at `bdce763e`, owner and adjacent PostgreSQL test classes passed on `eve_p8` via `tools/dev/db-lock.sh`: Host 462/462, Characters 2/2, Events 20/20, Corporations 113/113, Wallet 4/4 and Inventory 29/29; zero failures and skips.
- The initial task push's main CI run 1557 on `4c3141c9` exposed a stale migration-list expectation in `Home_station_cooldown_migration_preserves_existing_rows`. Commit `c270580d` added migration `0022_character_deletion_corporation` to that test's expected list; the focused PostgreSQL test passed 1/1 locally on `eve_p8` via `tools/dev/db-lock.sh`.
- Selected Actions run 1356 for `c270580d` passed. Main CI run 1558 for `c270580de301` passed all jobs, including the headless end-to-end job; `ci-status.py failures c270580de301` reported no failed tests or jobs.
- The graphical V24.01 client was not exercised.

## Current worktree

This follow-up includes the biomass settlement implementation, persistence migration, Host/PostgreSQL regressions, and this handoff update. The pre-existing `docs/project/character-phase/handoff-RPC-00996-01023-charMgr.md` edit is outside this task and remains untouched.
