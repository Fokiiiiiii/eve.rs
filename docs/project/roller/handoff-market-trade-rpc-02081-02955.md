# Market and direct-trade Host RPC coverage: RPC-02081–02955

## Client contract and call flow

The supplied RPC-02954 and RPC-02955 rows point to the same V24.01 service call, `trademgr.InitiateTrade(charID)`, at `eve/client/script/ui/station/pvptrade/pvptradesvc.py:34-37`.

- RPC-02081: `marketsvc.py:285-289` sends `ModifyCharOrder(orderID, newPrice, bid, stationID, solarSystemID, price, range, volRemaining, issueDate)`; the return is ignored. `marketsvc.py:113-127` and `marketbase.py:537-549` consume `OnOwnOrdersChanged` to refresh the order views.
- RPC-02082: `marketsvc.py:291-295` sends the same nine arguments to `ModifyPlexCharOrder`; its return is ignored and the same event refreshes the view. The PLEX order UI uses this method.
- RPC-02083: `marketsvc.py:256-268` sends the nine-argument `PlaceBuyOrder`; its return is ignored, then the client scatters `OnClientEvent_OrderPlaced`. The buy window reaches the service through `buyThisTypeWindow.py:543-547,647-702`.
- RPC-02085: `marketsvc.py:278-279` sends `PlacePlexSellOrder(item, useCorp, duration, expectedBrokersFee)` and returns its boolean. `sellMulti.py:420-470` consumes the result. The PLEX vault builds a station/structure hangar item and opens the sell flow in `plexVault.py:142-149,508-510` and `vault_actions.py:234-237`.
- RPC-02086: `quote.py:63-77` invokes zero-argument `StartupCheck`. Success (`None`) is ignored; a UserError is surfaced and cached for two minutes. The buy path calls this gate at `quote.py:254-283` (line 272).
- RPC-02954 and RPC-02955: `pvptradesvc.py:34-37` returns a trade moniker. The initiator reads `tradeSession.List().tradeContainerID` at lines 24-32; the target does so at lines 42-50. `pvptradewnd.py:45-62` consumes the List row's `traders`, `tradeContainerID`, money and items. Drag/drop callers route through `tradeUtil.py:8-15`, called by `userentry.py:632-634`, `member_list_entry.py:418-421`, and `xmppchatuserentry2.py:419-422`.

## EveJS and eve.rs behavior

EveJS v0.12.9 returns `null` from `PlaceBuyOrder` and order modification (`server/src/services/market/marketProxyService.js:6858-6913,7043-7276`), a boolean from `PlacePlexSellOrder` (`:7009-7041`), and `null` on successful `StartupCheck` (`:6181-6189`). `ModifyPlexCharOrder` delegates to `ModifyCharOrder` (`:7274-7276`). Trade initiation persists the new trade, returns its moniker and notifies the target (`server/src/services/trade/tradeMgrService.js:1847-1946`).

eve.rs preserves these client-visible reply shapes and state effects: `StartupCheck` returns `None` (`dotnet/src/Eve.Services.Market/MarketProxyService.cs:155-156)); `PlaceBuyOrder` returns `None` (`:469-485)); order modification returns `None` (`:545-565)); PLEX sale returns a boolean (`:608-615)); and PLEX modification delegates to the shared mutation (`:619-620`). `trademgr.InitiateTrade` returns the trade moniker and queues the peer notification after trade creation (`dotnet/src/Eve.Services.Station/TradeMgrService.cs:11-40`).

## Regression coverage

- `tools/headless-client/tests/test_market.py::test_a_seller_and_a_buyer_trade_through_the_market` exercises `PlaceBuyOrder` and the client-shaped `ModifyCharOrder` refusal, then confirms the unchanged price through the live Host.
- `tools/headless-client/tests/test_market_trade_rpc_coverage.py::test_plex_sell_modify_and_startup_match_the_v24_wire_contract` exercises StartupCheck, PLEX sell result/order/notification, the full PLEX modification argument list, refusal and unchanged price through the live Host.
- `tools/headless-client/tests/test_walk_trade.py::test_two_pilots_trade_a_cockroach_for_isk_and_the_database_holds_the_result` covers a valid InitiateTrade moniker and subsequent bound trade flow. `test_market_trade_rpc_coverage.py::test_initiate_trade_moniker_lists_the_client_consumed_row_and_notifies_peer` checks List fields and the target notification through the live Host.

These are headless Host checks, not graphical-client acceptance. The PLEX modification walk checks a valid request shape and the five-minute refusal before the order is eligible to change; the shared market service tests cover modification rules after the delay.
