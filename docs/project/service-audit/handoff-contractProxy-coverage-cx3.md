# contractProxy coverage handoff (cx3, 2026-10-30)

## Client evidence

Whole-tree eve-logic tracing found the contract API and its callers in `eve/client/script/ui/shared/neocom/contracts/contracts.py`: service binding at :93; login rows consumed at :190-205; AcceptContract called at :421 and its returned fields read at :427-429; completion, create, and delete flows at :444-555; auction settlement at :676-677; page summary reads at :727-740; escrow fields at :769-782; courier wrap lookup at :864-879. Other UI consumers include `contractPanels.py:416-423`, `createContractWnd.py:92,888-892,1210,1320,1343,1647`, `contractentry.py:181-209`, and `contractsDetailsWnd.py:513-600`. The detailed per-RPC contract and consumer map remains in `DEEPCHECK_contractProxy.md`.

## Completed group

- RPC-01171 AcceptContract is now IMPLEMENTED-TESTED in the coverage CSV. `ContractProxyPersistenceWireTests.Accept_contract_runs_through_host_and_persists_courier_state_and_notification` sends the client call through the registered TCP Host using the PostgreSQL-backed contract writer, inventory store, and outbox. It checks the client reply fields, persisted acceptance, moved courier wrap, and `OnContractAccepted` outbox event.
- Local focused DB test passed on `eve_p3`: 1 passed, 0 failed, 0 skipped.
- Client shape evidence: `contracts.py:421-429`; details UI invokes acceptance and reinitializes from the reply path at `contractsDetailsWnd.py:513-523`.

## Remaining P1 rows

RPC-01173-01175 CompleteContract (failed, finished, rejected); RPC-01176 CreateContract; RPC-01177 DeleteContract; RPC-01179 DeleteMultipleContracts; RPC-01181 FinishAuction.

The older registered Host scenario still covers request/reply routing with recording providers, but these rows need PostgreSQL-backed state and notification checks before their CSV status changes. Remaining client callers are listed above and in `DEEPCHECK_contractProxy.md`.

## Checks and handoff state

- Host.Tests project build passed (0 warnings, 0 errors).
- Focused PostgreSQL Host-wire case passed on eve_p3 before and after merging origin/main: 1 passed, 0 failed, 0 skipped.
- Pre-push red-gate check showed the newest completed main CI run green. Commit and push for this group are next; its push-triggered Actions result remains to be checked.
- No graphical client run was performed.
