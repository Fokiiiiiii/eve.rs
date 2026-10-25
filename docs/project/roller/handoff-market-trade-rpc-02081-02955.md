# Market and direct-trade Host RPC coverage: RPC-02081–02955

Updated: 2026-10-25 (CX10)

## Client contract and call flow

The supplied RPC-02954 and RPC-02955 rows point to the same V24.01 service call, `trademgr.InitiateTrade(charID)`, at `eve/client/script/ui/station/pvptrade/pvptradesvc.py:34-37`.

| RPC | All V24.01 call paths and consumers |
|---|---|
| RPC-02081 `ModifyCharOrder` | `marketsvc.py:285-289` sends the order ID, new price, side, location, shown price/range/volume and issue date. The RPC reply is ignored; `marketQuote.ModifyOrder` is called by `buyThisTypeWindow.py:620`, order menus route through `entries.py:124,148` and `orders.py:277,310`, and `marketsvc.py:113-127` plus `marketbase.py:537-549` refresh from `OnOwnOrdersChanged`. |
| RPC-02082 `ModifyPlexCharOrder` | `marketsvc.py:291-295` sends the same snapshot through `ModifyPlexOrder`; `buyThisTypeWindow.py:618` invokes it. The reply is ignored and the order-change event refreshes the view. |
| RPC-02083 `PlaceBuyOrder` | `marketsvc.py:256-268` forwards nine placement values, ignores the reply and scatters `OnClientEvent_OrderPlaced`. The buy window calls `BuyStuff` at `buyThisTypeWindow.py:700`; the developer slash path also calls it at `eve/devtools/script/svc_slash.py:1462`. |
| RPC-02085 `PlacePlexSellOrder` | `marketsvc.py:278-279` returns the boolean to `sellMulti.py:420-470`, where `wereOrdersCreated` controls follow-up behavior. The PLEX vault enters this sell flow from `plexVault.py:142-149,508-510` and `vault_actions.py:234-237`. |
| RPC-02086 `StartupCheck` | `quote.py:63-77` calls the zero-argument method, caches a recent UserError and rethrows it; success has no consumed payload. The buy path gates its UI at `quote.py:254-283` (line 272); `services/menuSvcExtras/marketMenu.py:10-21` is another caller. |
| RPC-02954 and RPC-02955 `InitiateTrade` | `pvptradesvc.py:34-37` calls the service and passes its moniker to the trade window. Initiator and target paths call `List` at `pvptradesvc.py:24-32,42-50`; `pvptradewnd.py:45-62` reads `tradeContainerID`, traders, money and items, and `invControllers.py:1664` reads the item list. `OnTradeInitiate` is consumed at `pvptradesvc.py:41-50`. Drag/drop reaches the service through `tradeUtil.py:7-15`, called by `userentry.py:622-634`, `chat/client/member_list_entry.py:405-421`, and `xmppchatuserentry2.py:405-422`. The local `ItemTrader.InitiateTrade` at `spacecomponents/client/components/itemtrader_legacy.py:20-29` is not this RPC. |

## EveJS v0.12.9 and Host path

EveJS returns `null` from `PlaceBuyOrder` and order modification (`server/src/services/market/marketProxyService.js:6858-6913,7043-7276)), a boolean from `PlacePlexSellOrder` (`:7009-7041)), and `null` on successful `StartupCheck` (`:6181-6189)). `ModifyPlexCharOrder` delegates to `ModifyCharOrder` (`:7274-7276)). Trade initiation validates a same-location online peer, persists an open trade, sends the target `OnTradeInitiate`, and returns a moniker (`server/src/services/trade/tradeMgrService.js:1847-1946)). eve.rs preserves these client-visible replies and state effects.

Host registration: `dotnet/src/Eve.Host/Rpc/EconomyRpc.cs:36-43` registers `MarketProxyService`; `dotnet/src/Eve.Host/Rpc/StationRpc.cs:36-44` supplies `TradeMgrService`; `dotnet/src/Eve.Host/Hosting/HostComposition.cs:279` registers the `tradeMgr` alias.

## Regression coverage

- `tools/headless-client/tests/test_market.py::test_a_seller_and_a_buyer_trade_through_the_market` covers a resting `PlaceBuyOrder` and client-shaped `ModifyCharOrder` refusal through the live Host.
- `tools/headless-client/tests/test_market_trade_rpc_coverage.py::test_plex_sell_modify_and_startup_match_the_v24_wire_contract` covers `StartupCheck`, the PLEX sell boolean/order/notification, and the complete PLEX modification argument list and refusal through the live Host.
- `tools/headless-client/tests/test_walk_trade.py::test_two_pilots_trade_a_cockroach_for_isk_and_the_database_holds_the_result` covers the initiation moniker and subsequent bound trade flow. `test_market_trade_rpc_coverage.py::test_initiate_trade_moniker_lists_the_client_consumed_row_and_notifies_peer` asserts List fields and the peer notification through the live Host.
- `dotnet/tests/Eve.Host.Tests/MarketTradeHostWireTests.cs::Registered_market_and_trade_calls_reply_and_update_persisted_state` calls the registered Host services, verifies market reply shapes and persisted buy/PLEX order modifications, then checks the trade moniker, bound List row, target notification and persisted open session. Its `traders` values are compared individually because `PyList` record equality compares the backing list identity.

These are Host/headless checks; no graphical-client session was used. Local compile-check passed with zero warnings and errors. The DB-backed focused Host test was not executed locally, per the task's PostgreSQL restriction. Actions run `40173762700` exposed the `PyList` structural comparison issue. Run `40174547762` then confirmed the trader IDs serialize as `PyInt`; the test assertions now expect those V24.01 wire types. Focused Actions rerun after this correction is pending.
