# Upwell structure service access handoff (cx4)

## Implementation

- `SettingIdForService` now covers every key in the V24.01 `SERVICES_ACCESS_SETTINGS` table. The `SETTING_NONE` value is unconditional, matching EveJS `characterHasStructureSetting`.
- Registered Host wire coverage checks all 27 mapped service IDs through `CharacterGetServices` and `GetMyCharacterStructures`. It also checks `CharacterHasService` and `CharacterCheckService` for reprocessing, repair, and insurance.
- `CharacterGetServices` is the client path used by `structureServices.IsServiceAvailableForCharacter`; this unblocks the client preflight for the three services.

## Client and EveJS evidence

- V24.01 `structures/services.py:119-145` is the full service-to-setting table, including fitting/repair/insurance → `SETTING_HOUSING_CAN_DOCK`, reprocessing → `SETTING_REPROCESSING_TAX`, automoon mining → `SETTING_AUTOMOONMINING`, and all industry subservices.
- V24.01 `eve/client/script/ui/services/structure/structureServices.py:26-58` fetches `CharacterGetServices`, and treats fitting and docking as unrestricted; `structureSettings.py:16-18` forwards `CharacterHasService`.
- V24.01 `eve/client/script/ui/eveCommands.py:2502-2522` gates repair, insurance, and reprocessing using those service checks. `infosvc.py:840-866` consumes `CharacterGetServices` to render structure service details.
- V24.01 `structureDirectory.py:45-49,64-68` separates `GetMyCharacterStructures` and `GetMyCorporationStructures`; the latter supplies structure-browser rows (`structureBrowserController.py:46-52,76-83`), whose service field is read at `structureEntryController.py:112-138`.
- `GetStructureInfo` has other client callers, but the service browser rows come from the listing calls above. EveJS intentionally omits `services` from its non-owner/basic `GetStructureInfo` payload (`structurePayloads.js:295-307`; handler selection at `structureDirectoryService.js:342-352`), so the eve.rs basic projection stays aligned.
- V24.01 client search found no call to `GetStructureInfos`. Host `Rpc/CoreRpc.cs:91-102` uses the batch read only inside `ConfigService.readStructureNames`, then projects `ItemName`; it is not registered as a client RPC.
- EveJS 261028.1 maps the same table at `structureServiceAuthority.js:131-159`; `structurePayloads.js:165-183` makes `SETTING_NONE` unconditionally accessible, and `:211-253` applies that setting check to online services.

## Validation and limits

- Passed `dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false --no-restore` (0 warnings/errors). The focused service-access wire test, neighboring structure-directory Host test, and persisted-directory read-model test passed across selected runs (3 passed, 0 skipped).
- Push-triggered CI run 1332 completed successfully; `ci-status.py wait` reported no failed tests or jobs. Its output did not enumerate skipped tests.
- PostgreSQL was not used, as required for this job. The test covers service availability gates and wire data, not a paid repair/insurance/reprocessing transaction. No graphical-client session was used.
