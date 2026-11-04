# Market status

Updated 2026-10-29. This status follows the V24.01 call sites, current Host wiring, pinned SDE and EVE University market rules.

## Built

- `MarketTrading` and `MarketProxyService` persist station and eligible Upwell structure orders, NPC supply, escrow, item custody, fills, cancellations, modifications, expiries and wallet journals.
- Order placement applies duration, range, minimum-volume, visibility and order-count rules. Buy Multiple executes its lines in one serializable transaction; duration-zero requests only fill available sellers and leave unfilled quantity unposted. A duration-zero `PlaceBuyOrder` with no match returns the client refusal.
- Corporate order reads resolve current persisted corporation membership and require the persisted Accountant role.
- NPC order import reads captured market snapshots for SDE stations. History and transaction reads follow the client response shape and persisted fills.
- NPC station broker commission uses Broker skill, factional-warfare upgrades and persisted standings. Seller tax uses Accounting and the V24.01 Zarzakh pirate-faction discount. Upwell broker fees use separate 100 ISK broker and 25 ISK SCC minimums; the accounting entries remain separate and factional-warfare upgrades adjust the structure owner rate.
- Structure access, per-character visibility, large-structure eligibility, order placement and matching, wallet and item settlement, cancellation, modification and restart persistence have Host coverage.

## Open

- Native graphical-client acceptance remains open. The NPC headless walk does not verify the structure market through the graphical client.

## Wire evidence

The best ask row shape is resolved from the V24.01 client source. `marketutil/__init__.py:7-10` passes each row to `BestByOrder(*order)`, while `carbon/common/script/sys/row.py:23-36,69-70` supports named attribute reads, integer indexing and sequence length on `util.Row`. The client uses both paths: `eve/client/script/ui/shared/market/quote.py:95-100` reads `ask.typeID`, and `eve/client/script/ui/shared/market/marketbase.py:653` converts the best-order rows to tuples. `MarketWire.BestByOrderRow` emits `util.Row` with matching `header` and `line` values in `dotnet/src/Eve.Services.Market/MarketWire.cs:25-30`.

## Regression evidence

The two-account station headless walk passed on `eve_p3`; it checks transfer, tax, broker fee, wallet journals, partial fill, modification refusal and cancellation. Host tests cover structure market access, fee charging, failed-transaction rollback, item custody, settlement and restart recovery. Focused rule tests cover separate broker/SCC minimums, Zarzakh tax discounts and structure factional-warfare discounts without a database. The duration-zero buy and market notification regressions passed on Actions run `40857707780` for `a28f6cf9`: `Eve.Market.Tests` 142 passed, 0 failed, 0 skipped; `Eve.Host.Tests` 940 passed, 0 failed, 0 skipped. Push-triggered CI run `40857692666` also passed. The earlier selected run `40854638927` found a stale expected ID-0 notification row; `MarketRpcTests.Buy_multi_fills_available_quantity_without_posting_the_rest` now checks the empty changed-order list and passed on the corrected run.

`MarketTradeHostWireTests.Registered_market_and_trade_calls_reply_and_update_persisted_state` now also calls the registered `CancelCharOrder` RPC after modifying a resting buy order and verifies the persisted order is removed.

Immediate fills notify the client with an empty changed-order list. This preserves the client-side transaction cache reset and escrow signal without reporting an order ID 0 that does not exist. Client consumers are `marketSvc.py:32-35`, `marketsvc.py:113-127`, `marketHistory.py:61-65`, `orders.py:66-69`, `marketbase.py:536-546`, and `buyMultiFromBase.py:557-564`; the V24 buy call returns no order (`marketsvc.py:256-268`). EveJS 261028.1 still sends a synthetic row (`marketProxyService.js:5687-5702`); the compatibility deviation is recorded in `docs/reference/compat/DEVIATIONS.md`.

The DB-free notifier regression passed locally. No local database or graphical client was used.

V24.01 client evidence: `eve/client/script/ui/services/marketsvc.py:280-281` calls `CancelCharOrder(orderID, regionID)`; `eve/client/script/ui/shared/market/orders.py:276-310` offers cancellation for the caller's own order and routes it through `marketutils.CancelOffer`.

For this regression, `Eve.Host.Tests` compiled successfully and the selected non-database Host route test passed. The database cases passed on the Actions run above.
