# Market history, statistics, order changes, and structure markets

## Audit result

The initial market audit missed a completed-order history authorization defect. Follow-up review found `GetMarketOrderHistory` appended corporation-wallet orders for every character. The service now resolves current persisted Accountant authority first, always reads personal history, and only reads current-corporation history when authorized. This is an audit handoff, not a claim that every market feature or the graphical client was exercised.

## V24.01 client paths and consumers

- The market UI uses the player's selected range in `eve/client/script/ui/shared/market/quote.py:76-81`; a station-range query flows through `marketbase.py:661-665` to `MarketQuote.GetStationAsks`. The wrapper accepts either `session.stationid` or `session.structureid` at `eve/client/script/ui/services/marketsvc.py:296-300`. System-range asks use `GetSystemAsks`; broader ranges use `GetRegionBest` (`marketbase.py:661-670`).
- Sell and immediate buy order creation flow through `marketsvc.py:255-278`. Order edits pass order ID, new price, side, venue, system, old price, range, remaining volume, and issue date at `marketsvc.py:281-293`. In particular, the client supplies old price and issue date for stale-view checks; the Host checks these against authoritative order state.
- Price-history callers are `marketsvc.py:318-338`: the chart requests history for a set of type IDs or separate old/new rowsets, with PLEX on its dedicated methods. `GetHistoryRowList` merges the rowsets, fills missing days, and `GetAveragePrice` uses `avgPrice × volume` over its time window (`marketsvc.py:342-386`). `GetMarketOrderHistory` is called by `MarketQuote.GetMarketOrderHistory` (`marketsvc.py:393-394`) and the sell, buy, and populate paths in `marketHistory.py:136-160`. The history view checks `isCorp`, `duration`, and side (`bid`) while building rows (`marketHistory.py:195-221`); it then reads `orderState`, `volRemaining`, `lastStateChange` (falling back to `issueDate + duration × DAY` when absent), `issueDate`, `typeID`, `stationID`, `regionID`, and `price` to render status and columns (`:229-267`). `isCorp` also controls the corporation/personal label and owner filter (`:195-205,254-267`). The personal history tab stays available when the accountant-only corporate-orders tab is hidden (`marketOrdersWnd.py:39-56`).
- `OnOwnOrdersChanged` invalidates the client's order, book, station/system ask, PLEX, and completed-order caches, then scatters `OnMarketEscrowChanged` (`marketsvc.py:113-127`). The UI's order windows consume that event in `marketbase.py:537-549`, `orders.py:67-71`, and `marketHistory.py:62-66`.
- Personal and corporation transaction views call `CharGetTransactions` and `CorpGetTransactions` in `eve/client/script/ui/shared/marketSvc.py:20-29`; the client reads transaction date, type, quantity, price, station, buyer/seller, and account fields at `:36-64`. The corporation path handles access denial using `CrpAccessDenied` and its reason tuple.

## Server path and mechanics cross-check

- Production registration is `new MarketProxyService(...)` in `CharacterServicesComposition.EconomyRpc` (`dotnet/src/Eve.Host/Rpc/EconomyRpc.cs:47`); `Eve.Host.Tests.MarketHostTests` exercises Host-level market routes. The prior market-proxy map `docs/project/service-audit/DEEPCHECK_marketProxy.md` records the route inventory and wire checks.
- `CorporationStore.CanReadCorporationMarketOrdersAsync` derives the current corporation, persisted member, and Accountant role from stored character and member rows (`dotnet/src/Eve.Corporations/CorporationStore.cs:511-519`). `GetMarketOrderHistory` calls this before any corporation `OwnerOrdersAsync` read (`dotnet/src/Eve.Services.Market/MarketProxyService.cs:420-432`); `OwnerOrdersAsync` itself selects the corporation wallet from persisted character affiliation but has no role predicate (`dotnet/src/Eve.Market/MarketTrading.cs:716-729`). A false authority result keeps the personal history path and skips the corporation read. The rowset and `sessionInfo:"charid"` cached response remain unchanged.
- History rows are computed from committed market fills by region and UTC day: low/high are fill prices, average is gross divided by quantity, volume is summed quantity, and orders is the fill count (`dotnet/src/Eve.Market/MarketStore.cs:428-445`). `MarketHistoryWire.Pair` splits prior days from the current day and emits the client rowset columns and FILETIME day boundary (`dotnet/src/Eve.Services.Market/MarketWire.cs:39-66`). This matches the client-side weighted average and row shape.
- Modifying an order locks it after acquiring the market book lock, checks ownership, optional stale price and issue time, five-minute delay, venue access and range, then charges the broker/SCC components and matches against the book in one transaction (`dotnet/src/Eve.Market/MarketTrading.cs:463-578`). Fills, escrow, changed orders and notifications are recorded before the transaction commits; the Host publishes the receipt afterward through `MarketNotifier`.
- Structure market access is required for station asks and order placement (`MarketTrading.cs:105-129,131-157`). The structure market test also checks an inaccessible pilot's read/place/cancel errors, accessible structure asks and orders, settlement, and restart recovery (`dotnet/tests/Eve.Host.Tests/MarketHostTests.cs:300-575`). Structure IDs remain 64-bit on book and ask wire rows (`dotnet/tests/Eve.Market.Tests/MarketStructureWireTests.cs:8-75)).
- Current EveJS 261028.1 gates both `Handle_GetCorporationOrders` and `Handle_GetMarketOrderHistory` through persisted corporation authority and the Accountant role (`/home/user/evejs-261028.1/src/server/src/services/market/marketProxyService.js:6597-6679`; helper `/home/user/evejs-261028.1/src/server/src/services/corporation/corporationAuthority.js:43-81,111-121`). Its station-asks handler also checks structure-market access (`marketProxyService.js:6306-6347`), and its current order-modification path is `:7161-7398`. Its market-state helper `marketState.js:79-87` is a canned bootstrap response, so it is not a suitable oracle for live fills or statistical aggregates.
- EVE University describes history's volume and price graph as trade activity ([Market](https://wiki.eveuniversity.org/Market)), and documents NPC and Upwell broker fees plus relist fee calculation ([Trading](https://wiki.eveuniversity.org/Trading)). The current `MarketRules` fee paths implement the fixed 0.5% SCC component for structures and apply the relist discount separately.

## Checks run

- `eve_p8`, through `tools/dev/db-lock.sh`: `MarketHistoryTests`, `ModificationRulesTests`, and `TradingTests`: **31 passed, 0 failed, 0 skipped**.
- `eve_p8`, through `tools/dev/db-lock.sh`: `MarketHostTests.The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart`: **1 passed, 0 failed, 0 skipped**.
- The Host test's Fortizar type is read from the local pinned-SDE database at runtime; it does not embed a captured structure or market payload.

## Not checked

No graphical-client session was performed. The audit did not execute every market RPC, corporation transaction permission combination, or every structure access-list tier.

## Completed-order history authorization handoff (2026-11-06)

- Persisted authority is resolved before the corporation-history query; session `corpid` / `corprole` are not used to grant access. Former members resolve to their persisted current corporation. Personal history is returned for every character.
- PostgreSQL RPC regressions in `MarketRpcTests` create standing buy orders, cancel them, and read committed non-open rows. They cover an Accountant with stale session roles seeing personal plus corporation rows, a plain member and Trader-only member seeing only personal rows despite stale Accountant session roles, and a former member with a stale former-corporation session seeing only personal rows. Row count and field assertions preserve the 21-column rowset, `isCorp`, personal `charID`, corporate wallet division `keyID`, type, and non-open `orderState`.
- V24.01 consumers: `eve/client/script/ui/services/marketsvc.py:393-394`; `eve/client/script/ui/shared/market/marketHistory.py:136-160,195-205`; `eve/client/script/ui/shared/market/marketOrdersWnd.py:39-56`.
- The matching EVE University mechanic states that Accountant authority covers corporation wallet transactions; Trader-only authority does not grant that access ([Managing corporation members](https://wiki.eveuniversity.org/Managing_corporation_members)).

## Checks run in cx7 and cx4 continuation (2026-11-06)

- `dotnet build dotnet/src/Eve.Services.Market/Eve.Services.Market.csproj -p:UseSharedCompilation=false`: succeeded, 0 warnings and 0 errors.
- `dotnet build dotnet/tests/Eve.Market.Tests/Eve.Market.Tests.csproj -p:UseSharedCompilation=false`: succeeded, 0 warnings and 0 errors.
- On `eve_p7` through `tools/dev/db-lock.sh`: focused `Market_order_history` RPC tests: 3 passed, 0 failed, 0 skipped.
- On `eve_p7` through `tools/dev/db-lock.sh`: `MarketRpcTests`, `MarketCoherenceTests`, `MarketHistoryTests`, `MarketTransactionReadTests`, and `ProtocolTests`: 49 passed, 0 failed, 0 skipped.
- On `eve_p7` through `tools/dev/db-lock.sh`: `Eve.Host.Tests.MarketHostTests`: 4 passed, 0 failed, 0 skipped.
- On `eve_p7` through `tools/dev/db-lock.sh`: `Eve.Services.Tests.DispatchGoldenTests`: 200 passed, 0 failed, 0 skipped.

- In the clean `/home/user/wt-cx4-market` continuation, builds of `Eve.Services.Market`, `Eve.Market.Tests`, `Eve.Host.Tests`, and `Eve.Services.Tests` succeeded with 0 warnings and 0 errors.
- On `eve_p4`, through `tools/dev/db-lock.sh`: focused `Market_order_history` RPC cases: 3 passed, 0 failed, 0 skipped; neighboring `MarketRpcTests`, `MarketCoherenceTests`, `MarketHistoryTests`, `MarketTransactionReadTests`, and `ProtocolTests`: 49 passed, 0 failed, 0 skipped.
- On `eve_p4`, through `tools/dev/db-lock.sh`: `MarketHostTests` and `MarketProxyFailureWireTests`: 4 passed, 0 failed, 0 skipped. The first attempt stopped at Host startup because the newly prepared database lacked `sde_mission.skinrComponentPointValues`; after recreating `eve_p4` from pinned-SDE `eve_p3`, all four passed.
