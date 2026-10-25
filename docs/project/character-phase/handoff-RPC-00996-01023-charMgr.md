# Character manager RPC coverage handoff (RPC-00996–01023)

## Scope and disposition

The first two audit rows point at inventory operations, not character manager operations. The V24.01 fitting UI calls the bound inventory object's `Add` from `eve/client/script/ui/shared/fitting/fittingSlotController.py:238`; nodegraph module fitting calls that same `Add` from `nodegraph/client/actions/module.py:75`. The owner method is `invbroker.Add`, which is covered by `CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item` (real Host wire, including `machoVersion`, client quantity and persisted item movement). It is not `charMgr.Add`; no character-manager route should be added for these rows.

The remaining listed operations are already registered by the production `charMgr` service and use its character-owned persistence path. EveJS v0.12.9 counterparts are `Handle_AddContact` (charMgrService.js:903–916), `Handle_DeleteContacts` (922–928), `Handle_EditContactsRelationshipID` (930–943), `Handle_BlockOwners` (945–951), `Handle_AddOwnerNote` (1030–1036), `Handle_EditOwnerNote` (1038–1046), and `Handle_DeleteCharacterSetting` (1079–1083).

## Client call and consumption evidence

- AddContact (RPC-00998/00999): `addressbookService.py:733` adds an agent and updates its local list; `:788–807` makes the CSPA-wrapped AddContact call, then updates the local contact and scatters `OnContactChange`. The mutation reply is unused.
- BlockOwners (01008/01009): `addressbookService.py:658` sends a one-owner list; after success the caller updates its blocked map, scatters `OnBlockContacts`, and refreshes the window (`:659–663`).
- DeleteContacts (01011/01012/01014/01015): `addressbookService.py:820` removes an agent; `:842` removes character contacts. Both update local state and emit the contact change event after the call (`:843–855`). The mutation reply is unused.
- EditContactsRelationshipID (01016/01017): `addressbookService.py:688` sends the selected IDs and relationship; `:689–692` updates local relationship values and scatters `OnContactChange`. The mutation reply is unused.
- DeleteCharacterSetting (01010): `characterSettingsSvc.py:56` calls delete before removing its local cached setting.
- AddOwnerNote (01002–01007) and EditOwnerNote (01018–01023): `notepad.py:173–175` persists settings notes in chunks of at most 6000 characters and stores the returned note IDs; `:187–203` loads notes and consumes each row's `note` field. `:207–214` creates and edits a note; `:545` edits a saved note; `:623` creates a note; `:647` edits a note label. AddOwnerNote's returned ID is retained as the note key; EditOwnerNote's return is ignored.

The full contact-list consumer is `addressbookService.py:193–250`: it reads `addressbook.addresses` and `addressbook.blocked`, maps address columns `contactID`, `inWatchlist`, `relationshipID`, and `labelMask`, and maps blocked `senderID` values into client caches.

## Checks and remaining work

The added database-free regression `CharacterPersonalRpcHostWireTests.CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire` calls the registered production service over Host transport and asserts the `util.KeyVal` plus `addresses.lines` and `blocked.lines` shape. Its focused test passed locally. Existing `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host` covers the mutations, 500-owner input limits, read-back, and restart persistence, but calls the registered service directly rather than over Host wire and requires PostgreSQL. The task's no-database constraint prevented running that test or adding a database-backed wire regression for these mutations. Do not mark the mutation rows as end-to-end wire-verified until such a wire regression is run.
