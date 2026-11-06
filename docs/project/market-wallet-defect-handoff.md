# Market and wallet defect handoff

Updated 2026-11-06.

## Market order notifications and volume

V24.01 clients register `OnOwnOrdersChanged` in `marketsvc.py:83-84`, `marketHistory.py:33`, `buyMultiFromBase.py:64-65`, `marketbase.py:195-196`, and `orders.py:29`. `buyMultiFromBase.py:557-567` only processes reason `Expiry`; `marketbase.py:536-549` refreshes for every reason except `Expiry`. `marketHistory.py:256-265` displays `volRemaining / volEntered`. EveJS 261028.1 defines `Created`, `Modified`, `Cancelled`, `Filled`, and `PartialFill`, and uses `Expiry` for expiry notices (`server/src/services/market/marketProxyService.js:230-237, 2130, 4604, 5344, 5629, 5703, 6025, 6107, 7073, 7384-7387`).

`MarketTrading` now emits `Expiry` for expired orders and preserves the requested order quantity as `vol_entered` when an immediate fill leaves a resting remainder. Host notification tests assert the Created, Modified, Cancelled, Filled, PartialFill, and Expiry reasons; the partial-fill regression checks both persisted and receipt values.

## Wallet journal month query

V24.01 `personalTransactionsPanel.py:9-11` and `corp/corpTransactionsPanel.py:11-15` pass the selected year and month to `accountsvc.py:107-128`; `transactionOverviewController.py:114-117` uses the no-month recent-history path. EveJS `server/src/services/account/accountService.js:881-915` forwards the month to the character or corporate journal filter; `server/src/services/account/walletState.js:471-492` and `server/src/services/corporation/corpWalletState.js:1159-1171` filter the requested month.

`AccountService.GetTransactions` now passes the requested month's UTC start and exclusive next-month boundary to `WalletTransactions.JournalForAccountAsync` (`dotnet/src/Eve.Services.Wallet/AccountService.cs:88-111`, `dotnet/src/Eve.Wallet/WalletTransactions.cs:219-255`). The month path omits the unrelated recent-row cap. `TransactionHistoryTests.A_month_query_returns_every_journal_row_in_that_month` drives the account service over 125 rows in January plus a February row and expects all 125 January entries.

## Checks

Market and wallet service test projects build locally. Two database-free tests pass: the market immediate-fill notification shape and the wallet account-change notification shape. Database-backed regressions are reserved for the GitHub Actions checks.

At the time of the 2026-10-29 handoff, Market Actions run 40865820146 (commit `6515ab76`) had reported 144 passed and one failed because the new volume test selected both the filled maker and created remainder. The assertion was changed to select the `Created` order, and that handoff left the corrected database regression queued for an Actions rerun.

## Market buyer delivery notifications

`MarketNotifier.PublishAsync` resolves receipt `DeliveredItemIds` from `PgItemStore`, builds packed inventory rows with type data from the pinned SDE catalog, and sends `OnItemsChanged` before per-item `OnMarketItemReceived`. Wallet announcements, cache invalidations and own-order notices stay ahead of both delivery notifications. EveJS 261028.1 uses the same inventory-before-received order (`server/src/services/market/marketProxyService.js:2903-2913`), while its shared inventory sync emits `OnItemChange` per row by default (`server/src/services/raffles/raffleInventory.js:106-142`; `server/src/services/character/characterState.js:2701-2760`). The V24.01 cache handles `OnItemsChanged` at `eve/client/script/environment/invCache.py:218-227`; the market service forwards the four-argument received event (`eve/client/script/ui/services/marketsvc.py:80-85,140-142`) to the neocom unseen-item handler (`eve/client/script/ui/shared/inventory/neocomInventoryBadging.py:529-541`). Other whole-client `OnItemsChanged` handlers are `missions/client/missionObjectivesTracker.py:103-126` and `cosmetics/client/shipSkinSequencingSvc.py:43,100-104`.

Host RPC coverage in `dotnet/tests/Eve.Host.Tests/MarketTradeHostWireTests.cs:132-355` checks an immediate multi-item buy, a corporation-funded resting buy fill, exact recipient and packet shapes/order, persisted buyer inventory, and a database-triggered rollback that emits neither delivery notification. Local DB checks on `eve_p4`: `MarketTradeHostWireTests` 3/3, selected Host classes 8/8, selected Market classes 49/49; all had zero skips. The four touched projects (`Eve.Services.Market`, `Eve.Host`, `Eve.Market.Tests`, and `Eve.Host.Tests`) compiled locally with zero warnings and errors. Graphical-client acceptance remains open; Commit `c6150d3b` was pushed to `main`. Selected agent-test run `42248915010` completed successfully. Push-triggered CI run `42248851968` completed successfully, including headless E2E (21.1 minutes); `ci-status.py failures c6150d3b` found no failed tests or jobs. The Actions summary did not report skip totals; the local DB runs above each reported zero skipped tests.
