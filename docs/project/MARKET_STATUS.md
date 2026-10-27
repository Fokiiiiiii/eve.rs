# Market status

Updated 2026-10-27. This status follows the V24.01 call sites, current Host wiring, pinned SDE and EVE University market rules.

## Built

- `MarketTrading` and `MarketProxyService` persist station and eligible Upwell structure orders, NPC supply, escrow, item custody, fills, cancellations, modifications, expiries and wallet journals.
- Order placement applies duration, range, minimum-volume, visibility and order-count rules. Buy Multiple executes its lines in one serializable transaction; duration-zero requests only fill available sellers and leave unfilled quantity unposted. A duration-zero `PlaceBuyOrder` with no match returns the client refusal.
- Corporate order reads resolve current persisted corporation membership and require the persisted Accountant role.
- NPC order import reads captured market snapshots for SDE stations. History and transaction reads follow the client response shape and persisted fills.
- NPC station broker commission uses Broker skill, factional-warfare upgrades and persisted standings. Seller tax uses Accounting. Upwell broker fees combine the owner rate and 0.5% SCC surcharge under one 100 ISK minimum; the accounting entries remain separate.
- Structure access, per-character visibility, large-structure eligibility, order placement and matching, wallet and item settlement, cancellation, modification and restart persistence have Host coverage.

## Open

- Native graphical-client acceptance remains open. The NPC headless walk does not verify the structure market through the graphical client.
- CI still needs to verify the latest structure-market rollback, combined broker-minimum, and duration-zero buy regressions. Do not count the duration-zero database scenarios as passing until their Actions run succeeds.

## Wire evidence

The best ask row shape is resolved from the V24.01 client source. `marketutil/__init__.py:7-10` passes each row to `BestByOrder(*order)`, while `carbon/common/script/sys/row.py:23-36,69-70` supports named attribute reads, integer indexing and sequence length on `util.Row`. The client uses both paths: `eve/client/script/ui/shared/market/quote.py:95-100` reads `ask.typeID`, and `eve/client/script/ui/shared/market/marketbase.py:653` converts the best-order rows to tuples. `MarketWire.BestByOrderRow` emits `util.Row` with matching `header` and `line` values in `dotnet/src/Eve.Services.Market/MarketWire.cs:25-30`.

## Regression evidence

The two-account station headless walk passed on `eve_p3`; it checks transfer, tax, broker fee, wallet journals, partial fill, modification refusal and cancellation. Host tests cover structure market access, fee charging, failed-transaction rollback, item custody, settlement and restart recovery. The focused combined-minimum rule test passes without a database. The new duration-zero buy database scenarios await Actions verification.
