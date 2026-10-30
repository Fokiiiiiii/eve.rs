# contractProxy coverage handoff (cx3, 2026-10-30)

## Client evidence

Whole-tree eve-logic tracing found the contract API and its callers in `eve/client/script/ui/shared/neocom/contracts/contracts.py`: service binding at :93; login rows consumed at :190-205; AcceptContract called at :421 and its returned fields read at :427-429; completion, create, and delete flows at :444-555; auction settlement at :676-677; page summary reads at :727-740; escrow fields at :769-782; courier wrap lookup at :864-879. Other UI consumers include `contractPanels.py:416-423`, `createContractWnd.py:92,888-892,1210,1320,1343,1647`, `contractentry.py:181-209`, and `contractsDetailsWnd.py:513-600`. The detailed per-RPC contract and consumer map remains in `DEEPCHECK_contractProxy.md`.

## Completed group

- RPC-01171 AcceptContract is now IMPLEMENTED-TESTED in the coverage CSV. `ContractProxyPersistenceWireTests.Accept_contract_runs_through_host_and_persists_courier_state_and_notification` sends the client call through the registered TCP Host using the PostgreSQL-backed contract writer, inventory store, and outbox. It checks the client reply fields, persisted acceptance, moved courier wrap, and `OnContractAccepted` outbox event.
- Local focused DB test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped.
- Client shape evidence: `contracts.py:421-429`; details UI invokes acceptance and reinitializes from the reply path at `contractsDetailsWnd.py:513-523`.

- RPC-01173-01175 CompleteContract are now IMPLEMENTED-TESTED in the coverage CSV. `ContractProxyPersistenceWireTests.Complete_contract_finishes_fails_and_rejects_with_persisted_notifications` sends all three statuses through the registered Host and verifies durable statuses plus `OnContractCompleted`, `OnContractFailed`, and `OnContractRejected` outbox entries.
- The focused completion DB test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped.
- Client callers and visible transitions: `contracts.py:481-489` (finish), :515-520 (fail), :545-554 (reject); details window refreshes after these actions at `contractsDetailsWnd.py:535-553`.

- RPC-01177 and RPC-01179 DeleteContract / DeleteMultipleContracts are now IMPLEMENTED-TESTED in the coverage CSV. `ContractProxyPersistenceWireTests.Delete_contract_calls_persist_status_and_batch_keeps_per_contract_failures` checks the registered Host boolean and tuple reply, confirms eligible contracts persist as deleted, and verifies the foreign contract ID remains in the failed list with its state unchanged.
- The focused delete DB test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped. Client list update consumers are `contracts.py:444-468` (`OnDeleteContracts` receives the successfully deleted IDs).

- RPC-01176 CreateContract is now IMPLEMENTED-TESTED in the coverage CSV. `ContractProxyPersistenceWireTests.Create_contract_persists_items_fees_expiry_and_creation_notification_over_host` exercises the client `ContractStruct` plus `confirm=None` through the registered Host and checks the ID-list reply, escrow transfer, fee debit, scheduled expiry job, and `OnContractCreated` outbox event.
- The focused create DB test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped. Client call and retry behavior are at `contracts.py:555`; `createContractWnd.py:888-892` retries after the cargo-space confirmation, and :1210/:1320/:1343/:1647 consume the inventory-side context.

- RPC-01181 FinishAuction is now IMPLEMENTED-TESTED in the coverage CSV. `ContractProxyPersistenceWireTests.Finish_auction_settles_expired_bid_and_persists_winner_inventory_and_notification` verifies the client boolean reply, expired auction state, winning bid payout, escrow item transfer and `OnContractCompleted` outbox notification through the registered Host.
- The focused auction DB test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped. Client calls and bid settlement UI are at `contracts.py:676-677` and `contractsDetailsWnd.py:590-600`.

## Remaining P1 rows

None. All eight P1 write rows identified at the start of this coverage group now point to focused PostgreSQL-backed registered Host tests in the CSV. The read rows tested by the older Host scenario remain as recorded there.

## Checks and handoff state

- Host.Tests project build passed (0 warnings, 0 errors).
- Acceptance DB Host-wire test passed before and after merging origin/main; completion, delete, create, and auction DB Host-wire tests passed locally: each 1 passed, 0 failed, 0 skipped.
- Acceptance group commit `04dc4844` is pushed. Its push-triggered Actions run remains queued; the remaining coverage group must wait for that run to complete before push.
- No graphical client run was performed.
