# Wallet and ISK accounting deep-fix handoff

## Status

Fixed the insufficient-funds result for personal and corporation transfers (pushed as `b9b485b9`). Fixed missing post-commit wallet balance notifications for manual and automatic office-rent bill payments; this second change is locally tested and awaiting its separate push. The journal and market-tax audit remains open. No graphical-client run was made.

## Finding and change

When a character transfer lacked funds, `AccountService.GiveCash` caught `InsufficientFundsException` and returned `PyNone`. The corporation-division transfer did the same. V24.01 `transferMoneyWnd.py:146-151` shows “Transfer Successful” after either RPC returns normally; `giveIskPanel.py:95-106` only clears its form when `GiveCash` returns truthy. Both paths could therefore report success without moving ISK.

The service now maps insufficient funds to the client `NotEnoughMoney` error with `amount` and `balance` substitutions. `WalletTransactions.TransferAsync` carries the balance it read under the wallet-row lock in that exception (`dotnet/src/Eve.Wallet/WalletTransactions.cs:89`). The account service applies the same error mapping to both RPCs (`dotnet/src/Eve.Services.Wallet/AccountService.cs:225-230,285-290`). The transfer transaction still rolls back before any wallet notification.

## Bill-payment finding and change

`OfficeRentSettlement` wrote the payer debit and creditor credit in the same office-bill DB transaction, but its callers discarded the settled balances and never announced them. The paid-bill UI refreshed the bill list, while wallet balance widgets listened for `OnAccountChange_Local`; after a successful payment, the corporation wallet display could remain stale. The settlement now returns both final balances. Manual payments announce them after the bill transaction succeeds in `BillMgrService`, and scheduled auto-pay announces them only after the due-bill transaction commits (`dotnet/src/Eve.Services.Corporations/OfficeRentSettlement.cs:12-25`, `BillMgrService.cs:118-142`, `dotnet/src/Eve.Host/Persistence/Structures/StructureOfficeRentDue.cs:59-83`).

The registered RPC regression asserts the payer and creditor notifications precede the `PayCorporationBill` call response and that wallet balances, bill state, and next rent period persist. The structure auto-pay regression checks renewal and persisted state.

## Evidence

- Whole-client transfer entry points: `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:141-150`, `corpISKPanel.py:53-66`, `corpDivisionsPanel.py:85-91`, and `eve/client/script/ui/services/menusvc.py:614-615,651-652`.
- Client result handling and success message: `eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:138-151`; direct Give ISK result handling: `.../panels/personal/giveIskPanel.py:95-106`.
- The client consumes wallet notifications through `walletSvc.py:80-90` and `wallet_balance.py:54-56,135-140`. Successful Host tests keep the existing `OnAccountChange` notifications before the call response; refused transfers send only the error response.
- Current EveJS 261028.1 handles `INSUFFICIENT_FUNDS` as `NotEnoughMoney` with `balance` and `amount` values in `/home/user/evejs-261028.1/src/server/src/services/account/accountService.js:914-980`; the shared values are built in `walletState.js:217-222`. Its successful character transfer returns true, matching `giveIskPanel.py`, while the corporate transfer result is ignored by `transferMoneyWnd.py`.
- Registered RPC regression: `dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs:114-126,143-171`. Wallet lock regression: `dotnet/tests/Eve.Wallet.Tests/TransactionTests.cs` (search for `An_insufficient_transfer_reports_the_locked_available_balance`).

- Whole-client bill payment and list refresh: `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:124-139`; bill panels refresh on `OnCorpWalletBillsChanged` (`panels/corp/corpBillsPayablePanel.py:116-118`). Wallet displays separately consume `OnAccountChange_Local` (`eve/client/script/ui/shared/market/wallet_balance.py:50-56,135-139`), which `walletSvc.py` scatters from the server's `OnAccountChange` notification (`walletSvc.py:80-90`). Thus the bill-list refresh does not substitute for the wallet-balance notification.
- Office-rent payment route and post-commit notices: `dotnet/tests/Eve.Corporations.Tests/CorporationTests.cs:705-827`; auto-pay persistence and renewal: `dotnet/tests/Eve.Host.Tests/StructureOfficeTests.cs:371-397`. Current EveJS 261028.1 checks accountant/division roles, pays the bill, processes due office-rental and war bills, and returns null (`/home/user/evejs-261028.1/src/server/src/services/account/billMgrService.js:279-309`).

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Wallet.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Corporations.Tests -p:UseSharedCompilation=false`: passed, 0 warnings and 0 errors.
- On database `eve_p5`, `TransactionTests`: 9 passed, 0 failed, 0 skipped; `AccountWalletHostRpcTests`: 1 passed, 0 failed, 0 skipped; `AccountRpcTests`: 2 passed, 0 failed, 0 skipped.
- The first Host run exposed a stale expected recipient balance in the new test; the test now snapshots the balance immediately before the corporate transfer and passes.

- For the bill fix, `Eve.Corporations.Tests` build succeeded (0 warnings/errors); `Eve.Host.Tests` build succeeded (one transient file-copy warning caused by concurrent worktree builds, 0 errors). On `eve_p5`: the office-bill registered RPC test passed (1/1); `CorporationTests|BillMgrServiceTests|CorporationProtocolTests` passed (64/64); `StructureOfficeTests` passed (10/10); the composed host activity/alpha/bill RPC test passed (1/1). An earlier attempt without `EVE_PG_URL` skipped that PgFact; it was rerun with `eve_p5` and passed.
- After syncing the latest `origin/main`, the transfer regression classes were rerun on `eve_p5`: `TransactionTests` 9/9, `AccountWalletHostRpcTests` 1/1, and `AccountRpcTests` 2/2.

## Audit work still open

Trace and compare the market order/fill and journal consumers, the fee and sales-tax settlement, and bill payment/auto-pay through all client callers, Host registration, wallet state, persistence and notifications. Check each against current EveJS 261028.1 and EVE University mechanics before changing any values or marking a flow complete.
