# Contract Proxy RPC handoff: RPC-01207–RPC-01231

## Increment shipped in this worktree

Implemented RPC-01215, `contractProxy.DeleteMultipleContracts`, in `dotnet/src/Eve.Services.Core/ContractProxyBatchService.cs`. The service parses a client list of positive contract IDs, calls the existing `IContractWriteModel.DeleteContractAsync` for each ID with the logged-in character, current corporation, and Contract Manager authority, and returns the V24 pair `(deletedIDs, failedIDs)`. Per-contract domain refusals are reported in `failedIDs`; storage and other unexpected failures reach the caller. Invalid list shape and IDs outside the client integer range are refused before mutations.

The whole-client path is `ContractsSvc.DeleteMultipleContracts` in eve-logic `eve/client/script/ui/shared/neocom/contracts/contracts.py:453-474`, called from `ContractEntry.DeteleMultiple` at `eve/client/script/ui/shared/neocom/contracts/contractentry.py:185-188`. The wrapper unpacks the two lists at line 461, builds a success/failure message from them at lines 462-468, publishes `OnDeleteContracts` for deleted IDs at line 470, and clears the contract cache at line 473. This is the consumed reply contract.

EveJS v0.12.9 registers the same method in `server/src/services/_other/contractProxyService.js:845-852`; its runtime loops over IDs and returns separate success and failure lists in `server/src/services/contracts/contractRuntimeState.js:3440-3456`. eve.rs delegates each item to the existing authoritative writer. Production Host wiring supplies `PersistenceContractWriteModel` from `dotnet/src/Eve.Host/Hosting/HostComposition.cs:92-126`; its single-contract deletion owns the transaction, escrow returns, role checks, expiry cancellation, and outbox notification.

## Verification

- `dotnet build dotnet/src/Eve.Services.Core/Eve.Services.Core.csproj -p:UseSharedCompilation=false` — passed, zero warnings/errors.
- Focused Host socket test `Delete_multiple_contracts_is_dispatched_through_the_host_and_reports_each_outcome` — passed (1 passed, 0 skipped). It registers the actual `CoreServices` service in `HostServer`, calls the RPC over the Host protocol, and checks the wire pair and per-ID outcomes.
- No PostgreSQL connection was used. The Host regression exercises the service through real registration with a recording writer; existing persistence deletion regressions remain the evidence for the production writer transaction.

## Remaining work in the requested batch

RPC-01207–RPC-01214 and RPC-01216–RPC-01231 remain to be resolved and verified. Their assigned V24 callsites are in `eve/client/script/ui/shared/neocom/contracts/contracts.py` and `contractentry.py` as listed in the task; the rest of their callers and reply consumers still need whole-tree tracing. EveJS has deliberate `false` or `None` handlers for `FinishAuction`, `DeleteNotification`, `DeleteContractNotification`, and `GM_ExpireContract` at `contractProxyService.js:859-862, 890-898, 912-915`; these values need client-context confirmation and must not be copied as fake success.
