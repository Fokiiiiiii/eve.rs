# corpmgr coverage handoff (cx4)

Completed RPC-01218–01233 (`GetAssetInventory`). Added `Corporation_asset_locations_read_persisted_items_through_registered_host` in `dotnet/tests/Eve.Host.Tests/CorporationAssetInventoryHostWireTests.cs`, a registered Host call using PostgreSQL-backed corporation, office and item stores. It checks office/property/delivery location rowsets, CRowset descriptors and persisted item rows. The 16 matching CSV rows now cite this test.

Client callers and consumers checked across /home/user/eve-logic:
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_accounts.py:99-103,219-277` reads the response and builds asset tree/options.
- `eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:1553-1565` consumes location IDs for contract delivery destinations.
- `eve/client/script/ui/shared/maps/starModeHandler.py:659-673`, `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:704`, and `eve/client/script/ui/shared/mapView/filters/mapFilterCorpAssets.py:15,88-101` group/filter assets by location and solar system.
- Cache invalidation paths for related location reads: `eve/client/script/ui/shared/neocom/corporation/corp_ui_accounts.py:405-422,470`, `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:440-441`, `eve/client/script/ui/services/assetSafetySvc.py:59`, `eve/client/script/ui/services/officeManager.py:120`, `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:293`. These are relevant next to GetAssetInventoryForLocation.

EveJS 261028.1 behavior checked: `server/src/services/corporation/corpmgrService.js:579-597` authorizes and caches the inventory response; `server/src/services/corporation/corpAssetState.js:388-414,473-477` builds a CRowset from location buckets with locationID/solarsystemID/typeID and delivery itemCount.

Local checks passed:
- Host.Tests build: 0 warnings, 0 errors.
- Focused Host wire test: 1 passed, 0 failed, 0 skipped, using eve_p4 and db-lock.sh.
- Neighbor CorpMgr RPC test: 1 passed, 0 failed, 0 skipped, using eve_p4 and db-lock.sh.

Remaining P1 rows: RPC-01234–01241 GetAssetInventoryForLocation; RPC-01242–01247 GetCorporationIDForCharacter; RPC-01248–01249 GetCorporations; RPC-01260–01261 SearchAssets. Confirm latest main CI is green, commit/push this group, and wait for both pushed CI workflows before starting another group.
