# Character manager RPC coverage handoff (RPC-00996–01023)

## Scope and disposition

The first two audit rows point at inventory operations, not character manager operations. The V24.01 fitting UI calls the bound inventory object's `Add` from `eve/client/script/ui/shared/fitting/fittingSlotController.py:238`; nodegraph module fitting calls that same `Add` from `nodegraph/client/actions/module.py:75`. The owner method is `invbroker.Add`, which is covered by `CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item` (registered production Host service, transport-added `machoVersion`, client quantity, persisted item movement). It is not `charMgr.Add`; no character-manager route should be added for these rows.

The remaining listed operations are already registered by the production `charMgr` service and use its character-owned persistence path. EveJS v0.12.9 counterparts are `Handle_AddContact` (`server/src/services/character/charMgrService.js:903–916`), `Handle_DeleteContacts` (922–928), `Handle_EditContactsRelationshipID` (930–943), `Handle_BlockOwners` (945–951), `Handle_AddOwnerNote` (1030–1036), `Handle_EditOwnerNote` (1038–1046), and `Handle_DeleteCharacterSetting` (1079–1083).

## Client call and consumption evidence

- AddContact (RPC-00998/00999): `addressbookService.py:733` adds an agent and updates its local list; `:788–807` makes the CSPA-wrapped AddContact call, then updates the local contact and scatters `OnContactChange`. The mutation reply is unused.
- BlockOwners (01008/01009): `addressbookService.py:658` sends a one-owner list; after success the caller updates its blocked map, scatters `OnBlockContacts`, and refreshes the window (`:659–663`).
- DeleteContacts (01011/01012/01014/01015): `addressbookService.py:820` removes an agent; `:842` removes character contacts. Both update local state and emit the contact change event after the call (`:843–855`). The mutation reply is unused.
- EditContactsRelationshipID (01016/01017): `addressbookService.py:688` sends the selected IDs and relationship; `:689–692` updates local relationship values and scatters `OnContactChange`. The mutation reply is unused.
- DeleteCharacterSetting (01010): `characterSettingsSvc.py:56` calls delete before removing its local cached setting.
- AddOwnerNote (01002–01007) and EditOwnerNote (01018–01023): `notepad.py:173–175` persists settings notes in chunks of at most 6000 characters and stores returned note IDs; `:187–203` loads notes and consumes each row's `note` field. `:207–214` creates and edits a note; `:545` edits a saved note; `:623` creates a note; `:647` edits a note label. AddOwnerNote's returned ID is retained as the note key; EditOwnerNote's return is ignored.

The full contact-list consumer is `addressbookService.py:193–250`: it reads `addressbook.addresses` and `addressbook.blocked`, maps address columns `contactID`, `inWatchlist`, `relationshipID`, and `labelMask`, and maps blocked `senderID` values into client caches. The inventory Add callers are `fittingSlotController.py:237–241` and `nodegraph/client/actions/module.py:74–78`.

## Checks and remaining work

The database-free regression `CharacterPersonalRpcHostWireTests.CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire` dispatches through the registered service over Host transport and asserts the `util.KeyVal`, `addresses.lines`, and `blocked.lines` shape. It passed locally (1 passed, 0 failed, 0 skipped) and in the selected Actions run for test ref `c9e1a454ce5e`.

Existing `CharacterContactNotepadHostTests.Character_contact_note_and_setting_rpcs_round_trip_through_registered_host` covers mutations, 500-owner input limits, read-back, and restart persistence. It calls registered production services directly through `IService.CallAsync`, not through Host wire, and requires PostgreSQL. The no-database constraint prevented running it or adding and running a database-backed Host wire mutation regression. The mutation rows are not end-to-end wire-verified.

The push-triggered CI run for `c9e1a454ce5e` exposed a stale RPC inventory assertion: a merged mainline change registered 13 additional methods, so `RpcSecurityInventoryTests.Real_host_registry_methods_are_enumerable` observed 1414 where it expected 1401. The test's expected count is corrected to 1414.

## 2026-10-25 continuation

- No PostgreSQL database was accessed. The mutation rows remain short of the requested end-to-end wire verification.
- Merging `origin/main` exposed a malformed truncation in `dotnet/tests/Eve.Host.Tests/RpcSecurityInventoryTests.cs` (CS1022 at lines 1, 3, and 15–20). The intact test source was restored from the read-only main checkout into this worktree. The post-merge `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore` passed with 0 warnings and 0 errors.
- Local focused test: `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-build --filter "FullyQualifiedName~CharMgr_contact_list_uses_the_registered_service_and_client_rowsets_over_host_wire"` passed (1 passed, 0 failed, 0 skipped). It checks the existing list read wire path, not mutation rows.
- The restored RPC inventory assertion still needs its focused Actions check and the push-triggered CI result.
