# Character manager RPC coverage handoff (RPC-00996–01023)

## Scope and disposition

The first two audit rows point at inventory operations, not character manager operations. The V24.01 fitting UI calls the bound inventory object's `Add` from `eve/client/script/ui/shared/fitting/fittingSlotController.py:238`; nodegraph module fitting calls that same `Add` from `nodegraph/client/actions/module.py:75`. The owner method is `invbroker.Add`, covered by `CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item` (registered Host service, transport-added `machoVersion`, client quantity, persisted movement). It is not `charMgr.Add`; no character-manager route should be added for these rows.

The remaining listed operations are already registered by the production `charMgr` service and use its character-owned persistence path. EveJS v0.12.9 counterparts are `Handle_AddContact` (`server/src/services/character/charMgrService.js:903–916`), `Handle_DeleteContacts` (922–928), `Handle_EditContactsRelationshipID` (930–943), `Handle_BlockOwners` (945–951), `Handle_AddOwnerNote` (1030–1036), `Handle_EditOwnerNote` (1038–1046), and `Handle_DeleteCharacterSetting` (1079–1083).

## Client call and consumption evidence

- AddContact (RPC-00998/00999): `addressbookService.py:733` adds an agent and updates its local list; `:788–807` makes the CSPA-wrapped AddContact call, then updates the local contact and scatters `OnContactChange`. The mutation reply is unused.
- BlockOwners (01008/01009): `addressbookService.py:658` sends a one-owner list; after success the caller updates its blocked map, scatters `OnBlockContacts`, and refreshes the window (`:659–663`).
- DeleteContacts (01011/01012/01014/01015): `addressbookService.py:820` removes an agent; `:842` removes character contacts. Both update local state and emit the contact change event after the call (`:843–855`). The mutation reply is unused.
- EditContactsRelationshipID (01016/01017): `addressbookService.py:688` sends selected IDs and relationship; `:689–692` updates local relationship values and scatters `OnContactChange`. The mutation reply is unused.
- DeleteCharacterSetting (01010): `characterSettingsSvc.py:56` calls delete before removing its local cached setting.
- AddOwnerNote (01002–01007) and EditOwnerNote (01018–01023): `notepad.py:173–175` persists settings notes in chunks of at most 6000 characters and stores returned note IDs; `:187–203` loads notes and consumes each row's `note` field. `:207–214` creates and edits a note; `:545` edits a saved note; `:623` creates a note; `:647` edits a note label. AddOwnerNote's returned ID is retained as the note key; EditOwnerNote's return is ignored.

The full contact-list consumer is `addressbookService.py:193–250`: it reads `addressbook.addresses` and `addressbook.blocked`, maps address columns `contactID`, `inWatchlist`, `relationshipID`, and `labelMask`, and maps blocked `senderID` values into client caches. The inventory Add callers are `fittingSlotController.py:237–241` and `nodegraph/client/actions/module.py:74–78`.

## Checks and remaining work

The database-free regression `CharacterPersonalRpcHostWireTests.CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire` sends a Host request through the registered service and asserts the `util.KeyVal`, `addresses.lines`, and `blocked.lines` shape. It passed locally (1 passed, 0 failed, 0 skipped) and in selected Actions run `40217908521` for test ref `c9e1a454ce5e`.

Existing `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host` covers the mutations, 500-owner input limits, read-back, and restart persistence. It calls registered services directly through `IService.CallAsync`, not over Host wire, and requires PostgreSQL. No database-backed mutation test was run locally. The mutation rows remain short of the requested end-to-end Host wire verification.

The push-triggered CI run for `c9e1a454ce5e` exposed a stale RPC inventory count: merged mainline changes registered 13 additional methods, so `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` saw 1414 rather than 1401. Its expected count is updated to 1414.

## Continuation record

- Initial Actions run `40217732635` received the invalid ref name `HEAD` and failed checkout. Corrected run `40217908521` passed the focused Host wire test.
- Push-triggered CI for `c9e1a454ce5e` failed only at `Real_host_registry_methods_are_enumerable` (expected 1401, actual 1414).
- A later merge truncated the beginning of `RpcSecurityInventoryTests.cs`; local compilation reported CS1022. The complete test source was restored with the count set to 1414.
- The repaired `Eve.Host.Tests` project build passed locally (0 warnings, 0 errors). No PostgreSQL connection was made by the local build.
- Selected Actions runs `40219150895`, `40219187166`, and `40219689764` did not produce test results; they failed during the solution build. The last run used the truncated source, and the two earlier runs overlapped other solution builds on the runner.
- Another selected run, `40219694445`, succeeded for test ref `591d8f6f`; this does not verify the repaired inventory test.
- Push-triggered CI `40220052863` for `ac29183aa8ff` was in progress at the last status check. The restored inventory regression still needs a focused Actions run after the repair commit.
- Do not mark the mutation rows as end-to-end wire-verified until a database-backed Host wire regression is run.
