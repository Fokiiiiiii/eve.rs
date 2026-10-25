# Host RPC coverage handoff: RPC-01725–RPC-02804

Updated 2026-10-25.

## Client and EveJS evidence

- Inventory: `invCache.py:428-432` caches `GetItemDescriptor`; `godma.py:124-127` exposes `itemrd`; `invCache.py:1131-1165,1167-1201` consumes the header for inventory rowsets. EveJS `invBrokerService.js:13567-13571,9966-9971` builds it from row columns.
- Dynamic items: `dynamicItemSvc.py:21-37` decodes/caches `GetDynamicItemInfo` and reads `mutatorTypeID`/attributes; `dynamicItemSvc.py:43-44` creates mutations; `craftingWindow.py:232-256` calls Create, fetches info, and updates UI. EveJS `dynamicItemService.js:426-436,438-534` reads and persists mutation state.
- Encoded items: `menuFunctions.py:473-477` opens an encoded-item window only when `DecodeItem` returns a URL. EveJS `encodedItemsService.js:178-209` accepts `.png`/`.webm`.
- Item locks: `itemLocking.py:11-79` caches location maps, checks membership and invalidates/scatters changes; `corp_ui_accounts.py:509-595` renders locked locations. EveJS `itemLockingService.js:21-36` returns keyed records.
- Mutual wars: `mutualWarInvites.py:8-41` wraps all six calls; pending rows and blocked state are consumed, mutation returns ignored. EveJS `mutualWarInviteMgrService.js:87-108` filters by owner and sorts newest first.
- Asset safety: `assetSafetySvc.py:25-30` returns personal/corp rows; `assetSafetyControllers.py:10-17` consumes them. EveJS `structureAssetSafetyService.js:185-192` maps persisted wraps to a list.

## eve.rs and wire

- Inventory: `InvBrokerService.GetItemDescriptor` at `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:225-226` returns the shared header through the registered Host publisher. Other active Host services are `DynamicItemRpcService.cs`, `OperationalRpcServices.cs`, `CorpMgrRpcServices.cs`, and `StructureDirectoryService.cs`.
- Client transport adds `machoVersion` in `ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`; tests send it.
- `InvBrokerDescriptorHostTests.cs`, `ServiceResponseBatchHostWireTests.cs`, `DynamicItemInfoHostWireTests.cs`, and `StructureAssetSafetyRpcTests.cs` use HostServer and registered services. They cover the descriptor, dynamic refusal/no-data, encoded URL, invite row/order/replies/arguments, asset-safety row/action/notification behavior, and empty locking results.

## Verification and gaps

- At `72746bc0`, local Host.Tests build succeeded (0 warnings/errors), eight focused tests passed (0 skipped), and Actions run `40186144273` succeeded.
- At `20c354b6`, the dynamic info no-data test was added. The local build succeeded (0 warnings/errors), eight focused tests passed (0 skipped), and Actions run `40187369602` passed.
- Current main head `976dae96`: Actions run `40187931398` succeeded with no failed jobs. `ci-status.py failures 976dae96` reported no ci.yml run.
- Successful dynamic mutation persistence and populated `GetDynamicItemInfo` data remain uncovered. No database or graphical-client run was performed. The audit generator misses the inventory publisher wrapper and shared wire-test helpers, so its CSV coverage remains stale.
