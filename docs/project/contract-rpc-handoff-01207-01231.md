# Contract Proxy RPC handoff: RPC-01207–RPC-01231

## Implemented increments

### RPC-01215: DeleteMultipleContracts

`dotnet/src/Eve.Services.Core/ContractProxyBatchService.cs` parses a list of positive client-range contract IDs, calls the existing `IContractWriteModel.DeleteContractAsync` for each with the active character, corporation, and Contract Manager authority, and returns the V24 pair `(deletedIDs, failedIDs)`. Per-contract domain refusals are reported in `failedIDs`; storage and unexpected errors reach the caller. Invalid list shape and IDs outside the client integer range are refused before mutations.

The whole-client path is `ContractsSvc.DeleteMultipleContracts` at eve-logic `eve/client/script/ui/shared/neocom/contracts/contracts.py:453-474`, called by `ContractEntry.DeteleMultiple` at `eve/client/script/ui/shared/neocom/contracts/contractentry.py:185-188`. The wrapper unpacks both lists at line 461, builds its success/failure message at lines 462-468, raises `OnDeleteContracts` for deleted IDs at line 470, and clears the cache at line 473. EveJS v0.12.9 registers the method at `server/src/services/_other/contractProxyService.js:845-852`; `server/src/services/contracts/contractRuntimeState.js:3440-3456` preserves separate deleted and failed lists for each item.

Production Host wiring supplies `PersistenceContractWriteModel` from `dotnet/src/Eve.Host/Hosting/HostComposition.cs:92-126`. That writer owns the transaction, escrow return, authority check, expiry cancellation, and outbox notification for each contract.

### RPC-01231: NumOutstandingContracts

The RPC counts active personal and corporation issued contracts through `IContractReadModel.GetContractListForOwnerAsync`, paging at its 100-row limit, and returns the four client KeyVal fields. eve-logic `ContractsSvc.NumOutstandingContracts` calls the proxy at `contracts.py:688-690`; `CreateContractWnd.GetNumContractsAndLimits` at `createContractWnd.py:1182-1223` reads `myCorpTotal`, `nonCorpForMyCorp`, `myCharTotal`, or `nonCorpForMyChar` according to corporation and internal-assignee rules. EveJS v0.12.9 returns those same four fields in `server/src/services/_other/contractProxyService.js:322-338`; `collectMyPageInfo` counts outstanding contracts by personal or corporation issuer in `server/src/services/contracts/contractRuntimeState.js:3960-4036`.

### RPC-01208: CollectMyPageInfo

The Host RPC reads paged owner-scoped outstanding lists plus existing current-contract and bid read models. It reads attention state and assignment details from the existing `GetLoginInfoAsync` query, whose assignment selection includes character, corporation, and alliance assignees. The login DTO carries issuer corporation, assignee, and type for the overview tuple while the `GetLoginInfo` wire rowset remains two columns. It returns EveJS's fields: personal/corporate open counts, personal/corporate in-progress and bid counts, attention counts, and the four-column outstanding assignment tuples. Eve-logic `ContractsSvc.CollectMyPageInfo` calls the proxy and derives skill-based remaining limits from `numOutstandingContractsNonCorp` and `numOutstandingContractsForCorp` at `contracts.py:729-740`. `StartPagePanel.Init` consumes outstanding assignments, attention, bid and in-progress counts at `contractPanels.py:66-162`. EveJS aggregation is `contractRuntimeState.js:3960-4036`; it returns the same raw statistics and assignment columns. The exact reply field set and sampled counts are checked through the registered Host route by the focused wire test.

### RPC-01212: CreateContract wire regression

The Host route receives the V24 `contracts.contractStruct.ContractStruct` object and returns the created contract ID list. Eve-logic's wrapper is `ContractsSvc.CreateContract` at `eve/client/script/ui/shared/neocom/contracts/contracts.py:552-560`; `CreateContractWnd.CreateContract` builds its 17 fields and consumes the returned IDs at `createContractWnd.py:853-919`. EveJS normalizes the object, calls the runtime writer, and returns a list at `server/src/services/_other/contractProxyService.js:807-821`, with creation state written by `contractRuntimeState.js:1619-1632`. The socket-backed Host test sends a serialized 17-field ContractStruct and asserts the returned ID and authoritative writer request fields.

## Additional full-tree caller evidence

The client wrappers at `contracts.py:711-719` preserve distinct replies: `GetItemsInContainer` sorts item rows by type name, `GetNumItemsInContainers` returns a mapping consumed as item counts, and `GetItemsInDockableLocation` returns the dockable inventory set. Full-tree consumers are `createContractWnd.py:92-96, 194-198, 933-937, 1320-1324, 1343-1347, 1647-1651`: inventory selection/revalidation reads the station item set; multi-contract creation sums container counts; per-container inspection uses each child row's `itemID`, `typeID`, and `stacksize`. EveJS v0.12.9 handles item rows via `contractProxyService.js:869-875, 882-888`; its container counter at `648-677` returns only readable containers with positive contents, using `canReadContractContainer` before counting. The existing Host inventory service centralizes hangar and corporation visibility in `InvBrokerService.ContentsAsync` (`dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:237-274`) and its corporation access path (`dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:196-235`). These three RPCs now use the Host-registered contract service and the existing authoritative InvBroker inventory provider; the socket-backed Host test checks each reply's client wire shape.

## Verification

The Host regression is `Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs`. It calls RPCs over a socket-backed Host with real CoreServices registration. It checks batch mutation results; counts including a 100-row page boundary; login rowsets; escrow fields; contract list bundles; current/expired rows; accepted row; CreateContract's ContractStruct reply; completion/deletion booleans; and CollectMyPageInfo fields.

- Core build after CollectMyPageInfo and assignment correction: passed, zero warnings/errors.
- Host build after assignment correction: passed, zero warnings/errors.
- Agent test 40101123016 for pushed tip 5273c3dc passed with no failed jobs.
- Latest code commit a30181a2, including the CreateContract wire case, was pushed with origin/main merged at 24cec7ac; Agent test 40101711525 passed with no failed jobs.
- Handoff commit 81eb74e8 was merged with origin/main and pushed at c79cd86b; subsequent origin/main fixes were merged and pushed at cc34becb. No push-triggered ci.yml run was found for cc34becb. Agent test 40102279877 failed after the selected-test command started, without an assertion or test summary in the returned log tail; follow-up run 40102299091 on SHA 2aa887b1 passed with no failed jobs. Current main updates were merged through 2856a206; the Host RPC inventory expectation is now 1394. Push-triggered ci.yml run 40102690691 for 2856a206 is queued.
- Focused Host test after CollectMyPageInfo and assignment correction: passed (1 passed, 0 skipped).
- Focused Host test after adding the CreateContract ContractStruct case: passed (1 passed, 0 skipped).
- No PostgreSQL connection was used.
- Agent test run 40098916457 for push 755a38a2 passed; Agent test run 40099756130 for push 23db167f passed.
- Local verification was run on code commit 835f8a1f before the origin/main merge; pushed tip after that merge was d89e97b2. The focused Actions runs listed above passed. Push-triggered ci.yml run 40098905958 for 755a38a2 completed with failures: the Host RPC inventory expected 1385 methods but found 1393; `test_a_corporation_unanchors_and_scoops_its_control_tower`, `CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item`, and `ConfigServiceTests.Batched_config_lookups_refuse_more_than_500_raw_ids` also failed. Later origin/main commits updated the Host inventory expectation and changed the undock, inventory, and config paths; selected Host inventory test 40102299091 passed. Runs 40101115215 (5273c3dc) and 40101705581 (24cec7ac) were still active at last poll; no local PostgreSQL connection was used.

## Remaining assigned rows

Still missing: RPC-01214, RPC-01216–RPC-01219, RPC-01221–RPC-01223, and RPC-01230. Covered by the Host wire test: RPC-01207, RPC-01208, RPC-01213, RPC-01215, RPC-01220, RPC-01224–RPC-01229, and RPC-01231. Remaining scenario coverage includes CompleteContract's other target statuses, CreateContract's valid shapes, corporate bids and lists, and the alternate expired-list case. For all remaining rows, the full caller/consumer paths and EveJS comparisons still need client file:line evidence. EveJS has deliberate `false` or `None` handlers for `FinishAuction`, `DeleteNotification`, `DeleteContractNotification`, and `GM_ExpireContract` at `contractProxyService.js:859-862, 890-898, 912-915`; do not copy these replies without confirming the V24 client behavior.


### RPC-01214/01216/01217: Contract inventory reads

`GetItemsInDockableLocation`, `GetItemsInContainer`, and `GetNumItemsInContainers` now call the production `InvBrokerService` through `IContractInventoryReader`. The provider checks the docked station, owner, source flag, corporation division permission, and selected division, then serializes rows from the pinned SDE-backed type catalog. The Host replies are respectively a `__builtin__.set` of packed rows, a list of packed rows, and a mapping containing only positive item-row counts.

The client wrappers are eve-logic `eve/client/script/ui/shared/neocom/contracts/contracts.py:711-719`. Full-tree consumers are `createContractWnd.py:92-96,194-198,933-937,1320-1324,1343-1347,1647-1651`: the dockable set feeds station inventory selection/revalidation; the count map is summed during multi-contract creation; container rows provide item IDs, type IDs, and stack sizes. EveJS v0.12.9 checks location, owner, and allowed division in `server/src/services/_other/contractProxyService.js:600-620`, selects and sorts container rows at `:622-646`, counts only readable non-empty containers at `:648-677`, and packs these replies at `:869-888`. The entire source search found no other consumers of these wrappers.

The Host regression `Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs` makes socket RPC calls through CoreServices registration, verifies the set/list/dictionary reply types, row type IDs, positive-count filtering, and provider arguments. Local Host project build passed before the latest main merge; the focused Actions result will be added after dispatch.

### Production registration follow-up

Push gate 898 exposed that character services and core services are assembled separately: the contract proxy is registered from `CoreServices.Create` in `HostComposition.cs`, while the inventory broker comes from `CharacterServicesComposition`. The production Host now passes that same registered broker as `contractInventoryReader` at CoreServices creation, before service registration. The socket-backed test confirms the response columns as itemID at packed-row index 0 and the SDE typeID at index 1; Action runs 558/898 exposed and drove this correction. Focused verification on the corrected Host wiring is pending its next Actions run.


## 2026-10-25 completion status and corporation-list wire coverage

The Host socket regression now sends `CompleteContract` with the V24 target statuses finished (4), failed (7), and rejected (6), checks the boolean replies, and records the status passed to the write model. Whole-tree eve-logic wrappers are `ContractsSvc.CompleteContract`, `FailContract`, and `RejectContract` at `contracts.py:475-489,490-521,523-547`; the detail window checks each returned boolean before reinitializing at `contractsDetailsWnd.py:547-548,570-571,537-538,580-581`. EveJS v0.12.9 forwards status in `contractProxyService.js:830-837`; `contractRuntimeState.js:3393-3420` maps rejected, finished, and failed to their terminal operations and returns false for other statuses.

The socket test also checks personal and corporation input flags through registered `CoreServices` for bids, current contracts, and expired contracts, and checks each V24 bundle's `contracts` list. Whole-tree client paths: `contracts.py:756-767` forwards `forCorp` to GetMyBids, which `contractPanels.py:414-418` selects from the owner and reads through `.list`; `contracts.py:783-794` caches and returns the current-contract bundle's `.list` (the full-tree search found no other caller); `contracts.py:744-754` fetches personal and corporation expired lists separately, and `contractPanels.py:423-427` selects `mySelf` or `myCorp` by owner. EveJS v0.12.9 uses `contractProxyService.js:732-735` for the intentionally empty bid bundle, filters current contracts by both flags at `:737-744`, and filters expired contracts by corporation at `:726-730`. The Host path uses the registered read model to return persisted query results.

The full focused Release Host run passed after increasing only the synthetic dispatcher budget to 100,000; production budget behavior was unchanged. The test covers personal/corporate bids and current/expired lists plus completion statuses 4, 7, and 6, all through registered CoreServices over the Host socket. No PostgreSQL database was used.
