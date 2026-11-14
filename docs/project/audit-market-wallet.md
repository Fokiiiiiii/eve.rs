# Market and wallet audit

Date: 2026-11-14. Scope: market orders and fills, ISK wallets and journals, trading, courier and other contract payments, insurance settlements, and LP store purchases. Existing findings recorded in `audit-fix-*.md` were not repeated.

## Confirmed and fixed

### A second identical market buy was refused for five seconds

**Verdict: confirmed with fix.** `MarketProxyService.RefuseRepeatedPlacement` keyed only on session ID and the last request terms. It refused two distinct calls with identical station, type, price, quantity, range, duration, and wallet choice even when their RPC CallIDs differed. This could prevent a pilot from intentionally placing two same-price orders or buying the same item twice from a newly replenished book.

The V24.01 path is a normal buy action in `eve/client/script/ui/shared/market/buyThisTypeWindow.py:543-545,701`, which reaches `eve/client/script/ui/services/marketsvc.py:256-268`. A whole-client search for `PlaceBuyOrder` and `BuyMultipleItems` found the direct RPC sends in `marketsvc.py:266,271`; the separate slash helper also reaches `BuyStuff` at `eve/devtools/script/svc_slash.py:1463`. Every blocking call gets a fresh ID in `carbon/common/script/net/machoNet.py:3592-3604`. EveJS handles each `PlaceBuyOrder` request by calling `executeBuyRequest` in `server/src/services/market/marketProxyService.js:7000-7048`; it has no five-second equality guard.

The guard was removed from the single buy, sell-line, and multi-buy paths in `dotnet/src/Eve.Services.Market/MarketProxyService.cs`. The durable operation key still includes session, CallID, character, and action (`Key`), and `MarketTrading.PlaceWithinTransactionAsync` replays a receipt only for that same key and matching terms (`dotnet/src/Eve.Market/MarketTrading.cs:245-280`). The market write remains serializable and commits order, wallet, inventory, fill, and receipt changes together (`MarketTrading.cs:74-86,303-471`).

The new real-Host regression `MarketTradeHostWireTests.Registered_market_accepts_consecutive_identical_buys_as_distinct_client_calls` first failed before the fix at `RefuseRepeatedPlacement` on its second call. After the fix, each call's settlement notices precede its delivery notification; the test also verifies two different delivered item IDs, two persisted fills, two delivered units, the exact 9,980 ISK balance from two 10 ISK buys, and an emptied NPC ask. `MarketHostTests.The_host_refuses_an_off_tick_price_a_fast_modification_and_a_stale_view_but_accepts_identical_new_calls` also verifies two identical resting bids persist independently while an off-tick price, a fast modification, and a stale view still receive their refusals.

## False positives and checked paths

### Duplicate identical rows in one `BuyMultipleItems` request

**Verdict: false positive for a player-reachable V24 flow.** `buyMultiFromBase.py:263-273` keeps one row per `typeID` and adds repeated quantities to that row; `_PerformTrade` sends the validated row list at `:296-306`. The current server rejects an exactly duplicated request row as malformed input. EveJS processes each supplied row in order (`marketProxyService.js:7057-7119`), but the V24 UI does not produce two identical rows through its normal flow. This check was left as request validation; the normal path is covered by the existing multi-buy Host tests.

### Market fees, taxes, matching, expiry, and escrow accounting

**Verdict: no confirmed player-facing discrepancy found in this pass.** `marketsvc.py:256-268` rounds the client buy price to two decimals. `MarketRules.RoundIsk` uses the same cent rule as EveJS `marketRules.js:93-99`; broker fees, SCC surcharge, and tax are calculated on the transaction value (`dotnet/src/Eve.Market/MarketRules.cs:51-140`, `marketRules.js:93-99,278-285`). `MarketMatching` filters by type, side, region, expiry, price, range, and minimum volume, then orders by price, issue time, and ID (`MarketMatching.cs:43-77`). EveJS match walks `marketProxyService.js:4412-4447,5203-5240` apply price, range, quantity, and bid minimum-volume checks in book order. The Host settlement locks the book and affected wallets/items, settles each fill, updates remaining order escrow, and writes the receipt inside the transaction (`MarketTrading.cs:303-471`). Existing expiry, fill, fee, custody, and concurrency tests cover these rules; final run results are recorded below.

### Replayed wallet and market operations

**Verdict: false positive for duplicate ISK or item mutation on the same operation key.** V24's transfer window calls `account.GiveCash` or `GiveCashFromCorpAccount` (`eve/client/script/ui/shared/neocom/wallet/transferMoneyWnd.py:138-153`). Host `AccountService` authenticates the source division and settles with a CallID-scoped operation key (`dotnet/src/Eve.Services.Wallet/AccountService.cs:205-249,251-305`); EveJS exposes the same transfer handlers in `server/src/services/account/accountService.js:850-914,917-970`. Personal wallet transfers lock both balances, check an existing debit/credit pair against the original terms, check funds and the ISK cap, then update each balance and journal in the same SQL statement (`dotnet/src/Eve.Wallet/WalletTransactions.cs:45-95,178-213`). Corporate settlements use an operation lock, a receipt with a term fingerprint, stable wallet row ordering, and a single caller transaction (`WalletTransactions.Corporate.cs:119-145,148-209`). Market operation replay similarly checks the stored fingerprint before returning its result. This is distinct from two new client CallIDs with the same order terms, which must each apply once.

### Courier contract reward, collateral, deposit, and item custody

**Verdict: false positive for partial completion settlement.** Contract creation moves offered items into escrow and writes reward, fees, tax, and deposit debits in one transaction (`dotnet/src/Eve.Host/Persistence/Contracts/PersistenceContractWriteModel.cs:40-164`). Courier acceptance validates and locks the contract and collateral wrap; a repeated acceptance by the same acceptor and wallet returns the existing accepted contract (`:320-400`). Completion locks the contract, moves the wrap contents, settles reward/collateral/deposit, changes contract status, and writes notifications before one commit (`:460-568`). The V24 client confirmation and calls are in `eve/client/script/ui/shared/neocom/contracts/contracts.py:345-430,476-522`; EveJS contract proxy/runtime handlers were traced in `server/src/services/_other/contractProxyService.js` and `server/src/services/contracts/contractRuntimeState.js`. No new amount or retry defect was confirmed.

### Insurance payout replay

**Verdict: false positive for a duplicate ISK payout.** The write model checks the settlement row by loss ID before calculating or applying a payout. A previously settled loss returns `AlreadyApplied` and may resend the payout notification for recovery, but it does not write another wallet credit (`dotnet/src/Eve.Host/Persistence/Insurance/PersistenceInsuranceWriteModel.cs:195-221`). The first payout, contract state, and loss settlement are committed together, then the committed balance and notification are announced (`:249-281`). V24 calls the bound insurance service for `InsureShip` and `UnInsureShip` in `eve/client/script/ui/station/insurance/base_insurance.py:96-111,395-402,519-550`; EveJS routes those methods through `insuranceService.js:69-89` and settles payout operations through `insuranceRuntime.js:860-962`. Resending a notification after a duplicate loss event is an at-least-once recovery behavior; no second payout is applied.

### LP store required items and purchase settlement

**Verdict: false positive for charging without granting, or consuming the wrong required-item quantity.** The V24 UI serializes offer acceptance locally (`lpStoreSvc.py:240-262`) and calculates required inventory using the largest non-singleton stack of each type (`:306-318`). `LpStorePurchases.TakeAsync` follows that rule, checks multiplication overflow, locks balances and stacks, applies LP and ISK costs, consumes requirements, creates the reward, inserts the purchase record, then commits before announcing balances (`dotnet/src/Eve.Services.Wallet/LpStorePurchases.cs:98-200,217-263`). EveJS delegates `TakeOfferForCharacter` and `TakeOfferForCorporation` from `server/src/services/evermarks/lpStoreMgrService.js:383-407`. Existing LP stack findings in `audit-fix-planet-lpstore.md` are excluded from this audit.

## Verification limits

- Source comparisons used `/home/user/eve-logic` and `/home/user/evejs-261108.1`; the EveJS source paths were traced, but an EveJS executable scenario was not run.
- The real Host wire test ran against PostgreSQL database `eve_p6` with official SDE build 3396210 and mission SDE build 3405148. The Host was registered and used persisted market, wallet, and inventory stores. No graphical V24 client was run.
- The focused regression was first run before the fix and failed on the second distinct CallID with the former repeated-placement refusal. It passed after the fix: 1 passed, 0 failed, 0 skipped.
- `Eve.Market.Tests` Release: 149 passed, 0 failed, 1 skipped. The skip was the opt-in 10,000-order benchmark; that benchmark was then run separately with `EVE_MARKET_BENCH=1` and passed (1 passed, 0 failed, 0 skipped).
- Related Host Release slice (`MarketHostTests`, `MarketTradeHostWireTests`, `MarketRpcHostWireTests`, `MarketProxyFailureWireTests`): 19 passed, 0 failed, 0 skipped, on `eve_p6`.
- Release builds with `-warnaserror` succeeded with 0 warnings and 0 errors for `Eve.Services.Market`, `Eve.Market.Tests`, and `Eve.Host.Tests`.
