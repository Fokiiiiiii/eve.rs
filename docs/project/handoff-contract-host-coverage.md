# Contract Host coverage handoff

Scope: RPC-01207 through RPC-01231, implemented in the clean worktree `/home/user/wt-cx12-contracts`. The unrelated staged/conflicted work in `/home/user/wt-cx12` was preserved. No database was used.

## Client and EveJS trace

All contractProxy callers were searched in the complete `/home/user/eve-logic/eve` tree. The exported contract methods are listed in `eve/client/script/ui/shared/neocom/contracts/contracts.py:42`. Client evidence by row (caller; then consumer/transition where distinct):

| Row | Client evidence |
|---|---|
| 01207 AcceptContract | `contracts.py:421`; acceptance details `contractsDetailsWnd.py:515,527`; completion logic `contracts.py:345-418` |
| 01208 CollectMyPageInfo | `contracts.py:727`; fields consumed at `contractPanels.py:67`, `contracts.py:732-738` |
| 01209 CompleteContract | `contracts.py:515`; completion caller `contractsDetailsWnd.py:547,570` |
| 01210 CompleteContract | `contracts.py:481`; delivery/completion caller `contractsDetailsWnd.py:547,570` |
| 01211 CompleteContract | `contracts.py:545`; courier completion `contracts.py:868-871` |
| 01212 CreateContract | `contracts.py:554`; creation UI `createContractWnd.py:888,892` |
| 01213 DeleteContract | `contracts.py:444`; callers `contractentry.py:181`, `contractsDetailsWnd.py:557` |
| 01214 DeleteContractNotification | `contracts.py:851`; auction removal transition `contracts.py:850-853`; return ignored |
| 01215 DeleteMultipleContracts | `contracts.py:458`; caller `contractentry.py:186` |
| 01216 DeleteNotification | `contracts.py:722`; `contractentry.py:195-198` removes the row only for a truthy result |
| 01217 FinishAuction | `contracts.py:672-677`; `contractentry.py:201-204` removes the row only for a truthy result |
| 01218 GM_ExpireContract | `contractentry.py:166-168`; GM-only menu is gated by ROLE_GML in `contractentry.py:75-108` |
| 01219 GetContract | `contracts.py:335-350`; cache/detail consumers also `contractentry.py:126,246` |
| 01220 GetContractListForOwner | `contractPanels.py:418`; result unpacking `contractPanels.py:407-429` |
| 01221 GetCourierContractFromItemID | `contracts.py:879-886`; courier station lookup consumer `contracts.py:888-893` |
| 01222 GetItemsInContainer | `contracts.py:712-713`; rows are sorted by typeID |
| 01223 GetItemsInDockableLocation | `contracts.py:718-719` |
| 01224 GetLoginInfo | `contracts.py:190-202`; reads assignedToMe, needsAttention, inProgress |
| 01225 GetMyBids | `contracts.py:762` |
| 01226 GetMyContractEscrow | `contracts.py:773-782` |
| 01227 GetMyCurrentContractList | `contracts.py:789` |
| 01228 GetMyExpiredContractList | `contracts.py:748`; auction cache invalidation `contracts.py:850-853` |
| 01229 GetMyExpiredContractList | `contracts.py:749`; corporate/person scope is consumed separately |
| 01230 GetNumItemsInContainers | `contracts.py:715-716` |
| 01231 NumOutstandingContracts | `contracts.py:689-690` |

EveJS v0.12.9 comparison: contract reads and writes are in `server/src/services/_other/contractProxyService.js`: GetContract 801-805; GetCourierContractFromItemID 900-905; FinishAuction 859-862 (false); GM_ExpireContract 912-915 (false); DeleteNotification 890-893 (null); DeleteContractNotification 895-898 (null). Persisted runtime paths include AcceptContract 2767-2792, CompleteContract 3393-3420, CreateContract 1619-1632, DeleteContract 3422-3438, DeleteMultipleContracts 3440-3456, and CollectMyPageInfo 3960-4036. Contract manager list methods are in `contractMgrService.js:30-63`; contractProxy list methods include bids 732-735, current list 737-744, and item/list reads 869-888. In eve.rs the two notification dismissals now persist owner-scoped dismissal, auction finish runs the durable expiry settlement, and GM expiry invokes the existing transactional expiry path with server role enforcement. These replace EveJS's false/null stubs with actual owner state changes. Auction settlement follows EVE University Contracts, “Auction Contracts” (auction expiry transfers the item to the highest bidder).

## Implementation and checks

Real Host routes and wire regressions are in `dotnet/src/Eve.Services.Core/GameRpcServices.cs` and `dotnet/tests/Eve.Host.Tests/ContractProxyBatchWireTests.cs`; persisted detail/list state and dismissals are in `PersistenceContractReadModel.cs` and `PersistenceContractWriteModel.cs`. Expiry settlement uses `ContractExpiryJobs.cs`. The batch exercises all rows through Host registration, including transport `machoVersion`, common client shapes, auction/GM transitions and dismissal effects. A separate test verifies player accounts cannot invoke GM expiry. A DB-backed persistence restart regression was added, but this task explicitly forbids database use, so it has not been exercised here.

Actions status: pending push and selected Host tests.
