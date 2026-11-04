# Contract deep-correctness handoff (cx5)

## State

Fixed a self-contract acceptance/bidding hole for corporation contracts when their issuing character has since left the issuer corporation, and added automatic settlement for accepted courier contracts at their delivery deadline. Both changes have PostgreSQL-backed regressions. Main run 1397 completed successfully; `origin/main` was merged and the build plus contract tests were repeated successfully afterward. This note records the tested state; check GitHub Actions for the push-triggered gate.

## Client and server path

Whole-tree V24.01 search found the `AcceptContract` RPC call only in `eve/client/script/ui/shared/neocom/contracts/contracts.py:421`; the service caller is reached from `contractsDetailsWnd.py:515,527`. The acceptance prompt displays the item exchange or courier terms in `contracts.py:368-419`, then returns to the caller's normal error path and clears its cache on success at :421-430. Courier acceptance also updates local in-progress state at :424-427. For auction bids, `contractentry.py:189-191` and `contractsDetailsWnd.py:607-617` call the service; `contracts.py:638-666` sends `PlaceBid(contractID, bid, forCorp)`. The only special retry is `ConBidTooLow` at :608-610; other `UserError` values propagate.

The live Core service registers `ContractProxyService` at `dotnet/src/Eve.Services.Core/CoreServices.cs:76`. It routes the two RPCs at `GameRpcServices.cs:270-302` and :312-344 to the persistence writer. The writer's issuer checks are in `PersistenceContractWriteModel.cs:218-219` (item exchange), :358-359 (courier), and :741-742` (auction bid). Previously, each check skipped the issuer character whenever the contract was corporate-issued and only compared the current corporation ID. A former member could therefore accept or bid on their own corporation's public contract from a personal wallet. The corrected rule always rejects the issuing character and also rejects a bid/acceptance made on behalf of the issuing corporation.

Current EveJS 261028.1 independently rejects the same actor: `server/src/services/contracts/contractRuntimeState.js:1970-1983` identifies the issuer by character ID or current issuer corporation; `prepareContractAcceptanceRequest` rejects an issuer accepting their own unassigned contract at :2365-2400. Its auction flow explicitly blocks the issuing character and issuer corporation at `server/src/services/contracts/contractAuctionRuntime.js:82-105`. EVE University's [Contracts page](https://wiki.eveuniversity.org/Contract) describes item exchange, courier collateral and auction expiry mechanics; CCP's [auction contract rules](https://support.eveonline.com/hc/en-us/articles/203280371-Auction-Contracts) confirm auction bids and buyout behavior. No statistics or SDE values changed.

On success, item-exchange/courier acceptance records `OnContractAccepted` in the same writer transaction at `PersistenceContractWriteModel.cs:288,383`; auction replacement/buyout records `OnContractOutbid`/`OnContractBuyout` at :825-828. The refusal occurs before these mutations. V24.01 consumes those notifications in `contracts.py:239-267`. The new test checks the actual registered Host wire error key and confirms balances, bid rows, and statuses remain unchanged.

## Accepted courier deadline

The V24.01 acceptance prompt shows `numDays` at `contracts.py:408`; after the RPC succeeds, the client stores `dateAccepted + const.DAY * numDays` at :424-427. The same deadline is read for issuer overdue checks at :501. V24.01 handles `OnContractCompleted` and `OnContractFailed` and refreshes escrow at :237-255. These are the client-visible deadline and terminal transitions.

EVE University's [Contracts page](https://wiki.eveuniversity.org/Contract) says that failure to deliver a courier package within its allotted time fails the contract and pays collateral to the issuer. EveJS 261028.1 exposes the accepted-courier deadline in its in-progress row at `src/server/src/services/contracts/contractRuntimeState.js:4513`, but its `isContractExpired` check at :1965-1968 only considers `dateExpired`; it has no automatic accepted-deadline settlement. The gameplay rule and V24.01 deadline take priority here.

Previously, acceptance changed the contract to status 1 but left only the listing-expiry job, while `ContractExpiryJobs` selected status 0 rows. The courier therefore stayed in progress indefinitely unless someone manually failed it. `PersistenceContractWriteModel.cs:384-386` now atomically replaces the listing job with the acceptance deadline; successful or manual terminal transitions cancel it at :557. `ContractExpiryJobs.cs:103-120` uses `date_accepted_filetime + num_days`, reschedules early invocations, and settles due status 1 couriers at :120-172: the package contents move to the acceptor's location, collateral is paid to the issuer, reward escrow is returned, the wrap is deleted, status becomes 7, and `OnContractFailed` is queued for issuer and acceptor. The focused DB test checks no early settlement at 23 hours, then status, escrow, balances, package and both outbox notifications after the one-day deadline.

## Tests and checks

- The new test first failed before the fix: the personal auction bid unexpectedly succeeded over the Host wire instead of returning `ConCannotAcceptOwnContract`.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- On database `eve_p5`, the focused registered Host wire case passed: 1 passed, 0 failed, 0 skipped. It covers item-exchange acceptance, courier acceptance, and auction bidding after the issuer character leaves the corporation.
- On `eve_p5`, `dotnet test dotnet/tests/Eve.Host.Tests --filter 'FullyQualifiedName~Contract'` passed: 40 passed, 0 failed, 0 skipped.
- `ContractCourierDeadlineTests.Accepted_courier_fails_at_its_delivery_deadline_and_forfeits_collateral` first failed with the old status-0-only query at the after-deadline assertion, then passed with the deadline settlement. Final `FullyQualifiedName~Contract` run passed: 41 passed, 0 failed, 0 skipped.
- After fast-forwarding to `origin/main` at `a251504e`, `dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false` passed, 0 warnings/errors; the DB-locked `FullyQualifiedName~Contract` run passed 41, failed 0, skipped 0.
- Existing `deepfix-market and trade and contracts.md` records earlier PostgreSQL-backed item-exchange settlement/concurrency, courier terminal transitions, and expired-auction settlement checks; those slices were not re-run in this turn.
- The graphical client and a new selected Actions run were not performed.

## Remaining audit scope

Contract search/filter consumers, bid ordering/extension behavior, and error branches outside these fixes still need separate deep checks. The graphical client was not run.
