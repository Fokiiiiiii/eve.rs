# Market and wallet defect handoff

Updated 2026-10-29.

## Market order notifications and volume

V24.01 clients register `OnOwnOrdersChanged` in `marketsvc.py:83-84`, `marketHistory.py:33`, `buyMultiFromBase.py:64-65`, `marketbase.py:195-196`, and `orders.py:29`. `buyMultiFromBase.py:557-567` only processes reason `Expiry`; `marketbase.py:536-549` refreshes for every reason except `Expiry`. `marketHistory.py:256-265` displays `volRemaining / volEntered`. EveJS 261028.1 defines `Created`, `Modified`, `Cancelled`, `Filled`, and `PartialFill`, and uses `Expiry` for expiry notices (`server/src/services/market/marketProxyService.js:230-237, 2130, 4604, 5344, 5629, 5703, 6025, 6107, 7073, 7384-7387`).

`MarketTrading` now emits `Expiry` for expired orders and preserves the requested order quantity as `vol_entered` when an immediate fill leaves a resting remainder. Host notification tests assert the Created, Modified, Cancelled, Filled, PartialFill, and Expiry reasons; the partial-fill regression checks both persisted and receipt values.

## Wallet journal month query

V24.01 `personalTransactionsPanel.py:9-11` and `corp/corpTransactionsPanel.py:11-15` pass the selected year and month to `accountsvc.py:107-128`; `transactionOverviewController.py:114-117` uses the no-month recent-history path. EveJS `server/src/services/account/accountService.js:881-915` forwards the month to the character or corporate journal filter; `server/src/services/account/walletState.js:471-492` and `server/src/services/corporation/corpWalletState.js:1159-1171` filter the requested month.

The Host wallet SQL range query and the regression that requests a month containing 125 rows are in the follow-up wallet commit.

## Checks

Market and wallet service test projects build locally. Two database-free tests pass: the market immediate-fill notification shape and the wallet account-change notification shape. Database-backed regressions are reserved for the GitHub Actions checks.
