# Account wallet RPC host coverage handoff

## Scope and implementation

Rows RPC-00733–RPC-00738 all resolve to the already implemented `account` service methods. The gap was end-to-end Host wire regression coverage. Added `dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs`: it registers the production `AccountService` in `ServiceRegistry`, dispatches marshalled `Packets.Call` requests through `PacketDispatcher`, and uses persisted PostgreSQL stores and the production `WalletAnnouncer`. It checks the three RPC methods, response values/types, resulting balances, committed transaction changes, and notification-before-reply order.

No implementation behavior or compatibility deviation was changed. No database was used locally; the focused `[PgFact]` runs in the GitHub Actions test environment.

## V24.01 whole-client evidence

The whole `/home/user/eve-logic` search found these callers and consumers:

- `carbon/common/script/net/httpGate.py:34`: calls `GetCashBalance(0)` and returns the value as JSON.
- `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:40-48`: reads personal cash as a scalar for wallet wealth; `:157-158` reads corporation cash as a scalar and maps a `UserError` to zero.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_home.py:197`: calls `GetCashBalance(1, 1000)` and uses the amount for dividend form eligibility.
- `eve/client/script/ui/shared/neocom/wallet/panels/personal/giveIskPanel.py:102-103`: calls personal `GiveCash` and checks truthiness before showing success.
- `eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:147-152`: calls personal `GiveCash` or `GiveCashFromCorpAccount`; the window handles completion without consuming the returned value.

These consumers establish that cash balance must be numeric and that the personal gift panel requires a truthy success value. The transfer window does not inspect the transfer reply.

## EveJS comparison

The matching executable reference is `/home/user/evejs-v0.12.9/server/src/services/account/accountService.js`:

- `:678-700` returns the actual personal/corporation balance and checks corporation access.
- `:864-913` performs personal/corporation `GiveCash` transfers.
- `:915-964` performs `GiveCashFromCorpAccount`.

The eve.rs Host regression exercises these existing paths against persisted balances and the real announcer. The existing deviation record for corporation donation reply details is in `docs/reference/compat/DEVIATIONS.md`; this work adds no new difference.

## Checks and handoff

- Initial local compile check passed with 0 warnings and 0 errors. Follow-up local recompilation was killed during C# compilation with csc exit 137 under shared-memory pressure.
- Focused Actions run `40080221080` failed because the fixture founder had only 5,000 ISK for the 1,599,800 ISK corporation registration fee. The fixture now starts the founder at the fee plus 5,000 ISK and checks the post-fee balance.
- No local PostgreSQL command was run.
- Fix commits: `0f5c0c20` (wire regression), `c6815db1` (founder balance correction). Pushed branch head after main integration: `90f549b9`.
- Nine pre-existing unrelated Rust deletions remain unstaged and were not committed.

Update this note with the next Actions result after the check finishes.
