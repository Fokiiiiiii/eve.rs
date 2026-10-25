# Character manager RPC coverage handoff (RPC-00996–01023)

## Scope and disposition

RPC-00996 and RPC-00997 are inventory-bound calls, not character-manager calls. The V24.01 fitting UI calls the bound inventory object's `Add` from `eve/client/script/ui/shared/fitting/fittingSlotController.py:237–241`; nodegraph fitting does so from `nodegraph/client/actions/module.py:74–78`. The new socket test invokes the registered production `invbroker.MachoBindObject` with nested `Add`, client `qty`/ `flag`, and transport `machoVersion=3396210`; it checks movement and restart persistence.

The remaining rows are registered on production `charMgr` and use character-owned persistence. EveJS v0.12.9 handlers: `Handle_AddContact` (`server/src/services/character/charMgrService.js:903–916`), `Handle_DeleteContacts` (922–928), `Handle_EditContactsRelationshipID` (930–943), `Handle_BlockOwners` (945–951), `Handle_AddOwnerNote` (1030–1036), `Handle_EditOwnerNote` (1038–1046), `Handle_DeleteCharacterSetting` (1079–1083).

## V24.01 callers and consumers

- AddContact 00998/00999: `addressbookService.py:732–733` adds agents; `:788–807` makes the CSPA-bound call, then updates local contact cache and scatters `OnContactChange`. The reply is unused.
- BlockOwners 01008/01009: `addressbookService.py:649–663` calls with one ID, updates blocked cache and scatters `OnBlockContacts`.
- DeleteContacts 01011/01012/01014/01015: `addressbookService.py:819–820` removes an agent; `:841–855` removes regular contacts and updates cache/events. The reply is unused.
- EditContactsRelationshipID 01016/01017: `addressbookService.py:682–692` calls, updates local relationship values and emits `OnContactChange`. The reply is unused.
- DeleteCharacterSetting 01010: `characterSettingsSvc.py:47–60` deletes the server value before clearing the local setting.
- AddOwnerNote 01002–01007 / EditOwnerNote 01018–01023: `notepad.py:168–203` chunks strings at 6000 characters, persists chunks and consumes returned note IDs/loaded `note` fields; `:206–214`, `:538–545`, `:622–647` create/edit named notes and folder labels.
- Contact list consumer: `addressbookService.py:193–250` consumes `addressbook.addresses` and `addressbook.blocked`, including `contactID`, `inWatchlist`, `relationshipID`, `labelMask`, and `senderID`.

The server returns `util.KeyVal` with rowsets for contact-list reads; the client uses AddOwnerNote's note ID and then reads `GetOwnerNote` rows. Client callers emit the contact cache notifications after successful RPC returns.

## Checks

- `CharacterPersonalRpcHostWireTests.CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire` covers a real socket Host request through registered `charMgr`, asserting `util.KeyVal` and both rowset shapes. Local focused run: 1 passed, 0 failed, 0 skipped. Actions run `40217908521` passed for ref `c9e1a454ce5e`.
- Existing `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host` covers mutation state, 500-item limits, read-back and restart persistence through registered `IService.CallAsync`.
- Added `CharacterManagerMutationHostWireTests.Character_manager_mutations_persist_through_registered_host_wire`: uses the production `charMgr` from `CharacterHostRig` in the real socket Host; covers AddContact, BlockOwners (500 owners), EditContactsRelationshipID, DeleteContacts (500 IDs), AddOwnerNote, both EditOwnerNote call forms, setting save/delete, subsequent reads and note persistence after Host rebuild.
- Added `CharacterManagerMutationHostWireTests.Bound_inventory_add_uses_registered_host_wire_with_client_arguments_and_transport_metadata`: sends V24.01 build `machoVersion` in both outer transport and nested bound call, accepts `qty=None`, moves a stack into its ship, and confirms persistence after Host rebuild.
- The test project build passed after these tests were added (0 warnings, 0 errors). The local database-free focused contact-list wire test passed (1 passed, 0 failed, 0 skipped). Both new wire regressions require PostgreSQL and were not executed locally per the no-database instruction.
- Focused Actions runs `40224991836`, `40225134650`, and `40225716431` failed before test execution: `actions/setup-dotnet` received HTTP 409 downloading .NET 10.0.14 and SDK 10.0.403 from dotnetorigin.blob.core.windows.net.
- Push CI run `40225705507` for `a22cd8bb` also failed in setup-dotnet on Linux and Windows with the same HTTP 409. Other jobs completed; the focused .NET tests did not start.
- Earlier inventory-registry test `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` was restored after a merge truncated its source; expected count 1414. Local project build and Actions run `40221496527` passed for ref `8ce3380f`.
- Descendant push CI run `40221666019` for `e6310b34c165` failed in the separate `ConfigServiceTests.Batched_config_lookups_refuse_more_than_500_raw_ids` case. Main includes a change to that test; its post-merge CI result is affected by the same SDK download failure.

## Continuation record

- Initial Actions run `40217732635` used invalid ref `HEAD` and failed checkout; correct focused Host run `40217908521` passed.
- Repaired inventory test Actions run `40221496527` passed for ref `8ce3380f`.
- Two intervening inventory runs on ref `894528ec` failed before test results; that commit contained truncated test source, which was restored.
- `ci-status.py failures 8ce3380f` reported no push-triggered run for that SHA. The later descendant CI result is recorded above.
- The two new PostgreSQL-backed socket tests need a successful focused Actions run. No database was used locally.
