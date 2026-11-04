# corpmgr coverage handoff (cx4)

Completed RPC-01218–01241 in the coverage CSV. The Host test `Corporation_asset_locations_read_persisted_items_through_registered_host` runs `GetAssetInventory` and `GetAssetInventoryForLocation` through the registered `corpmgr` route, with PostgreSQL-backed corporation, office and item stores. It checks office/property/delivery location rowsets, item row descriptors and persisted office, impounded, property and delivery entries; a wrong location/bucket returns no rows.

Client callers and consumers checked across /home/user/eve-logic:
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_accounts.py:99-103,219-277` builds corporation asset locations; `:405-410` reads impounded type/stacksize, invalidates cache, then trashes items; `:422-423,470-490` reads rows and creates item/office nodes; `:482-493` filters hangar rows by flagID.
- `eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:1553-1565` consumes location IDs for contract delivery destinations.
- `eve/client/script/ui/shared/maps/starModeHandler.py:659-673`, `eve/client/script/ui/shared/mapView/mapViewColorHandler.py:704`, and `eve/client/script/ui/shared/mapView/filters/mapFilterCorpAssets.py:15,88-101` group/filter assets by location and solar system.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_votes.py:440-445` combines office and impounded rowsets, then filters hangars by flagID.
- Cache invalidation consumers: `eve/client/script/ui/shared/assets/assetSafetySvc.py:59`, `eve/client/script/ui/services/corporation/officeManager.py:120`, `eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:293`.

EveJS 261028.1: `server/src/services/corporation/corpmgrService.js:579-597,599-619` authorizes and caches these calls; `server/src/services/corporation/corpAssetState.js:388-414,448-471,473-477` builds matching location and item CRowsets. eve.rs matches these row shapes; no defect surfaced.

Local checks passed:
- Host.Tests build: 0 warnings, 0 errors.
- Focused Host wire test: 1 passed, 0 failed, 0 skipped, using eve_p4 and db-lock.sh.
- Neighbor CorpMgr RPC test: 1 passed, 0 failed, 0 skipped, using eve_p4 and db-lock.sh.
- Commit `59b656e9` is on main. Its selected Actions test passed (run `41097371465`); push CI run `41097343466` remains in progress.

Remaining P1 IMPLEMENTED-UNTESTED rows: RPC-01244–01247 `GetCorporationIDForCharacter`; RPC-01260–01261 `SearchAssets`. RPC-01242–01243 and RPC-01248–01249 are already covered on main.
