# Host RPC coverage handoff: RPC-01725–RPC-02804

Updated 2026-10-25.

## Verified client and EveJS behavior

- Inventory descriptors: `invCache.py:428-432` caches `GetItemDescriptor`; `godma.py:124-127` exposes it as `itemrd`. `invCache.py:1131-1165,1167-1201` builds rowsets and checks each inventory row against the descriptor. EveJS `invBrokerService.js:13567-13571,9966-9971` builds the descriptor from inventory row columns.
- Dynamic items: `dynamicItemSvc.py:21-37` decodes and caches `GetDynamicItemInfo`, then reads `mutatorTypeID` and `attributes`; `dynamicItemSvc.py:43-44` creates a mutation. `craftingWindow.py:232-256` calls Create, fetches mutation info, and updates the result UI. EveJS `dynamicItemService.js:426-436,438-534` reads dynamic info and performs/persists mutation changes.
- Encoded items: `menuFunctions.py:473-477` calls `DecodeItem` and opens the encoded-item window only for a returned URL. EveJS `encodedItemsService.js:178-209` accepts supported item resource URLs (`.png` and `.webm`).
- Item locking: `itemLocking.py:11-79` caches the location-to-item map, tests membership, and invalidates/scatters on lock changes; `corp_ui_accounts.py:509-595` consumes locked locations to render lockdown groupings. EveJS `itemLockingService.js:21-36` returns keyed item records.
- Mutual war invites: `mutualWarInvites.py:8-41` is the client wrapper for all six methods. Pending invite rows are returned to callers; the other mutation RPC results are ignored, while the block query result is consumed. EveJS `mutualWarInviteMgrService.js:87-108` filters invite rows by current owner and sorts newest first.
- Asset safety: `assetSafetySvc.py:25-30` returns the character/corporation calls; `assetSafetyControllers.py:10-17` consumes those row lists. EveJS `structureAssetSafetyService.js:185-192` returns persisted safety wraps as a list.

## eve.rs wiring and transport

- Inventory: `InvBrokerService.cs:326-327` returns `InventoryWire.DescriptorHeader`; `InventoryRpc.cs:43-46` creates it, `IndustryInventoryChangePublisher.cs:16-26` forwards its route, and `CharacterServiceList.cs:47-70` registers the wrapper.
- Other services are in the active Host composition: `DynamicItemRpcService.cs` (`dynamicItemService`), `OperationalRpcServices.cs` (`encodedItems`), `CorpMgrRpcServices.cs` (`mutualWarInviteMgr`, `itemLocking`), and `StructureDirectoryService.cs` (`structureAssetSafety`).
- Client transport appends `machoVersion` in `ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`; the wire tests include it.

## Tests and remaining scope

- `InvBrokerDescriptorHostTests.cs` sends a packet through HostServer with the actual registered publisher and broker; the static reply matches the shared inventory header.
- `ServiceResponseBatchHostWireTests.cs` sends HostServer packets through actual service registrations. It checks DynamicItemService's missing-character refusal; encoded URL; sorted/filtered invite rows, integer block state, None replies and callback argument order; personal/corporate asset-safety row fields and owner IDs; and empty locking collections without a corporation context.
- Local Host.Tests compile check succeeded (0 warnings, 0 errors). The five focused service tests passed (0 skipped); the descriptor focused test passed (1/1, 0 skipped). The pushed head `74208f93a950` passed Actions run `40181736937`. Push CI run `40181728597` was still in progress at this update.
- The create test checks the no-character refusal only. Successful mutation persistence and a populated `GetDynamicItemInfo` reply are not covered here. No database or graphical-client run was performed. The generated coverage scanner still fails to follow the inventory publisher wrapper and currently labels RPC-01725/01726 MISSING despite the real Host registration/test; its matcher also misses the shared wire-test helpers.
