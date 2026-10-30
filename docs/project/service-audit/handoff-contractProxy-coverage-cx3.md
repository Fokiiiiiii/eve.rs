# contractProxy coverage handoff (cx3, 2026-10-30)

## Verified client path

A whole-tree Serena search of `/home/user/eve-logic` found the contract service API and its direct callers in `eve/client/script/ui/shared/neocom/contracts/contracts.py:42, 190, 344-554, 672-789, 851-879`. The client reads login contract rows at :190-205, updates local lists after DeleteContract / DeleteMultipleContracts at :444-468, consumes the page summary at :727-740, and reads escrow values at :769-782.

Other consumers found across the client tree:

- `contractPanels.py:416-423` chooses current, bid, expired, or owner contract bundles.
- `createContractWnd.py:92, 888-892, 1210, 1320, 1343, 1647` displays dockable/container inventory, retries cargo-space confirmation, checks the outstanding limit, and uses per-container counts.
- `contractentry.py:181-209` invokes deletion and auction completion; `contractsDetailsWnd.py:515-600` invokes accept, completion, delete, and auction settlement.
- `contracts.py:864-879` turns a missing courier wrap lookup into the client-side not-found error.
- The contract detail consumers of packed contract/items/bid rows are recorded in `DEEPCHECK_contractProxy.md` under RPC GetContract (`contracts.py:335-350`, `contractentry.py:126,246`, `contractsDetailsWnd.py:135-168`).

This identifies all direct service calls and the UI paths that consume the read replies or trigger state changes; the existing deepcheck table records the field-level consumers for each contractProxy RPC.

## Covered rows

Thirteen database-free read RPC rows now point to the passing registered Host scenario `Contract_reads_and_mutations_are_dispatched_through_the_host_with_client_shapes` in `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs`: RPC-01172, 01184-01195. The test reaches the registered `contractProxy` route over TCP, checks reply types and client fields, and checks paging, corporate/personal, inventory container, and expired-list variants.

The test passed locally: 1 passed, 0 failed, 0 skipped. Host.Tests and its referenced projects compiled during this run. No PostgreSQL database was used.

## Still untested

Eight P1 write rows remain IMPLEMENTED-UNTESTED: RPC-01171 AcceptContract; RPC-01173-01175 CompleteContract (finished, failed, rejected); RPC-01176 CreateContract; RPC-01177 DeleteContract; RPC-01179 DeleteMultipleContracts; and RPC-01181 FinishAuction.

The existing TCP scenario exercises their request/reply handling with recording providers. It does not exercise durable contract state, the transactional outbox, restart visibility, or notification delivery. These rows remain untested until those effects are covered by an appropriate persistence-backed Host test. The task forbids PostgreSQL use, so no such verification was run here.

## Tool limitation

Serena `replace_content` failed because no language server is available. The CSV change was applied with the authorized apply_patch fallback.
