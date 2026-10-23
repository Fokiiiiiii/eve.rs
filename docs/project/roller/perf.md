# Performance audit: source-level findings

## Scope and method

This pass inspected the Host/character entry and market/inventory persistence paths through CBM symbol results and Serena source reads. PostgreSQL was explicitly excluded by this job's instruction, so this report contains no database timings, EXPLAIN plans, table cardinalities, or before/after measurements. No database-index change is justified without those measurements.

## Query shapes found

| Path | Source behavior | Measurable from code |
|---|---|---|
| Character selection | `PgCharacterStore.ForAccountAsync` selects the account's non-deleted characters ordered by id. `characters_account_idx(account_id)` supports account filtering; account character lists are naturally bounded by character slots, while deletion filtering and order may need a sort. | One SQL command per list call. |
| Market book | `MarketStore.BookAsync` opens one connection and a repeatable-read transaction, then issues one ordered query for asks and one for bids. `orders_asks` and `orders_bids` filter by region/type/side/state/remaining volume and order by price/station/id. The read also orders by issue time before id, so the index ordering does not fully match the query ordering. | Two SQL commands per book read, one shared snapshot. |
| Market history | `GetMarketOrderHistory` asks `MarketTrading.OwnerOrdersAsync` for personal and corporation history separately, then combines the results. | Two owner-order calls per history request, plus the read-triggered expiry sweep. |
| Inventory | `PgItemStore` has owner and location indexes. The source includes owner-wide item reads ordered by id and location/owner/type item reads. | Index selectivity and rows scanned depend on the seeded distribution; source inspection cannot establish the actual plan. |

## Candidate follow-up

Capture `EXPLAIN (ANALYZE, BUFFERS)` for market book reads and the station inventory browse path on the permitted seeded lane, including ask-heavy, bid-heavy, and empty books. Compare the actual sort and row counts against the indexes before adding an index or changing the query. Record p50/p95 over repeated warm and cold reads. For market history, measure the expiry sweep separately from the two owner-order reads before considering a combined read API; preserve the repeatable-read snapshot and existing wire ordering.

## Changes and verification

No production code or migration changed in this pass: the required seeded-database profile was excluded by the task's no-PostgreSQL instruction, so there was no measured basis to select a worst query or claim a performance improvement. No tests or builds were run because no code changed.
