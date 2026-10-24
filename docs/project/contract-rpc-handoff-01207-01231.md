# Contract Proxy RPC handoff: RPC-01207–RPC-01231

## Implemented increments

### RPC-01215: DeleteMultipleContracts

`dotnet/src/Eve.Services.Core/ContractProxyBatchService.cs` parses a list of positive client-range contract IDs, calls the existing `IContractWriteModel.DeleteContractAsync` for each with the active character, corporation, and Contract Manager authority, and returns the V24 pair `(deletedIDs, failedIDs)`. Per-contract domain refusals are reported in `failedIDs`; storage and unexpected errors reach the caller. Invalid list shape and IDs outside the client integer range are refused before mutations.

The whole-client path is `ContractsSvc.DeleteMultipleContracts` at eve-logic `eve/client/script/ui/shared/neocom/contracts/contracts.py:453-474`, called by `ContractEntry.DeteleMultiple` at `eve/client/script/ui/shared/neocom/contracts/contractentry.py:185-188`. The wrapper unpacks both lists at line 461, builds its success/failure message at lines 462-468, raises `OnDeleteContracts` for deleted IDs at line 470, and clears the cache at line 473. EveJS v0.12.9 registers the method at `server/src/services/_other/contractProxyService.js:845-852`; `server/src/services/contracts/contractRuntimeState.js:3440-3456` preserves separate deleted and failed lists for each item.

Production Host wiring supplies `PersistenceContractWriteModel` from `dotnet/src/Eve.Host/Hosting/HostComposition.cs:92-126`. That writer owns the transaction, escrow return, authority check, expiry cancellation, and outbox notification for each contract.

### RPC-01231: NumOutstandingContracts

The RPC counts active personal and corporation issued contracts through `IContractReadModel.GetContractListForOwnerAsync`, paging at its 100-row limit, and returns the four client KeyVal fields. eve-logic `ContractsSvc.NumOutstandingContracts` calls the proxy at `contracts.py:688-690`; `CreateContractWnd.GetNumContractsAndLimits` at `createContractWnd.py:1182-1223` reads `myCorpTotal`, `nonCorpForMyCorp`, `myCharTotal`, or `nonCorpForMyChar` according to corporation and internal-assignee rules. EveJS v0.12.9 returns those same four fields in `server/src/services/_other/contractProxyService.js:322-338`; `collectMyPageInfo` counts outstanding contracts by personal or corporation issuer in `server/src/services/contracts/contractRuntimeState.js:3960-4036`.

### RPC-01208: CollectMyPageInfo

The Host RPC reads owner-scoped outstanding and attention lists plus existing current-contract and bid read models. It returns EveJS's fields: personal/corporate open counts, personal/corporate in-progress and bid counts, attention counts, and the four-column outstanding assignment tuples. Eve-logic `ContractsSvc.CollectMyPageInfo` calls the proxy and derives skill-based remaining limits from `numOutstandingContractsNonCorp` and `numOutstandingContractsForCorp` at `contracts.py:729-740`. `StartPagePanel.Init` consumes outstanding assignments, attention, bid and in-progress counts at `contractPanels.py:66-162`. EveJS aggregation is `contractRuntimeState.js:3960-4036`; it returns the same raw statistics and assignment columns. The exact reply field set and sampled counts are checked through the registered Host route by the focused wire test.

## Verification

The Host regression is `Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs`. It calls RPCs over a socket-backed Host with real CoreServices registration. It checks batch mutation results; counts including a 100-row page boundary; login rowsets; escrow fields; contract list bundles; current/expired rows; accepted row; completion/deletion booleans; and CollectMyPageInfo fields.

- Core build after CollectMyPageInfo: passed, zero warnings/errors.
- Focused Host test after CollectMyPageInfo: passed (1 passed, 0 skipped).
- No PostgreSQL connection was used.
- Agent test run 40098916457 for push 755a38a2 passed; Agent test run 40099756130 for push 23db167f passed.
- Local verification was run on code commit 835f8a1f before the origin/main merge; pushed tip after that merge was d89e97b2. The focused Actions runs listed above passed. Push-triggered ci.yml run 40098905958 remained in progress at last check; no ci.yml run was found for 23db167f by `ci-status.py failures`.

## Remaining assigned rows

Still missing: RPC-01212, RPC-01214, RPC-01216–RPC-01219, RPC-01221–RPC-01223, and RPC-01230. Covered by the Host wire test: RPC-01207, RPC-01208, RPC-01213, RPC-01215, RPC-01220, RPC-01224–RPC-01229, and RPC-01231. Remaining scenario coverage includes CompleteContract's other target statuses, CreateContract's valid shapes, corporate bids and lists, and the alternate expired-list case. For all remaining rows, the full caller/consumer paths and EveJS comparisons still need client file:line evidence. EveJS has deliberate `false` or `None` handlers for `FinishAuction`, `DeleteNotification`, `DeleteContractNotification`, and `GM_ExpireContract` at `contractProxyService.js:859-862, 890-898, 912-915`; do not copy these replies without confirming the V24 client behavior.
