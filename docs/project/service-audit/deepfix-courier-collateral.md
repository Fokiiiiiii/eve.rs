# Courier collateral and auction claims (cx5)

## Result

Fixed two contract lifecycle defects in this worktree: accepted courier contracts now fail at their delivery deadline with collateral forfeited to the issuer (previously recorded in `deepfix-contracts.md`), and auction expiry no longer automatically pays the seller and delivers the item. Auction item and money claims now follow the two separate client actions and persist independently until both sides claim.

## Evidence and decisions

The V24.01 client exposes auction item and money claims separately: `eve/client/script/ui/shared/neocom/contracts/contractentry.py:201-210` calls `FinishAuction(contractID, isIssuer=False/True)` and removes that entry when the RPC returns true. The details window makes the same winner-then-refresh and issuer-then-refresh calls at `contractsDetailsWnd.py:589-601`. The service wrapper forwards the boolean unchanged and propagates `UserError` at `contracts.py:672-678`; no new client error key or fabricated success was introduced.

The client defines auction statuses 2/3/4 as issuer-finished, contractor-finished, and finished in `eve/common/lib/appConst.py:1029-1031`. Their user-facing labels distinguish “Items not yet claimed,” “Unclaimed by seller,” and “Finished” in `eve/common/script/util/contractscommon.py:121-123`. The client consumes `OnContractCompleted` by showing the completed message and refreshing escrow at `contracts.py:237-239`; its failure handler likewise shows the failure and refreshes escrow at :252-254. Thus the server retains the auction row between independent claims and emits completion only when both are done.

EveJS 261028.1 models those claim flags and statuses in `src/server/src/services/contracts/contractAuctionClaims.js:32-47`; the issuer/winner claim actions and settlement effects are in :62-105, with expiry reconciliation at :113-124. Its auction job schedules expiry only for open auctions in `src/server/src/services/contracts/contractAuctionJobs.js:10-26`. This agrees with CCP's [Auction Contracts rules](https://support.eveonline.com/hc/en-us/articles/203280371-Auction-Contracts), which require both parties to claim after an auction, and EVE University's [Contracts guide](https://wiki.eveuniversity.org/Contract) on courier collateral and missed delivery deadlines. Courier deadline calculation follows the client’s accepted-date plus `numDays` at `eve/client/script/ui/shared/neocom/contracts/contracts.py:424-427`; collateral forfeiture on missed delivery is described by the EVE University guide.

Previously `ContractExpiryJobs.SettleAsync` settled type-2 auctions at expiry: it could move the item and pay the winning bid while marking the contract complete before either client claim. Now `ContractExpiryJobs.cs:287-288` leaves type-2 rows for claims. The registered FinishAuction path passes actor, corporation and manager role through `GameRpcServices.cs:559-560`, `PersistenceContractWriteModel.cs:606-610`, and `ContractExpiryJobs.cs:80-81`. The transactional claim applies authorization and deadline checks, transfers goods or proceeds for the requested side, changes status 2/3/4, and publishes issuer-then-winner completion events only on final completion (`ContractExpiryJobs.cs:80-194`). A no-bid expiry returns the goods and deposit to the issuer through the issuer claim branch. Stable wallet command IDs and row locking make repeat/concurrent claims idempotent.

The courier deadline transition is documented in `deepfix-contracts.md`: V24.01 uses `dateAccepted + numDays` (:424-427), the wiki says missed courier delivery forfeits collateral, and the Host job settles accepted couriers in `ContractExpiryJobs.cs:103-172` by delivering the package to the acceptor's location, paying collateral to the issuer, returning reward escrow, deleting the wrap, setting failed status 7, and queueing `OnContractFailed` for both parties. The old behavior left accepted couriers in progress indefinitely.

## Regression and verification

- `ContractProxyPersistenceWireTests.Finish_auction_claims_items_and_proceeds_independently` verifies the registered Host wire call, expiry behavior, separate item/money state, concurrent duplicate issuer claims, final balances/status and issuer-then-winner notification order.
- `ContractReadModelTests.Expired_auction_keeps_winning_item_and_proceeds_as_independent_claims` verifies expiry, item ownership, both balances, final status and repeat-claim behavior against PostgreSQL.
- Counterfactual check: temporarily removing the type-2 expiry guard made the Host regression fail at its expected-false assertion (actual true); the guard was restored.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- On `eve_p5`, DB-locked Host `FullyQualifiedName~Contract`: 41 passed, 0 failed, 0 skipped.
- Core `FullyQualifiedName~Contract`: 59 passed, 0 failed, 0 skipped.
- Earlier courier and issuer-self-contract regression results are recorded in `deepfix-contracts.md`.
- The graphical V24.01 client was not operated during this check. No stat/SDE value was changed.
