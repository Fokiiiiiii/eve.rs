# corpmgr coverage handoff — cx3

## Completed

- RPC-01242–RPC-01243 (GetCorporationIDForCharacter from contract assignee checks) and RPC-01248–RPC-01249 (GetCorporations) are covered by CorporationPublicInfoHostWireTests.Corporation_identity_and_row_reads_use_persisted_records_through_registered_host.
- The DB-backed test registers the real CorpMgrService, dispatches Macho CallReq packets via PacketDispatcher, creates a corporation and member in PostgreSQL, then checks the member's corporation ID, None for an unknown character, the persisted util.Row header/line fields, and None for a missing corporation. These are read calls; no notification is expected.
- The test exposed a defect: an unknown character ID received the logged-in corporation ID. eve-logic calls the RPC at eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:1204-1205 and uses the result to classify a contract assignee. characterInfoWindow.py:110-121 and infoContentBase.py:471-476 use it to show a character's corporation banner. Current EveJS 261028.1 server/src/services/corporation/corpmgrService.js:507-524 returns null for an unknown non-session character; eve.rs now returns PyNone.
- GetCorporations is called at eve/client/script/ui/services/corporation/bco_corporations.py:50; its util.Row header and line are consumed by LoadCorporation at lines 64-76. EveJS 261028.1 server/src/services/corporation/corpmgrService.js:526-530 likewise returns a corporation row for a resolved corporation and null when none exists.
- Verification on database eve_p3: Host.Tests build passed; CorporationPublicInfoHostWireTests passed 2/2 with 0 skipped; CorporationProtocolTests passed 51/51 with 0 skipped. The earlier test expectation that unknown character IDs resolve to the caller's corporation was corrected from the client/EveJS evidence.
- The graphical client was not used. RPC-01244–RPC-01247 remain untested: those info-window callers request NPC character corporations, and the current code does not resolve an NPC corporation from static data.

- RPC-01234–RPC-01241 (`GetAssetInventoryForLocation`) are covered by `CorporationAssetInventoryHostWireTests.Corporation_asset_locations_read_persisted_items_through_registered_host`. The database-backed test dispatches the registered `corpmgr` route and checks office, property, delivery, and impounded item rows, their exact CRowset columns and values, location filtering, and an empty result. Reads publish no notification; the returned `CachedMethodCallResult` remains the expected response.
- Whole-client evidence: `corp_ui_accounts.py:405-410` reads impounded rows (`typeID`, `stacksize`) before invalidating cache and trashing; `:422-468` reads buckets for the corporation assets UI and consumes `itemID`, `typeID`, `flagID`, and `stacksize`; `:470-496` filters office rows by `flagID`; `corp_ui_votes.py:335,438-446` combines office and impounded rows for vote item selection. Cache invalidation callers are `assetSafetySvc.py:59`, `officeManager.py:121`, and `menuSvcExtras/invItemFunctions.py:293`.
- Current EveJS 261028.1 `server/src/services/corporation/corpmgrService.js:599-619` returns a cached CRowset of items for the location; the web gateway contract at `server/src/_secondary/express/evejsWebGatewayRuntime.js:1532-1540` records the 11-field row shape and int64 `itemID`/`locationID`. The eve.rs route produces that shape, so the test exposed no handler defect.
- Verification on database `eve_p3`: `CorporationAssetInventoryHostWireTests` passed 1/1 with 0 skipped; `Corp_mgr_aggression_and_asset_rpcs_use_login_corporation_and_inventory_data` passed 1/1 with 0 skipped. `Eve.Host.Tests` build passed with 0 warnings and 0 errors. Graphical-client verification was not performed.

## Remaining P1 rows

- RPC-01244–RPC-01247: `GetCorporationIDForCharacter` in NPC info-window paths (4 rows)
- RPC-01260–RPC-01261: `SearchAssets` (2 rows)

Total remaining: 6 rows.
