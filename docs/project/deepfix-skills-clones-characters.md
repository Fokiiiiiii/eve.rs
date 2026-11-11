# Skills, clones, and characters deep correctness review

## Confirmed divergence and fix

This pass found one new V24.01 client-visible divergence: `corporationSvc.GetEmploymentRecord` accepted a profile character ID only when the wire value was `PyInt`. Character IDs above `Int32.MaxValue` arrive as `PyLong`, so the service silently read employment for the active session character instead of the profile target. The registered service now accepts positive `PyInt` and `PyLong` values and keeps the active-character fallback when the argument is absent or invalid (`dotnet/src/Eve.Services.Corporations/CorporationService.cs:16-32`).

The regression uses the real Host RPC dispatcher and the combined `GetEmployementRecordAndCharacterTransfers` call. It gives the session character `90000001`, requests profile `4000000001` as `PyLong`, and asserts the read provider receives the requested ID, the history rowset remains client-shaped, and the transfer list stays empty (`dotnet/tests/Eve.Host.Tests/ClientVerifiedBatch28CdHostWireTests.cs:115-142`). Before the source fix, this test failed with expected profile ID `4000000001` but observed selected-character ID `90000001`; after the fix it passed.

## Client and reference trace

- The V24.01 employment panel calls `GetEmploymentRecordAndCharacterTransfers(session.charid)` (`/home/user/eve-logic/eve/client/script/ui/shared/neocom/charsheet/employmentHistoryPanel.py:21-24`). `info.GetEmploymentHistorySubContent(itemID)` forwards that ID and consumes `corporationID`, `startDate`, and `deleted` (`/home/user/eve-logic/eve/client/script/ui/shared/info/infosvc.py:2840-2869`). The faction-war cooldown path also requests `GetEmploymentRecord(session.charid)` (`/home/user/eve-logic/eve/client/script/ui/services/facWarSvc.py:289-299`).
- EveJS v0.12.9 resolves `args[0]` when supplied (`/home/user/evejs-261108.1/server/src/services/corporation/corpService.js:62-69`), returns the sorted `corporationID/startDate/deleted` rowset (`:413-445`), and returns that rowset with an empty transfer list from the combined RPC (`:467-469`).
- Production Host composition registers `CorporationService` (`dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:63`). Its persisted provider reads history in newest-first order (`dotnet/src/Eve.Corporations/CorporationStore.cs:549-556`); corporation moves close the previous row and insert the next history row in the same transaction (`:684-694`). If there is no persisted row, the service derives the initial employment from the authoritative character record and creation time (`dotnet/src/Eve.Services.Corporations/CorporationService.cs:25-32`). This RPC is read-only and emits no mutation notification.

The Long-ID regression uses a synthetic high ID to exercise wire decoding; it does not require a corresponding live character record. The existing PostgreSQL corporation protocol tests cover persisted history rows and native rowset shapes.

## Adjacent requested areas reviewed

The existing evidence records and current implementations were reviewed for the other requested skill and character flows. No additional new divergence was confirmed in this pass.

- Skill queue, training, skill points/injectors, extraction, attributes, and remapping: the V24.01 callers and their notifications/calculation inputs are traced in `docs/project/service-audit/deepfix-skillqueue.md:9-19` and `docs/project/service-audit/deepfix-attributes-remap.md:7-23`. EveJS comparisons and prior fail-first regressions are recorded there. This pass reran the `CharacterTrainingTests`, `SkillServiceTests`, `TrainingServiceTests`, and `SkillQueueContractTests` slices listed below.
- Jump clones, follower implants, clone-jump confirmation, clone installation limits, augmentations, and pod death: the client callsites, SDE-backed follower behavior, EveJS behavior, and existing Host regressions are recorded in `docs/project/evejs-261108-port-clones-mct-scoop.md:5-18` and `docs/project/audit-fixes-characters.md:7-29`. Medical-clone/pod-death boundaries remain subject to the limitation below.
- Character creation and selection: the V24.01 argument translation to `charUnboundMgr.CreateCharacterWithDoll` is at `/home/user/eve-logic/eve/client/script/ui/services/ccSvc.py:92-99`; Host creation/selection cases were included in the `HostServerTests` class run. Standing reads and client consumption are recorded in `docs/project/service-audit/DEEPCHECK_standing.md:32-46` and `docs/project/service-audit/DEEPCHECK_standingMgr.md:9-21`.

## Verification

The database-backed checks used the dedicated `eve_p3` database with pinned SDE build 3396210 and mission SDE build 3405148 loaded. No SDE files or generated data are part of this change.

- Fail-first Host RPC regression: failed before the fix on the target/session ID mismatch; passed after the fix, 1 passed, 0 skipped.
- `CorporationProtocolTests`: 55 passed, 0 skipped.
- `HostServerTests` after merging `origin/main`: 454 passed, 0 skipped.
- Character creation and jump-clone store slices (`CreationTests|JumpCloneStoreTests`): 29 passed, 0 skipped.
- Skill training store slice (`CharacterTrainingTests`): 24 passed, 0 skipped.
- Skill service slices (`SkillServiceTests|TrainingServiceTests|SkillQueueContractTests`): 16 passed, 0 skipped.
- Selected Host skill, standing, and character-flow tests: 10 passed, 0 skipped.
- `PilotAugmentationTests|PilotLearningBonusTests`: 4 passed, 0 skipped.
- `DeathTests`: 17 passed, 0 skipped.
- Release builds of `Eve.Services.Corporations` and `Eve.Host.Tests` succeeded with warnings treated as errors: 0 warnings, 0 errors for each.

After the main merge added inventory-backed contract item details to `PersistenceContractReadModel`, the existing auction Host test initially failed locally with PostgreSQL `42P01` because its read model used the default `inventory` schema while its fixture created a unique inventory schema. The fixture now passes that schema to the read model (`dotnet/tests/Eve.Host.Tests/ContractProxyPersistenceWireTests.cs:93`); the focused test and all 454 `HostServerTests` then passed. The expected wire response and gameplay assertions were not changed.

## Not checked

- The unmodified graphical V24.01 client was not launched, and no live Tranquility or other external EVE service was used.
- EveJS was source-traced but not run as a live end-to-end server flow in this pass.
- The high Long-ID profile scenario is a synthetic Host wire regression rather than a live profile for a persisted character above `Int32.MaxValue`.
- The whole test suite and extended CI/nightly checks were not run locally. Passing headless tests do not establish graphical-client verification.
