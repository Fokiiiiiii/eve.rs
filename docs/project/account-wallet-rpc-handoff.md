# Account wallet RPC host coverage handoff

## Scope and implementation

Rows RPC-00733–RPC-00738 already have working `account` service methods. Added `dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs` to close end-to-end Host wire coverage. It registers production `AccountService`, dispatches marshalled `Packets.Call` requests through `PacketDispatcher`, uses persisted stores and production `WalletAnnouncer`, and checks reply shapes, balances, committed changes, and notification order.

No server behavior or compatibility deviation changed. No local PostgreSQL command was run.

## V24.01 whole-client evidence

- `carbon/common/script/net/httpGate.py:34`: calls `GetCashBalance(0)`, JSON encodes the result.
- `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:40-48`: consumes personal scalar wealth; `:157-158` reads corp scalar and maps `UserError` to zero.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_home.py:197`: consumes `GetCashBalance(1, 1000)` for dividend eligibility.
- `eve/client/script/ui/shared/neocom/wallet/panels/personal/giveIskPanel.py:102-103`: personal `GiveCash` truthiness gates success UI.
- `eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:147-152`: calls personal `GiveCash` or `GiveCashFromCorpAccount`; completion does not inspect the reply.

EveJS comparison: `/home/user/evejs-v0.12.9/server/src/services/account/accountService.js:678-700` returns actual balances and checks corporation access; `:864-913` handles `GiveCash`; `:915-964` handles `GiveCashFromCorpAccount`. Existing corporation-donation response difference is in `docs/reference/compat/DEVIATIONS.md`.

## Checks

- Initial local compile passed with 0 warnings and 0 errors. Follow-up compile was killed with csc exit 137 under shared-memory pressure.
- Actions run `40080221080` exposed the corporation founding-fee fixture issue; fixed with the fee plus 5,000 ISK starting balance.
- Actions runs `40084145705` and `40084153623` exposed and verified fixes for the kwargs packet helper.
- Actions run `40085201940` found the notification destination assertion issue; personal notifications use `clientID`, corporation wallet notifications use `*corpid&corpAccountKey`.
- Focused Actions run `40085581144`, test ref `25fdbc54`: passed, 1 passed, 0 failed, 0 skipped. Release solution build succeeded.
- `python3 tools/dev/ci-status.py failures 25fdbc54`: no `ci.yml` run found.
- Feature commits: `0f5c0c20` (Host wire regression), `c6815db1` (founder balance).
- Nine pre-existing unrelated Rust deletions remain unstaged and uncommitted.

The focused regression and its result are complete.

The preceding run records are historical. This increment's incoming uncommitted changes were preserved on `backup/cx8-foreign-wip-1028a` before continuing from `origin/main`.

## Wallet persistence re-read

`AccountWalletHostRpcTests.Account_wallet_rpcs_route_real_wire_calls_persist_balances_and_notify` now rebuilds the wallet store, transaction reader, service registry and dispatcher after the transfers, then reads personal and corporation balances plus each journal over the same production `account` RPC route. The regression checks that the committed balance and two transaction entries per wallet are still visible after provider reconstruction. It does not restart the entire Host process.

V24.01 starts the wallet service with `GetCashBalance(0)` (`eve/client/script/ui/shared/neocom/wallet/walletSvc.py:39-48`). Personal transactions flow through `accountsvc.py:114-115` from `personalTransactionsPanel.py:9-11`; corporation transactions flow through `accountsvc.py:127-128` from `corpTransactionsPanel.py:11-15`. The panels consume the returned transaction rows in `transactionsPanel.py:125-128`. EveJS v0.12.9 serves `GetTransactions` from wallet state (`server/src/services/account/accountService.js:832-862`) and personal balance from the account wallet (`:678-700`).

## Current persistence regression

`AccountWalletHostRpcTests.Account_wallet_rpcs_route_real_wire_calls_persist_balances_and_notify` transfers ISK through production `account` RPC calls, then reconstructs `PgWalletStore`, `WalletTransactions`, `AccountService`, `ServiceRegistry` and `PacketDispatcher`. The reconstructed providers re-read the personal and corporation balances and both `GetTransactions` lists from the same PostgreSQL tables. It asserts each list has two rows and checks the newest transaction's amount and resulting balance, plus the preceding transaction amount. The test uses a real Host dispatcher and `WalletAnnouncer`; it does not restart the Host process.

Whole-client call and consumer evidence: `walletSvc.py:39-48` reads the personal balance at startup; `walletSvc.py:157-159` reads corporation balances; `accountsvc.py:107-128` calls and caches the `GetTransactions` results; `personalTransactionsPanel.py:9-11` and `corpTransactionsPanel.py:11-14` provide personal/corporation inputs; `transactionsPanel.py:125-128` consumes each row's fields; `transactionOverviewController.py:114-117` consumes personal entries; `corp_ui_home.py:196-197` uses the corporation balance for dividend eligibility; `httpGate.py:33` exposes the personal balance to the HTTP gate. `giveIskPanel.py:102-103` checks the personal transfer result, and `transferMoneyWnd.py:147-152` issues personal and corporation transfers. `marketSvc.py:21-29` uses the separate market-transaction service, not the `account` journal.

EveJS v0.12.9 uses the matching `account` methods: `server/src/services/account/accountService.js:678-700` reads personal/corporation balances and `:832-862` reads the respective transaction lists. The client evidence supports the persisted scalar balance and list-of-KeyVal transaction rows checked here. No server behavior or compatibility deviation changed.

Local checks on this increment: `dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore` passed with 0 warnings and 0 errors; the DB-free `HostServerTests.A_call_is_answered_through_the_dispatcher` test passed 1/1. This test is marked `PgFact` and is queued for Actions after push. No PostgreSQL database or graphical EVE client was used.
