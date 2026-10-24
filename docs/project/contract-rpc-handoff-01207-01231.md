# Contract Proxy RPC handoff: RPC-01207–RPC-01231

## Implemented increments

### RPC-01215: DeleteMultipleContracts

`dotnet/src/Eve.Services.Core/ContractProxyBatchService.cs` parses a list of positive client-range contract IDs, calls the existing `IContractWriteModel.DeleteContractAsync` for each with the active character, corporation, and Contract Manager authority, and returns the V24 pair `(deletedIDs, failedIDs)`. Per-contract domain refusals are reported in `failedIDs`; storage and unexpected errors reach the caller. Invalid list shape and IDs outside the client integer range are refused before mutations.

The whole-client path is `ContractsSvc.DeleteMultipleContracts` at eve-logic `eve/client/script/ui/shared/neocom/contracts/contracts.py:453-474`, called by `ContractEntry.DeteleMultiple` at `eve/client/script/ui/shared/neocom/contracts/contractentry.py:185-188`. The wrapper unpacks both lists at line 461, builds its success/failure message at lines 462-468, raises `OnDeleteContracts` for deleted IDs at line 470, and clears the cache at line 473. EveJS v0.12.9 registers the method at `server/src/services/_other/contractProxyService.js:845-852`; `server/src/services/contracts/contractRuntimeState.js:3440-3456` preserves separate deleted and failed lists for each item.

Production Host wiring supplies `PersistenceContractWriteModel` from `dotnet/src/Eve.Host/Hosting/HostComposition.cs:92-126`. That writer owns the transaction, escrow return, authority check, expiry cancellation, and outbox notification for each contract.

### RPC-01231: NumOutstandingContracts

The RPC counts active personal and corporation issued contracts through `IContractReadModel.GetContractListForOwnerAsync`, paging at its 100-row limit, and returns the four client KeyVal fields. eve-logic `ContractsSvc.NumOutstandingContracts` calls the proxy at `contracts.py:688-690`; `CreateContractWnd.GetNumContractsAndLimits` at `createContractWnd.py:1182-1223` reads `myCorpTotal`, `nonCorpForMyCorp`, `myCharTotal`, or `nonCorpForMyChar` according to corporation and internal-assignee rules. EveJS v0.12.9 returns those same four fields in `server/src/services/_other/contractProxyService.js:322-338`; `collectMyPageInfo` counts outstanding contracts by personal or corporation issuer in `server/src/services/contracts/contractRuntimeState.js:3960-4036`.

## Verification

The Host wire regression is `Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs`. It calls the RPCs over the socket-backed Host with the real CoreServices registration. It checks the batch result, four count values and owner scopes, three login rowsets, escrow fields, contract list bundles, accepted row, completion boolean, and deletion boolean.

- `dotnet build dotnet/src/Eve.Services.Core/Eve.Services.Core.csproj -p:UseSharedCompilation=false` — passed, zero warnings/errors.
- Focused Host test `Contract_batch_and_outstanding_count_rpcs_use_host_registration_and_client_shapes` — passed (1 passed, 0 skipped).
- No PostgreSQL connection was used. The test wires a recording store; production persistence reuse is covered by the existing single-contract writer and Host persistence tests.
- Focused Actions run 40098916457 for pushed commit `755a38a2` passed; no tests were skipped.

## Remaining assigned rows

RPC-01208–RPC-01214, RPC-01216–RPC-01219, RPC-01221–RPC-01223, and RPC-01230 still need implementation. Additional V24 input cases remain for RPC-01209–RPC-01211, RPC-01212, RPC-01225, RPC-01227, and RPC-01229. The task's direct callsites identify their initial locations; whole-tree caller and reply-consumer tracing and EveJS comparisons remain to be completed for those rows. EveJS has deliberate `false` or `None` handlers for `FinishAuction`, `DeleteNotification`, `DeleteContractNotification`, and `GM_ExpireContract` at `contractProxyService.js:859-862, 890-898, 912-915`; do not copy these replies without confirming the V24 client behavior.
