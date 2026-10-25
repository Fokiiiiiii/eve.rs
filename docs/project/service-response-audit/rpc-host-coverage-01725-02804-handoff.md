# Host RPC coverage handoff: RPC-01725–RPC-02804

Updated 2026-10-25.

## Verified client and EveJS behavior

- Inventory descriptor: `invCache.py:428-432` caches `GetItemDescriptor`; `godma.py:124-127` exposes `itemrd`; `invCache.py:1131-1165,1167-1201` consumes its header in inventory rowsets. EveJS `invBrokerService.js:13567-13571,9966-9971` builds that descriptor from row columns.
- Dynamic items: `dynamicItemSvc.py:21-37` decodes/caches `GetDynamicItemInfo` and reads `mutatorTypeID` and `attributes`; `dynamicItemSvc.py:43-44` creates mutations. `craftingWindow.py:232-256` calls Create, fetches mutation info, and updates the result UI. EveJS `dynamicItemService.js:426-436,438-534` reads and persists mutation state.
- Encoded items: `menuFunctions.py:473-477` calls `DecodeItem` and opens the encoded-item window only for a URL. EveJS `encodedItemsService.js:178-209` accepts `.png` and `.webm` URLs.
- Item locking: `itemLocking.py:11-79` caches the location-to-item map, checks membership, and invalidates/scatters on lock changes; `corp_ui_accounts.py:509-595` renders locked-location groups. EveJS `itemLockingService.js:21-36` returns keyed item records.
- Mutual war invites: `mutualWarInvites.py:8-41` wraps all six methods; it returns pending rows, consumes blocked state, and ignores mutation returns. EveJS `mutualWarInviteMgrService.js:87-108` filters rows by owner and sorts newest first.
- Asset safety: `assetSafetySvc.py:25-30` returns character/corporation calls; `assetSafetyControllers.py:10-17` consumes row lists. EveJS `structureAssetSafetyService.js:185-192` returns persisted wraps.

## eve.rs wiring and transport

- Inventory: `InvBrokerService.GetItemDescriptor` at `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:225-226` returns the shared header through the registered Host publisher.
- Active Host services: `DynamicItemRpcService.cs` (`dynamicItemService`), `OperationalRpcServices.cs` (`encodedItems`), `CorpMgrRpcServices.cs` (`mutualWarInviteMgr`, `itemLocking`), and `StructureDirectoryService.cs` (`structureAssetSafety`).
- Client transport adds `machoVersion` in `ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`; the tests send it.

## Tests and current state

- `InvBrokerDescriptorHostTests.cs` checks the registered inventory route through HostServer.
- `ServiceResponseBatchHostWireTests.cs` checks dynamic-item refusal, encoded URL, invite filtering/order, blocked state and callback order, asset-safety row fields/owner IDs, and empty item-locking collections, all through HostServer.
- `DynamicItemInfoHostWireTests.cs` checks the no-data `GetDynamicItemInfo` reply for itemID 0 through HostServer.
- `StructureAssetSafetyRpcTests.cs` exercises registered Host mutation replies, notification order, wrap-name and delivery-target result shapes.
- At `72746bc0`, local Host.Tests built with 0 warnings/errors and all eight focused tests passed with 0 skipped. Actions run `40186144273` succeeded. The preceding Actions failure `40184910516` was caused by the duplicate import after two concurrent commits added the same using; the duplicate was removed.
- Successful dynamic mutation persistence and a populated `GetDynamicItemInfo` reply remain uncovered. No database or graphical-client run was performed. The coverage generator misses the inventory publisher wrapper and shared wire-test helpers, so its CSV status is stale.
