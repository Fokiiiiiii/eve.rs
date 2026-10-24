# Account wallet RPC host coverage handoff

## Scope and implementation

Rows RPC-00733–RPC-00738 already have working `account` service methods. Added `dotnet/tests/Eve.Host.Tests/AccountWalletHostRpcTests.cs` to close the end-to-end Host wire coverage gap. It registers the production `AccountService` in `ServiceRegistry`, dispatches marshalled `Packets.Call` requests through `PacketDispatcher`, uses persisted stores and the production `WalletAnnouncer`, and checks all three methods’ reply values/types, balances, committed changes, and notification-before-reply order.

No server behavior or compatibility deviation changed. No local PostgreSQL command was run.

## V24.01 whole-client evidence

Whole-tree search found these callers and consumers:

- `carbon/common/script/net/httpGate.py:34`: `GetCashBalance(0)`, JSON encodes the value.
- `eve/client/script/ui/shared/neocom/wallet/walletSvc.py:40-48`: uses personal scalar balance for wealth; `:157-158` reads corporation scalar and maps `UserError` to zero.
- `eve/client/script/ui/shared/neocom/corporation/corp_ui_home.py:197`: reads `GetCashBalance(1, 1000)` for dividend form eligibility.
- `eve/client/script/ui/shared/neocom/wallet/panels/personal/giveIskPanel.py:102-103`: personal `GiveCash` reply truthiness gates success UI.
- `eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:147-152`: calls personal `GiveCash` or `GiveCashFromCorpAccount`, then handles completion without inspecting the reply.

Thus balances need numeric values and the personal gift needs a truthy success reply; the transfer window ignores that reply.

## EveJS comparison

`/home/user/evejs-v0.12.9/server/src/services/account/accountService.js:678-700` returns real balances and checks corp access; `:864-913` handles personal/corp `GiveCash`; `:915-964` handles `GiveCashFromCorpAccount`. Existing `docs/reference/compat/DEVIATIONS.md` records the corporation donation reply difference. This work adds no new deviation.

## Checks and handoff

- Initial local compile passed (0 warnings, 0 errors). Follow-up compilation was killed with csc exit 137 during shared-memory pressure.
- Actions run `40080221080` found and led to fixing the underfunded corporation fixture: founder starts at the registration fee plus 5,000 ISK.
- Actions run `40084145705` found the missing `Packets.CallWithKwargs` helper in a concurrent Host test. The merged helper was consolidated to a single implementation; ordinary `Call` delegates to it.
- Actions run `40084153623` checked the pre-helper source.
- Feature commits: `0f5c0c20` (Host wire regression), `c6815db1` (founder balance).
- Latest pushed head: `99083293`. Nine pre-existing unrelated Rust deletions remain unstaged and uncommitted.

Dispatch the focused Host regression against the latest pushed head, then add its result here.
