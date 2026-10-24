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

## Checks and handoff

- Initial local compile passed with 0 warnings and 0 errors. Follow-up compilation was killed with csc exit 137 under shared-memory pressure.
- Actions run `40080221080` identified the founding-fee fixture issue; founder now starts with 1,599,800 ISK plus 5,000 ISK.
- Actions runs `40084145705` and `40084153623` found a missing kwargs-aware test packet helper and duplicate introduced while merging concurrent work; it is now a single implementation.
- Actions run `40085201940` reached the test and found the notification destination assertion was too strict. Personal notifications use `clientID`; corporation wallet notifications use `*corpid&corpAccountKey`. The regression now checks each shape.
- Feature commits: `0f5c0c20` (Host wire regression), `c6815db1` (founder balance).
- Last pushed head before this update: `c9d9c30c`. Nine pre-existing unrelated Rust deletions remain unstaged and uncommitted.

Push the notification assertion update and rerun the focused Actions regression.
