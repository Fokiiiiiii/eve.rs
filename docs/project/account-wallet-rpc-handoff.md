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

`AccountWalletHostRpcTests.Account_wallet_rpcs_route_real_wire_calls_persist_balances_and_notify` transfers ISK through production `account` RPC calls, then reconstructs `PgWalletStore`, `WalletTransactions`, `AccountService`, `ServiceRegistry` and `PacketDispatcher`. The reconstructed providers re-read the personal and corporation balances and both `GetTransactions` lists from the same PostgreSQL tables. It checks three personal rows (including the corporation founding fee) and two corporation rows, with amounts and resulting balances. The test uses a real Host dispatcher and `WalletAnnouncer`; it does not restart the Host process.

Whole-client call and consumer evidence: `walletSvc.py:39-48` reads the personal balance at startup; `walletSvc.py:157-159` reads corporation balances; `accountsvc.py:107-128` calls and caches the `GetTransactions` results; `personalTransactionsPanel.py:9-11` and `corpTransactionsPanel.py:11-14` provide personal/corporation inputs; `transactionsPanel.py:125-128` consumes each row's fields; `transactionOverviewController.py:114-117` consumes personal entries; `corp_ui_home.py:196-197` uses the corporation balance for dividend eligibility; `httpGate.py:33` exposes the personal balance to the HTTP gate. `giveIskPanel.py:102-103` checks the personal transfer result, and `transferMoneyWnd.py:147-152` issues personal and corporation transfers. `marketSvc.py:21-29` uses the separate market-transaction service, not the `account` journal.

EveJS v0.12.9 uses the matching `account` methods: `server/src/services/account/accountService.js:678-700` reads personal/corporation balances and `:832-862` reads the respective transaction lists. The client evidence supports the persisted scalar balance and list-of-KeyVal transaction rows checked here. No server behavior or compatibility deviation changed.

Local checks after the corrections: the Host test project build passed with 0 warnings and 0 errors; the DB-free `HostServerTests.A_call_is_answered_through_the_dispatcher` test passed 1/1. Host Actions run `40790744414` found the personal journal also contains the founding-fee row (expected 2, actual 3); the regression now expects that row and checks its amount and balance. Run `40794743590` then exposed a test-helper shape error: `PyObject.Args` is the `PyDict` directly. The helper now reads that shape. A new Actions run is required after the fix is pushed. The persistence test is marked `PgFact`; no local PostgreSQL database or graphical EVE client was used.

## Corporate wallet balance authorization (2026-11-04)

`WalletAnnouncer` now sends a committed corporation balance only to sessions whose server-owned `rolesAtAll` mask has Director or Account Take for that exact division. This matches V24.01's division check; an Accountant/Junior Accountant bit alone does not authorize a notification for every division. `CorporationRoles.CanReadWalletBalanceChange` keeps that notification rule separate from `CorporationRoles.CanReadWalletDivision`, which preserves the existing persisted Account RPC behavior. `CorporationLoginHook.GetSessionAttributesAsync` populates `rolesAtAll` from persisted membership and role/title state; corporation registry role and membership mutations refresh those session attributes. The notification key, destination, balance encoding, and commit callback order remain unchanged.

`GetAllMyCorporationWalletLPBalances` still resolves the character's corporation through persisted affiliation, then returns the existing rowset only when the injected director check confirms the character is a Director of that same corporation. Denial returns the same rowset shape with no rows. The stale-session regression seeds both the former and current corporations and confirms only the current Director balance is returned.

V24.01 whole-client evidence: `walletSvc.py:80-88` scatters `OnAccountChange_Local` before `_OnCorpAccountChange` finishes its role checks at `:96-107`; `walletUtil.py:34-54,62-78` defines the wallet and division roles; `corpLPTab.py:27-30` gates the LP panel on Director; `loyaltyPointsWalletSvc.py:34-38` is the direct LP RPC consumer. Other notification consumers found across the client are `industrySvc.py:280-284`, `accountsvc.py:63-67`, `securityOfficeWindow.py:149-153`, `lpStoreSvc.py:389-391`, `wallet_balance.py:135-140`, and `lpStorePanel.py:400-412`. `wallet_balance.py:135-140` and `lpStorePanel.py:400-412` consume the earlier local scatter, so client-side cache gating alone cannot protect the notification payload.

EveJS 261028.1 sends corporate notifications broadly (`server/src/services/corporation/corpWalletState.js:241-256`) and reads corporation LP balances without a Director check (`server/src/services/corporation/lpService.js:104-114`). The server checks follow V24.01's authorization and local-scatter behavior; this difference is recorded in `docs/reference/compat/DEVIATIONS.md`.

Local verification after syncing main on `eve_p8`: `Eve.Corporations` and `Eve.Services.Wallet` builds succeeded with 0 warnings/errors; `AnnouncementsTests` and `LoyaltyPointsServiceTests` passed 9/9; the selected corporation protocol and bill tests passed 55/55; selected `MarketRpcTests` and `MarketTransactionReadTests` passed 27/27; `AccountWalletHostRpcTests` passed 1/1. All tests ran through `tools/dev/db-lock.sh`; 0 skipped. The graphical V24.01 client was not exercised. Commit `3bb9d0c8` is pushed to main. Selected Agent Test run `41894272015` passed with no failed tests or jobs. Push-triggered CI run `41894228198` failed at `HostServerTests.Registered_lp_store_and_wallet_routes_return_the_client_consumed_shapes`: the fixture expected a corporate LP row without injecting the required Director check. I reproduced the failure on `eve_p8`; the fixture now supplies and asserts that check, and the focused regression passes 1/1 with 0 skipped. The failed Host shard also reported skips for `AbyssalHostFlowTests.Tournament_camera_rejects_an_actual_private_scene` and `AbyssalHostFlowTests.Referee_from_npc_station_returns_to_same_station_with_same_ship_and_condition`; those are skips, not passes. The correction is ready locally and awaits its fix-only commit and push.
## Transfer RPC failure reporting (2026-11-04)

The whole-client Serena search in `/home/user/eve-logic` found two direct callsites: `transferMoneyWnd.py:147-152` calls both transfer methods then reports success after a normal return, and `giveIskPanel.py:102-108` runs its success UI only when personal `GiveCash` returns truthy. No other source callsites were found. EveJS 261028.1 `server/src/services/account/accountService.js:914-980, 982-1044` returns `null` for other failed settlement results. The Host's `EconomyRpc.cs:41-45` registers the production `AccountService`; `PacketDispatcher.cs:257-270` sends a thrown `MachoWrappedException` as an RPC error response.

`GiveCash` and `GiveCashFromCorpAccount` now turn `WalletNotFoundException` and `WalletOperationConflictException` into the client-visible `CustomInfo` user error at `AccountService.cs:235-239, 296-300`. The Host regression covers personal and corporate-source operation conflicts by replaying a call ID with changed terms, a personal recipient with no wallet, and a corporate source division with no wallet. Each case requires an ErrorResponse and verifies source/destination balances, journals, and committed-change count stay unchanged. The existing successful transfer, notification, persistence, and insufficient-funds checks remain in the same regression.

Local verification on `eve_p3`: `Eve.Services.Wallet` build passed with 0 warnings and 0 errors; `Eve.Host.Tests` build passed with 0 warnings and 0 errors; `AccountWalletHostRpcTests.Account_wallet_rpcs_route_real_wire_calls_persist_balances_and_notify` passed 1/1 with 0 skipped; selected `AccountRpcTests` and `CorporationProtocolTests` passed 56/56 with 0 skipped; `CharacterInfoTests.AccountTests` passed 3/3 with 0 skipped. An initial fixture attempt deleted journal-referenced balance rows and hit the database foreign key; the passing fixture uses a walletless character and an unused corporation division. The reported tower-test handshake failure passed locally alone and after its immediate predecessor. Graphical-client validation remains open. Actions validation: Agent Test run `41871535851` passed; CI run `41871497956` initially failed the tower test during login with a closed handshake, then the rerun passed all jobs including the cancelled `.NET (space tests 6/32)` shard. The E2E job reported 10 skipped slow-live or skill-speed cases; those remain skips, not passes.
