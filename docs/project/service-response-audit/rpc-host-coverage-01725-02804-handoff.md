# Host RPC coverage handoff: RPC-01725–RPC-02804

Updated 2026-10-25.

## Verified client and EveJS behavior

- Inventory descriptors: `invCache.py:428-432` caches `GetItemDescriptor`; `godma.py:124-127` exposes it as `itemrd`. `invCache.py:1131-1165,1167-1201` builds rowsets and checks each inventory row against the descriptor. EveJS `invBrokerService.js:13567-13571,9966-9971` builds the descriptor from inventory row columns.
- Dynamic items: `dynamicItemSvc.py:21-37` decodes/caches `GetDynamicItemInfo` and reads `mutatorTypeID` and `attributes`; `dynamicItemSvc.py:43-44` creates a mutation. `craftingWindow.py:232-256` calls Create, fetches mutation info, and updates the result UI. EveJS `dynamicItemService.js:426-436,438-534` reads dynamic info and performs/persists mutation changes.
- Encoded items: `menuFunctions.py:473-477` calls `DecodeItem` and opens the encoded-item window only for a returned URL. EveJS `encodedItemsService.js:178-209` accepts supported item resource URLs (`.png` and `.webm`).
- Item locking: `itemLocking.py:11-79` caches the location-to-item map, tests membership, and invalidates/scatters on lock changes; `corp_ui_accounts.py:509-595` consumes locked locations to render lockdown groupings. EveJS `itemLockingService.js:21-36` returns keyed item records.
- Mutual war invites: `mutualWarInvites.py:8-41` wraps all six RPCs. Pending rows are returned to callers; mutation returns are ignored, while the block query result is consumed. EveJS `mutualWarInviteMgrService.js:87-108` filters rows by owner and sorts newest first.
- Asset safety: `assetSafetySvc.py:25-30` returns character/corporation calls; `assetSafetyControllers.py:10-17` consumes those row lists. EveJS `structureAssetSafetyService.js:185-192` returns persisted safety wraps as a list.

## eve.rs wiring and transport

- Inventory: current `InvBrokerService.GetItemDescriptor` at `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:225-226` returns the shared header. Host composition registers it through the inventory notification publisher.
- Other services in active Host composition: `DynamicItemRpcService.cs` (`dynamicItemService`), `OperationalRpcServices.cs` (`encodedItems`), `CorpMgrRpcServices.cs` (`mutualWarInviteMgr`, `itemLocking`), and `StructureDirectoryService.cs` (`structureAssetSafety`).
- Client transport adds `machoVersion` in `ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`; wire requests include it.

## Tests and remaining scope

- `InvBrokerDescriptorHostTests.cs` sends a packet through HostServer with the actual registered publisher and broker; the static reply matches the shared inventory header.
- `ServiceResponseBatchHostWireTests.cs` uses real HostServer registrations to check DynamicItemService's missing-character refusal, encoded URL, sorted and filtered invite rows, integer blocked state, None replies and callback argument order, personal/corporate safety row fields and owner IDs, and empty item-locking collections without a corporation.
- Origin/main also added `StructureAssetSafetyRpcTests.cs`, a HostServer test for asset safety mutations, result shapes and notification order. Its initial compile failed because it referenced `MachoMessage` without importing `Eve.Macho`; this worktree fix adds the missing using.
- Local Host.Tests build at `24a2628c` succeeded with 0 warnings/errors and six focused tests passed with 0 skipped. Actions run `40182343738` passed on that revision. The later `b464cc84` Actions job `40183379503` failed in the Release solution build; local reproduction found the missing namespace above. After the fix, local Host.Tests build succeeded with 0 warnings/errors and seven focused Host tests passed with 0 skipped. The fix is ready to commit and recheck on Actions.
- The Create test covers missing-character refusal only. Successful mutation persistence and a populated `GetDynamicItemInfo` reply remain uncovered. No database or real graphical-client run was performed. The generated coverage scanner still misses the inventory publisher wrapper and shared wire-test helpers, so its CSV status remains stale.
