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

## Local verification

- `dotnet build dotnet/src/Eve.Skills/Eve.Skills.csproj -p:UseSharedCompilation=false`: passed, zero warnings and errors.
- `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false`: passed, zero warnings and errors.
- `dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false`: passed, zero warnings and errors.
- Focused deletion, rollback, and expiry tests: 3 passed, 0 failed, 0 skipped.
- `CharacterHostTests`: 33 passed, 0 failed, 0 skipped.
- `SpaceServiceTests`: 160 passed, 0 failed, 0 skipped.

## Post-merge verification and gate

After fast-forwarding to main commit `2178010953f5`, the `Eve.Skills`, `Eve.Host.Tests`, and `Eve.Services.Space.Tests` projects built successfully. On `eve_p8`, all `CharacterHostTests` plus the updated RPC fuzz regression passed (34 passed, 0 failed, 0 skipped); `SpaceServiceTests` passed 160, 0 failed, 0 skipped.

Main CI run `1519` at `4fe2df93417b` first failed in `Eve.Host.Tests.RpcFuzzTests.Registered_rpcs_refuse_seeded_hostile_arguments_through_dispatcher` due to a PostgreSQL read timeout at `RpcFuzzTests.cs:195`. Main commit `2178010953f5` adjusted that test; run `1520` completed successfully with no failed jobs. The same test passed locally after the merge.

The expert-system deletion changes are staged for commit. Two unrelated pre-existing worktree edits remain unstaged and untouched.

The graphical V24.01 client was not exercised.
