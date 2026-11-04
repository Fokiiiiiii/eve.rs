# Market history, statistics, order changes, and structure markets

## Audit result

I did not confirm a new correctness defect in the traced core flows, so this audit makes no runtime code change. Existing regressions cover the client-visible paths described below. This is an audit handoff, not a claim that every market feature or the graphical client was exercised.

## V24.01 client paths and consumers

- The market UI uses the player's selected range in `eve/client/script/ui/shared/market/quote.py:76-81`; a station-range query flows through `marketbase.py:661-665` to `MarketQuote.GetStationAsks`. The wrapper accepts either `session.stationid` or `session.structureid` at `eve/client/script/ui/services/marketsvc.py:296-300`. System-range asks use `GetSystemAsks`; broader ranges use `GetRegionBest` (`marketbase.py:661-670`).
- Sell and immediate buy order creation flow through `marketsvc.py:255-278`. Order edits pass order ID, new price, side, venue, system, old price, range, remaining volume, and issue date at `marketsvc.py:281-293`. In particular, the client supplies old price and issue date for stale-view checks; the Host checks these against authoritative order state.
- Price-history callers are `marketsvc.py:318-338`: the chart requests history for a set of type IDs or separate old/new rowsets, with PLEX on its dedicated methods. `GetHistoryRowList` merges the rowsets, fills missing days, and `GetAveragePrice` uses `avgPrice × volume` over its time window (`marketsvc.py:342-386`). Market order history is consumed by `marketHistory.py:136-162`, where the UI filters personal and corporation orders.
- `OnOwnOrdersChanged` invalidates the client's order, book, station/system ask, PLEX, and completed-order caches, then scatters `OnMarketEscrowChanged` (`marketsvc.py:113-127`). The UI's order windows consume that event in `marketbase.py:537-549`, `orders.py:67-71`, and `marketHistory.py:62-66`.
- Personal and corporation transaction views call `CharGetTransactions` and `CorpGetTransactions` in `eve/client/script/ui/shared/marketSvc.py:20-29`; the client reads transaction date, type, quantity, price, station, buyer/seller, and account fields at `:36-64`. The corporation path handles access denial using `CrpAccessDenied` and its reason tuple.

## Server path and mechanics cross-check

- Production registration is through `CharacterServicesComposition.EconomyRpc`; the prior market-proxy map `docs/project/service-audit/DEEPCHECK_marketProxy.md` records the route inventory and Host wire checks.
- History rows are computed from committed market fills by region and UTC day: low/high are fill prices, average is gross divided by quantity, volume is summed quantity, and orders is the fill count (`dotnet/src/Eve.Market/MarketStore.cs:428-445`). `MarketHistoryWire.Pair` splits prior days from the current day and emits the client rowset columns and FILETIME day boundary (`dotnet/src/Eve.Services.Market/MarketWire.cs:39-66`). This matches the client-side weighted average and row shape.
- Modifying an order locks it after acquiring the market book lock, checks ownership, optional stale price and issue time, five-minute delay, venue access and range, then charges the broker/SCC components and matches against the book in one transaction (`dotnet/src/Eve.Market/MarketTrading.cs:463-578`). Fills, escrow, changed orders and notifications are recorded before the transaction commits; the Host publishes the receipt afterward through `MarketNotifier`.
- Structure market access is required for station asks and order placement (`MarketTrading.cs:105-129,131-157`). The structure market test also checks an inaccessible pilot's read/place/cancel errors, accessible structure asks and orders, settlement, and restart recovery (`dotnet/tests/Eve.Host.Tests/MarketHostTests.cs:300-575`). Structure IDs remain 64-bit on book and ask wire rows (`dotnet/tests/Eve.Market.Tests/MarketStructureWireTests.cs:8-75)).
- Current EveJS uses `Handle_GetStationAsks` with the station/structure session ID and an explicit structure-market access scope (`/home/user/evejs-261028.1/src/server/src/services/market/marketProxyService.js:6306-6347)); its current order modification path is `:7161-7398`. Its market-state helper `marketState.js:79-87` is a canned bootstrap response, so it is not a suitable oracle for live fills or statistical aggregates.
- EVE University describes history's volume and price graph as trade activity ([Market](https://wiki.eveuniversity.org/Market)), and documents NPC and Upwell broker fees plus relist fee calculation ([Trading](https://wiki.eveuniversity.org/Trading)). The current `MarketRules` fee paths implement the fixed 0.5% SCC component for structures and apply the relist discount separately.

## Checks run

- `eve_p8`, through `tools/dev/db-lock.sh`: `MarketHistoryTests`, `ModificationRulesTests`, and `TradingTests`: **31 passed, 0 failed, 0 skipped**.
- `eve_p8`, through `tools/dev/db-lock.sh`: `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart`: **1 passed, 0 failed, 0 skipped**.
- The Host test's Fortizar type is read from the local pinned-SDE database at runtime; it does not embed a captured structure or market payload.

## Not checked

No graphical-client session was performed. The audit did not execute every market RPC, corporation transaction permission combination, or every structure access-list tier.
