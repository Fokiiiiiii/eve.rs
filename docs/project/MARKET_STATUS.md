# Market status

Updated 2026-10-21. This status follows the V24.01 client call sites and the persisted Host implementation.

## Built

- `MarketTrading` and `MarketProxyService` persist player orders and NPC supply at SDE stations, escrow, item custody, fills, cancellations, modifications, expiries, and wallet journal entries.
- Order placement applies duration, range, minimum-volume, visibility, and order-count rules. Buy Multiple executes its lines in one serializable transaction.
- NPC order seeding imports a captured market snapshot for SDE stations. History reads aggregate persisted market fills by region and split the current day from earlier days in the client response.
- NPC station broker commission uses Broker skill, factional-warfare upgrades, and persisted faction and corporation standings. Sales tax uses Accounting; player-structure SCC surcharge is represented by the rule calculation.
- The two-account headless trade walk checks transferred ISK, both balance deltas, tax, broker fee, and SCC surcharge against persisted wallet journal rows.

## Open

- Player-structure order placement, reading, settlement, and custody are not wired into the market venue model. The implementation must authorize the market service from persisted structure state and profile settings, resolve structure system and region, and settle goods and wallet entries at that structure.
- The headless two-account walk needs execution against a running Host for graphical-client-adjacent acceptance. NPC seed import tests exercise captured artifacts; they do not validate a live seeded book.
- The market ask response contract has a separate client decoding question documented in `service-response-audit/market-asks-fix.md`.

## Regression evidence

The `Eve.Market.Tests` PostgreSQL suite passed on eve_p3 (122 passed, 1 benchmark skipped). The focused `RulesTests` selection passed 7 tests with 4 delay-dependent cases skipped. Host registration and restart checks are in `dotnet/tests/Eve.Host.Tests/MarketHostTests.cs`; the two-account walk is `tools/headless-client/tests/test_market.py::test_a_seller_and_a_buyer_trade_through_the_market` and still needs a running-host execution.
