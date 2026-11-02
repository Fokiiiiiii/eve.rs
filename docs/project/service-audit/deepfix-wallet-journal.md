# Wallet and ISK accounting deep-fix handoff

## Status

Fixed one client-visible wallet transfer defect in the personal and corporation transfer RPCs. The wider wallet, journal, market-tax, and bill audit remains in progress. No graphical-client run was made.

## Finding and change

When a character transfer lacked funds, `AccountService.GiveCash` caught `InsufficientFundsException` and returned `PyNone`. The corporation-division transfer did the same. V24.01 `transferMoneyWnd.py:146-151` shows “Transfer Successful” after either RPC returns normally; `giveIskPanel.py:95-106` only clears its form when `GiveCash` returns truthy. Both paths could therefore report success without moving ISK.

The service now maps insufficient funds to the client `NotEnoughMoney` error with `amount` and `balance` substitutions. `WalletTransactions.TransferAsync` carries the balance it read under the wallet-row lock in that exception (`dotnet/src/Eve.Wallet/WalletTransactions.cs:89`). The account service applies the same error mapping to both RPCs (`dotnet/src/Eve.Services.Wallet/AccountService.cs:225-230,285-290`). The transfer transaction still rolls back before any wallet notification.

## Evidence

- Whole-client transfer entry points: `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:141-150`, `corpISKPanel.py:53-66`, `corpDivisionsPanel.py:85-91`, and `eve/client/script/ui/services/menusvc.py:614-615,651-652`.
- Client result handling and success message: `eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:138-151`; direct Give ISK result handling: `.../panels/personal/giveIskPanel.py:95-106`.
- The client consumes wallet notifications through `walletSvc.py:80-90` and `wallet_balance.py:54-56,135-140`. Successful Host tests keep the existing `OnAccountChange` notifications before the call response; refused transfers send only the error response.
- Current EveJS 261028.1 handles `INSUFFICIENT_FUNDS` as `NotEnoughMoney` with `balance` and `amount` values in `/home/user/evejs-261028.1/src/server/src/services/account/accountService.js:914-980`; the shared values are built in `walletState.js:217-222`. Its successful character transfer returns true, matching `giveIskPanel.py`, while the corporate transfer result is ignored by `transferMoneyWnd.py`.
- Registered RPC regression: `dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs:114-126,143-171`. Wallet lock regression: `dotnet/tests/Eve.Wallet.Tests/TransactionTests.cs` (search for `An_insufficient_transfer_reports_the_locked_available_balance`).

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Wallet.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Corporations.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- On database `eve_p5`, `TransactionTests`: 9 passed, 0 failed, 0 skipped; `AccountWalletHostRpcTests`: 1 passed, 0 failed, 0 skipped; `AccountRpcTests`: 2 passed, 0 failed, 0 skipped.
- The first Host run exposed a stale expected recipient balance in the new test; the test now snapshots the balance immediately before the corporate transfer and passes.

## Audit work still open

Trace and compare the market order/fill and journal consumers, the fee and sales-tax settlement, and bill payment/auto-pay through all client callers, Host registration, wallet state, persistence and notifications. Check each against current EveJS 261028.1 and EVE University mechanics before changing any values or marking a flow complete.
