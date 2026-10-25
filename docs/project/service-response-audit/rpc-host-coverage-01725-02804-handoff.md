# Host RPC coverage handoff: RPC-01725–RPC-02804

Updated 2026-10-25.

## Client and EveJS evidence

- Inventory: `invCache.py:428-432` caches `GetItemDescriptor`; `godma.py:124-127` exposes `itemrd`; `invCache.py:1131-1165,1167-1201` consumes the header for inventory rowsets. EveJS `invBrokerService.js:13567-13571,9966-9971` builds it from row columns.
- Dynamic items: `dynamicItemSvc.py:21-37` decodes/caches `GetDynamicItemInfo` and reads `mutatorTypeID`/attributes; `dynamicItemSvc.py:43-44` creates mutations; `craftingWindow.py:232-256` calls Create, fetches info, and updates UI. EveJS `dynamicItemService.js:426-436,438-534` reads and persists mutation state.
- Encoded items: `menuFunctions.py:473-477` opens an encoded-item window only when `DecodeItem` returns a URL. EveJS `encodedItemsService.js:178-209` accepts `.png`/`.webm`.
- Item locks: `itemLocking.py:11-79` caches location maps, checks membership and invalidates/scatters changes; `corp_ui_accounts.py:509-595` renders locked locations. EveJS `itemLockingService.js:21-36` returns keyed records.
- Mutual wars: `mutualWarInvites.py:8-41` wraps all six calls; pending rows and blocked state are consumed, mutation returns ignored. EveJS `mutualWarInviteMgrService.js:87-108` filters by owner and sorts newest first.
- Asset safety: `assetSafetySvc.py:25-30` returns personal/corp rows; `assetSafetyControllers.py:10-17` consumes them. EveJS `structureAssetSafetyService.js:185-192` maps persisted wraps to a list.
- RPC-01742–01747, 01750, 01753–01754: V24.01 callers are `dungeonTracking.py:67-70`, `scenarioMgr.py:88-135`, and `dungeoneditor.py:276-326`. The tracking caller unpacks `(dungeonID, roomID, instanceID, dungeonValues)` and forwards these into the room-entry flow; scenario-manager calls bind `keeper.GetLevelEditor` and invoke its editor methods only when a truthy object is returned. The editor window’s `GetCurrentRoomID` directly chains `GetLevelEditor().GetCurrentlyEditedRoomID()`. EveJS `keeperService.js:987-990` resolves current dungeon data; `keeperService.js:997-1001` explicitly returns null for GetLevelEditor and notes the editor runtime is disabled. The editor bound-object operations have no EveJS runtime implementation. Therefore RPC-01743, 01750, 01753 and 01754 remain open; no successful editor state transition is represented by this change.
- RPC-01755–01760: all callers are `bountySvc.py:325-346` and `killRights.py:262-265`. Successful returns are ignored; the bounty service consumes named user errors to invalidate cached rights and refresh ship bounty UI. EveJS `killRightMgrService.js:98-168` validates ownership/sale, applies the suspect effect, publishes the owner notification and returns None.
- RPC-01773–01780: `localizationClient.py:26-30` sends the QA-wrap boolean and a localization hash dictionary; the client cache update consumes three per-language/label maps. EveJS `localizationServerService.js:31-47` stores the QA-wrap flag and returns a triple of empty dictionaries by default.
- RPC-01790–01791: `corp_ui_applications.py:890-899` reads each `characterID`, primes owner data and renders the invite candidates. EveJS `lookupSvcService.js:456-458` aliases this to character lookup.
- RPC-01794–01795: `searchOld.py:53-57` calls `LookupKnownLocationsByGroup`, extracts only `each.itemID`, then primes location data. The only direct V24.01 caller found in the whole client is `searchOld.py:54`; EveJS `lookupSvcService.js:341-351,555-557` builds the KeyVal location row and aliases this RPC to location-group lookup.
- RPC-01798–01799: `noneNPCAccountOwnerDialog.py:64-75` reads `ownerID`, primes owner data and builds selectable rows. EveJS `lookupSvcService.js:533-546` searches character and non-NPC corporation owners.

## eve.rs and wire

- Inventory: `InvBrokerService.GetItemDescriptor` at `dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:225-226` returns the shared header through the registered Host publisher. Other active Host services are `DynamicItemRpcService.cs`, `OperationalRpcServices.cs`, `CorpMgrRpcServices.cs`, and `StructureDirectoryService.cs`.
- Client transport adds `machoVersion` in `ServiceCallGPCS.py:221-225` and `ObjectCallGPCS.py:403-407`; Host tests send it.
- `InvBrokerDescriptorHostTests.cs`, `ServiceResponseBatchHostWireTests.cs`, `DynamicItemInfoHostWireTests.cs`, and `StructureAssetSafetyRpcTests.cs` use HostServer and registered services. They cover the descriptor, dynamic refusal/no-data, encoded URL, invite row/order/replies/arguments, asset-safety row/action/notification behavior, and empty locking results.
- This increment adds `FocusedRpc01742HostWireTests.cs`: HostServer dispatches the registered Core keeper/localization RPCs, a Core lookup service registered in the Host registry, and kill-right error responses. The calls include transport `machoVersion`.

## Verification and gaps

- At `72746bc0`, local Host.Tests build succeeded (0 warnings/errors), eight focused tests passed (0 skipped), and Actions run `40186144273` succeeded.
- At `20c354b6`, the dynamic info no-data test was added. The local build succeeded (0 warnings/errors), eight focused tests passed (0 skipped), and Actions run `40187369602` passed.
- Historical head `976dae96`: Actions run `40187931398` succeeded with no failed jobs. `ci-status.py failures 976dae96` reported no ci.yml run.
- For this increment, the Host.Tests project build succeeded (0 warnings/errors) and the keeper/localization wire test passed locally (1 passed, 0 skipped).
- Agent run `40204774520` on `6fd6d8e9` failed before tests (0 passed, 0 failed, 0 skipped) because its filter used the filename while the test methods belong to the existing partial `HostServerTests`.
- Corrected Agent run `40205284776` executed three tests: two passed, one failed, and none skipped. The failure was the Host wire assertion expecting PyStr for `itemName`; the Host-decoded KeyVal carries its Python `str` name and value as PyBytes. `LookupSvcService.BuildLocationRow` supplies the itemName field, and the V24.01 consumer reads only itemID. The Host assertion now checks PyBytes, matching the wire representation and EveJS KeyVal row.
- Release build of `dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj` passed with 0 errors; a concurrent duplicate build caused three MSB3026 file-copy retry warnings. The focused lookup Host test passed locally (1 passed, 0 failed, 0 skipped) with `EVE_PG_URL` unset. The corrected Agent run and push-gate run on the updated commit are pending.
- Successful dynamic mutation persistence and populated `GetDynamicItemInfo` data remain uncovered. In this increment, the dungeon editor’s bound-object methods, successful kill-right payment/activation and owner notification, database state, and graphical-client behavior were not exercised. No local PostgreSQL database was used. The audit generator misses the inventory publisher wrapper and shared wire-test helpers, so its CSV coverage remains stale.
