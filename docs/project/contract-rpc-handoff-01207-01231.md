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

The client wrappers at `contracts.py:711-719` preserve distinct replies: `GetItemsInContainer` sorts item rows by type name, `GetNumItemsInContainers` returns a mapping consumed as item counts, and `GetItemsInDockableLocation` returns the dockable inventory set. Full-tree consumers are `createContractWnd.py:92-96, 194-198, 933-937, 1320-1324, 1343-1347, 1647-1651`: inventory selection/revalidation reads the station item set; multi-contract creation sums container counts; per-container inspection uses each child row's `itemID`, `typeID`, and `stacksize`. EveJS v0.12.9 handles item rows via `contractProxyService.js:869-875, 882-888`; its container counter at `648-677` returns only readable containers with positive contents, using `canReadContractContainer` before counting. The existing Host inventory service centralizes hangar and corporation visibility in `InvBrokerService.ContentsAsync` (`dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:237-274`) and its corporation access path (`dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:196-235`). These RPCs still need a real Host wire test and authoritative inventory wiring.

## Verification

The Host regression is `Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs`. It calls RPCs over a socket-backed Host with real CoreServices registration. It checks batch mutation results; counts including a 100-row page boundary; login rowsets; escrow fields; contract list bundles; current/expired rows; accepted row; CreateContract's ContractStruct reply; completion/deletion booleans; and CollectMyPageInfo fields.

- Core build after CollectMyPageInfo and assignment correction: passed, zero warnings/errors.
- Host build after assignment correction: passed, zero warnings/errors.
- Agent test 40101123016 for pushed tip 5273c3dc passed with no failed jobs.
- Latest code commit a30181a2, including the CreateContract wire case, was pushed with origin/main merged at 24cec7ac; Agent test 40101711525 passed with no failed jobs.
- Handoff commit 81eb74e8 was merged with origin/main and pushed at c79cd86b. Push-triggered ci.yml run lookup for c79cd86b found no run. Focused Agent test 40102279877 for the Host RPC registry inventory on c79cd86b is in progress.
- Focused Host test after CollectMyPageInfo and assignment correction: passed (1 passed, 0 skipped).
- Focused Host test after adding the CreateContract ContractStruct case: passed (1 passed, 0 skipped).
- No PostgreSQL connection was used.
- Agent test run 40098916457 for push 755a38a2 passed; Agent test run 40099756130 for push 23db167f passed.
- Local verification was run on code commit 835f8a1f before the origin/main merge; pushed tip after that merge was d89e97b2. The focused Actions runs listed above passed. Push-triggered ci.yml run 40098905958 for 755a38a2 completed with failures: the Host RPC inventory expected 1385 methods but found 1393; `test_a_corporation_unanchors_and_scoops_its_control_tower`, `CharacterContactNotepadHostTests.Bound_inventory_add_accepts_client_quantity_and_transport_kwargs_and_moves_the_item`, and `ConfigServiceTests.Batched_config_lookups_refuse_more_than_500_raw_ids` also failed. Later pushes include fixes in undock, inventory, and config paths. Runs 40101115215 (5273c3dc) and 40101705581 (24cec7ac) were active at last poll; no PostgreSQL connection was used locally.

## Remaining assigned rows

Still missing: RPC-01214, RPC-01216–RPC-01219, RPC-01221–RPC-01223, and RPC-01230. Covered by the Host wire test: RPC-01207, RPC-01208, RPC-01213, RPC-01215, RPC-01220, RPC-01224–RPC-01229, and RPC-01231. Remaining scenario coverage includes CompleteContract's other target statuses, CreateContract's valid shapes, corporate bids and lists, and the alternate expired-list case. For all remaining rows, the full caller/consumer paths and EveJS comparisons still need client file:line evidence. EveJS has deliberate `false` or `None` handlers for `FinishAuction`, `DeleteNotification`, `DeleteContractNotification`, and `GM_ExpireContract` at `contractProxyService.js:859-862, 890-898, 912-915`; do not copy these replies without confirming the V24 client behavior.
