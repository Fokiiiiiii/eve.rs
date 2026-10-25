# Character manager RPC coverage handoff (RPC-00996–01023)

## Scope and disposition

The first two rows are inventory-bound calls. The V24.01 fitting UI calls the bound inventory object's `Add` from `eve/client/script/ui/shared/fitting/fittingSlotController.py:238`; nodegraph module fitting calls it from `nodegraph/client/actions/module.py:75`. The method is `invbroker.Add`, not `charMgr.Add`, and is covered by `CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item` over registered Host transport with `machoVersion`, client quantity, and persistent item movement.

The remaining RPCs are registered on production `charMgr` and use character-owned persistence. EveJS v0.12.9 handlers: `Handle_AddContact` (`server/src/services/character/charMgrService.js:903–916`), `Handle_DeleteContacts` (922–928), `Handle_EditContactsRelationshipID` (930–943), `Handle_BlockOwners` (945–951), `Handle_AddOwnerNote` (1030–1036), `Handle_EditOwnerNote` (1038–1046), `Handle_DeleteCharacterSetting` (1079–1083).

## Client callers and consumers

- AddContact 00998/00999: `addressbookService.py:733` adds agents; `:788–807` calls AddContact through CSPA and updates local contacts / emits `OnContactChange`. Reply unused.
- BlockOwners 01008/01009: `addressbookService.py:658–663` calls with one ID, updates blocked cache, emits `OnBlockContacts`, refreshes.
- DeleteContacts 01011/01012/01014/01015: `addressbookService.py:820` deletes an agent; `:842–855` deletes regular contacts and updates cache/events. Reply unused.
- EditContactsRelationshipID 01016/01017: `addressbookService.py:688–692` calls, updates local relationship values and emits `OnContactChange`. Reply unused.
- DeleteCharacterSetting 01010: `characterSettingsSvc.py:56` deletes on server before clearing its local setting.
- AddOwnerNote 01002–01007 / EditOwnerNote 01018–01023: `notepad.py:173–175` persists chunks capped at 6000 characters and retains returned note IDs; `:187–203` consumes loaded `note` rows; `:207–214`, `:545`, `:623`, `:647` create/edit notes and labels. AddOwnerNote's ID is consumed; EditOwnerNote reply is ignored.

Whole-client contact-list consumer: `addressbookService.py:193–250` reads `addressbook.addresses` and `addressbook.blocked`, consumes `contactID`, `inWatchlist`, `relationshipID`, `labelMask`, and `senderID`. Inventory Add callers are `fittingSlotController.py:237–241` and `nodegraph/client/actions/module.py:74–78`.

## Checks and remaining work

Added `CharacterPersonalRpcHostWireTests.CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire`: database-free real Host request through the registered service, asserting `util.KeyVal`, `addresses.lines`, and `blocked.lines`. It passed locally (1 passed, 0 failed, 0 skipped) and selected Actions run `40217908521` (ref `c9e1a454ce5e`).

Existing `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host` exercises mutation state, 500-item limits, read-back, and restart persistence through registered `IService.CallAsync`, not Host wire. It requires PostgreSQL and was not run locally. The mutation rows remain short of the requested end-to-end Host wire verification.

The push CI for `c9e1a454ce5e` found `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` expected 1401 but observed 1414 after 13 RPC registrations merged from main. The expected count is now 1414. A later merge truncated the beginning of that test; local compilation reported CS1022. The complete test source was restored and the project build passed locally (0 warnings, 0 errors).

## Continuation record

- Initial Actions run `40217732635` used invalid ref `HEAD` and failed checkout; correct focused Host run `40217908521` passed.
- Repaired inventory test Actions run `40221496527` passed for ref `8ce3380f` (no failed tests).
- Two intervening inventory runs on ref `894528ec` failed before reporting test results. The source on that commit had the truncation; its focused failure exposed the need for restoration.
- Push-triggered CI run `40221666019` for descendant SHA `e6310b34c165` was in progress at the last status check. `ci-status.py failures 8ce3380f` reported no push-triggered run for that SHA.
- Mutation rows are not end-to-end wire verified. Do not mark them complete until a database-backed Host wire regression is run.
