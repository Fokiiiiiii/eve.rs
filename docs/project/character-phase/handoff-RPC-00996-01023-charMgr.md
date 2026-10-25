# Character manager RPC coverage handoff (RPC-00996–01023)

## Scope and disposition

RPC-00996 and RPC-00997 are inventory-bound calls, not character-manager calls. V24.01 fitting invokes the bound inventory object's `Add` from `eve/client/script/ui/shared/fitting/fittingSlotController.py:237–241`; nodegraph fitting calls it from `nodegraph/client/actions/module.py:74–78`. `CharacterManagerMutationHostWireTests.Bound_inventory_add_uses_registered_host_wire_with_client_arguments_and_transport_metadata` routes the registered production `invbroker.MachoBindObject` and nested `Add` through the socket Host, with client `qty` and `flag`, transport `machoVersion=3396210`, persistent item movement and Host rebuild.

The remaining rows are registered on production `charMgr` and use character-owned persistence. EveJS v0.12.9 handlers: `Handle_AddContact` (`server/src/services/character/charMgrService.js:903–916`), `Handle_DeleteContacts` (922–928), `Handle_EditContactsRelationshipID` (930–943), `Handle_BlockOwners` (945–951), `Handle_AddOwnerNote` (1030–1036), `Handle_EditOwnerNote` (1038–1046), `Handle_DeleteCharacterSetting` (1079–1083).

## V24.01 callers and consumers

- AddContact 00998/00999: `addressbookService.py:732–733` adds agents; `:788–807` makes the CSPA-bound call, then updates local contact cache and scatters `OnContactChange`. The reply is unused.
- BlockOwners 01008/01009: `addressbookService.py:649–663` calls with one ID, updates blocked cache and scatters `OnBlockContacts`.
- DeleteContacts 01011/01012/01014/01015: `addressbookService.py:819–820` removes an agent; `:841–855` removes regular contacts and updates cache/events. The reply is unused.
- EditContactsRelationshipID 01016/01017: `addressbookService.py:682–692` calls, updates local relationship values and emits `OnContactChange`. The reply is unused.
- DeleteCharacterSetting 01010: `characterSettingsSvc.py:47–60` deletes the server value before clearing the local setting.
- AddOwnerNote 01002–01007 / EditOwnerNote 01018–01023: `notepad.py:168–203` chunks strings at 6000 characters, persists chunks and consumes returned note IDs/loaded `note` fields; `:206–214`, `:538–545`, `:622–647` create/edit named notes and folder labels.
- Contact list consumer: `addressbookService.py:193–250` consumes `addressbook.addresses` and `addressbook.blocked`, including `contactID`, `inWatchlist`, `relationshipID`, `labelMask`, and `senderID`.

The server returns `util.KeyVal` with rowsets for contact-list reads. The client uses AddOwnerNote's note ID and then reads `GetOwnerNote` rows. The contact callers emit cache notifications after successful RPC returns.

## Checks and remaining verification

- `CharacterPersonalRpcHostWireTests.CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire` passes locally (1 passed, 0 failed, 0 skipped) and Actions run `40217908521` passed for ref `c9e1a454ce5e`.
- Existing `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host` covers mutation state, 500-item limits, read-back and restart persistence through registered `IService.CallAsync`.
- Added `CharacterManagerMutationHostWireTests.Character_manager_mutations_persist_through_registered_host_wire`: the production `charMgr` from `CharacterHostRig` is registered in a real socket Host; tests AddContact, BlockOwners (500 owners), EditContactsRelationshipID, DeleteContacts (500 IDs), AddOwnerNote, both EditOwnerNote forms, setting save/delete, subsequent reads and note persistence after Host rebuild.
- Added `CharacterManagerMutationHostWireTests.Bound_inventory_add_uses_registered_host_wire_with_client_arguments_and_transport_metadata`: checks the bound inventory RPC, accepted transport metadata, item movement and persistence after Host rebuild.
- The `Eve.Host.Tests` project built with 0 warnings and 0 errors after both new regressions. One database-free focused wire test passed locally. The two database-backed wire tests could not be executed locally because this task prohibits local database use.
- Focused Actions runs `40224991836`, `40225134650`, `40225716431`, and `40225969854` all failed before test execution. `actions/setup-dotnet` received HTTP 409 for .NET 10.0.14 and SDK 10.0.403 from `dotnetorigin.blob.core.windows.net`.
- Push CI run `40225705507` for `a22cd8bb` failed on Linux and Windows during the same SDK download step. `ci-status.py failures 49caa4e9` reported no `ci.yml` run for the later documentation-only descendant.
- Earlier inventory-registry test `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` passed locally and Actions run `40221496527` for ref `8ce3380f`. It was restored after an earlier merge truncated its source; expected registered count is 1414.
- Descendant CI run `40221666019` for `e6310b34c165` failed in the separate `ConfigServiceTests.Batched_config_lookups_refuse_more_than_500_raw_ids` case. Main contains a later change to that test, but its post-merge CI could not start the .NET jobs because of the SDK download failure.

## Continuation record

- Initial Actions run `40217732635` used invalid ref `HEAD` and failed checkout; correct focused Host run `40217908521` passed.
- Two intervening inventory runs on ref `894528ec` failed before test results; that commit contained truncated test source, which was restored.
- `ci-status.py failures 8ce3380f` reported no push-triggered run for that SHA.
- RPC-00996–01023 need a successful Actions run of the two new PostgreSQL-backed wire regressions before completion. No PostgreSQL database was used locally.
