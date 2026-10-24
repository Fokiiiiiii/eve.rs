# Character contact, notes, and inventory Add RPC coverage

## Scope and endpoint correction

The 25 requested rows group into eight behavior paths:

| Rows | Registered eve.rs endpoint | V24.01 client call and consumed result |
|---|---|---|
| RPC-00996, RPC-00997 | invbroker.MachoBindObject → bound Add | fittingSlotController.py:238,241 and nodegraph/client/actions/module.py:75,78 call inventory Add to move a fitted module to the hangar or cargo. They do not consume the returned item ID; the move and resulting inventory state are authoritative. The owner is invbroker, not charMgr. |
| RPC-00998, RPC-00999 | charMgr.AddContact | addressbookService.py:733 adds an agent contact and updates the client cache; the regular contact path calls the same method and then updates the cache and scatters OnContactChange. Devtools also calls it at form_tournament.py:302,305. The call returns no client value. |
| RPC-01002–RPC-01007 | charMgr.AddOwnerNote | notepad.py:175 appends returned note IDs to settings; notepad.py:207,623 stores the returned ID in the note/folder state. The client later calls GetOwnerNote and reads note[0].note while rebuilding a setting (around lines 187–199). |
| RPC-01008, RPC-01009 | charMgr.BlockOwners | addressbookService.py:658 ignores the return, records the blocked owner locally, emits OnBlockContacts, and refreshes the window. |
| RPC-01010 | charMgr.DeleteCharacterSetting | characterSettingsSvc.py:56 ignores the return and removes the key from its local settings cache. |
| RPC-01011, RPC-01012, RPC-01014, RPC-01015 | charMgr.DeleteContacts | addressbookService.py:820,842 removes agent or personal contacts; the caller updates its local map and emits contact-change events. form_tournament.py:811 is an additional client-tree caller. |
| RPC-01016, RPC-01017 | charMgr.EditContactsRelationshipID | addressbookService.py:688 ignores the return, changes the cached relationship IDs, and emits OnContactChange. Corporation and alliance branches use their own services (lines 693–698). |
| RPC-01018–RPC-01023 | charMgr.EditOwnerNote | notepad.py:173,214,545,647 edits note text or label. The return is ignored; local note state is updated and GetOwnerNote is used to read it later. |

The client references above were searched across the full /home/user/eve-logic tree and the relevant callers and consumers were read in context. Client paths use one-based source line numbers. The Macho client wrapper adds machoVersion to call kwargs (carbon/common/script/net/ServiceCallGPCS.py:222–226), and the object call dispatcher strips that transport field before invoking the service (carbon/common/script/net/ObjectCallGPCS.py:129–134). The handlers therefore must tolerate the normal transport envelope.

## Behavior comparison

The EveJS v0.12.9 handlers are server/src/services/inventory/invBrokerService.js:12087–12673 (Handle_Add) and server/src/services/character/charMgrService.js:903–951,1017–1046,1079–1083. The reference moves inventory state for bound Add, persists contacts and blocked owners, returns an owner-note ID, edits/removes notes and contacts, and deletes a setting. Its AddContact also returns no value and can create the recipient notification requested by the client.

eve.rs already wires the corresponding real handlers: InventoryRpc includes HostInventoryServiceFactory.CreateInvBroker (dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:40–45), and CharacterServiceList includes InventoryRpc (dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:56). InvBrokerService.MachoBindObject/Add are in dotnet/src/Eve.Services.Inventory/InvBrokerService.cs. CharacterRpc.cs:73 constructs CharMgrService with the existing character-notes read/update model. The Add rows were classified MISSING in the input, but the source shows an existing registered and stateful bound-inventory implementation; this batch adds the missing Host regression coverage rather than duplicating the route. The contact notification path already has a Host regression that checks type 89, event argument shape, persistence, and restart (CharacterHostTests.Contact_add_persists_and_routes_the_recipient_notification).

No second contact/note/settings store or stub handler was introduced. Bulk owner inputs allow up to 500 IDs; the service unit tests retain rejection coverage for malformed and over-limit input (CharacterInfoTests.cs:750–760).

## Regression added

dotnet/tests/Eve.Host.Tests/CharacterContactNotepadHostTests.cs adds two database-backed Host tests through the Host registry:

- the bound invbroker.MachoBindObject route executes Add with qty=None, a cargo flag, and machoVersion, then checks the inventory move survives a Host restart;
- the registered charMgr route runs add/edit/delete contact, block, add/edit owner note, and delete-setting calls, checks their client-visible response shapes and the 500-ID burst, then checks persisted state after restart.

## Verification record

- Local check: dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore succeeded with 0 warnings and 0 errors.
- The tests require the Host PostgreSQL fixture; no PostgreSQL database was used locally.
- GitHub Actions run 40093894419 failed the focused charMgr Host regression at AddOwnerNote: `PersistenceCharMgrReadModel.WriteChanges` received `TABLE_NOT_FOUND` for `characterNotes`. The whole Host route reached persistence; `LegacyGameStoreComposition.OpenAsync` had loaded `characters` but omitted `characterNotes`, so cache writes could not resolve that table.
- Fixed Host initialization by loading `characterNotes` alongside `characters` in `dotnet/src/Eve.Host/Composition/LegacyGameStoreComposition.cs`. The regression exercises note creation, reads, and restart persistence through the registered Host.
- Local check after the fix: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore` succeeded with 0 warnings and 0 errors.
- Focused Actions run 40094801177 passed the charMgr round-trip test and failed only the inventory Add test because the test expected an item ID in the bound RPC result. The actual result is `PyNone`; client callers ignore Add's return and observe the inventory move via state. The regression now asserts that wire result, then verifies the persisted location and flag.
- Focused Actions run 40095571275 passed the charMgr test and showed the first inventory setup left the stack at its source. The regression now uses the client-shaped station-hangar bound call: item starts in the ship cargo, the hangar receives `Add(itemID, shipID, flag=4)`, and the test checks returned item ID plus persisted hangar location/flag after restart. This follows the caller path from `fittingSlotController.py:238,241`.
- Focused Actions run 40097861742 passed the charMgr test. The inventory route returned `PyNone` because `NewItem` defaults `Singleton=false`; `CharacterShips.MoveAsync` refuses to move from a non-singleton ship. The Host fixture now marks its Rifter as a singleton and saves it as the character's active ship with `PgCharacterStore.SetShipAsync` in the item transaction.
- Focused Actions run 40098949992 passed both new Host tests (2 passed, 0 failed, 0 skipped).
- The push-triggered CI gate for `e114b7c5` exposed a separate merged ConfigService regression: the existing 501-ID limit test expected `CONFIG_BULK_LIMIT_EXCEEDED`, while `MaximumRequestedIds` was 10,000 and execution fell through into a null test dependency. Reduced the configured limit to the tested 500-ID contract; this CI fix is included in the final batch push.
- The push-triggered CI gate result and focused ConfigService rerun will be added after they complete.
