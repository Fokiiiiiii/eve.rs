# Market and direct-trade Host wire coverage handoff (CX10)

Updated: 2026-10-25

## Scope and result

This batch covers RPC-02081, RPC-02082, RPC-02083, RPC-02085, RPC-02086, RPC-02954 and RPC-02955. The production handlers already existed and are registered by Host composition; this change adds one focused DB-backed regression that calls the registered `marketProxy` and `trademgr` services, checks client reply forms, reads committed market/trade state, and observes the target trade notification.

The new test is `Eve.Host.Tests.MarketTradeHostWireTests.Registered_market_and_trade_calls_reply_and_update_persisted_state`. It uses a unique `EVE_TRADE_SCHEMA` alongside the other Host scratch schemas. The batch does not change gameplay logic.

## V24.01 caller and consumer evidence

The entire `/home/user/eve-logic` tree was searched for each method name and all service callers and downstream consumers were followed:

| Row | Caller, consumer and client-visible contract |
|---|---|
| RPC-02081 `ModifyCharOrder` | `eve/client/script/ui/services/marketsvc.py:284-289` forwards order ID, new price, side, location, shown price/range/volume and issue date. The modify window invokes `marketQuote.ModifyOrder` at `eve/client/script/ui/shared/market/buyThisTypeWindow.py:620`; market order menus reach it through `entries.py:124,148` and `orders.py:277,310`. The RPC result is ignored; the market order change notification updates the displayed order. |
| RPC-02082 `ModifyPlexCharOrder` | `marketsvc.py:290-295` forwards the same order snapshot through `ModifyPlexOrder`; the PLEX modification window calls it at `buyThisTypeWindow.py:618`. Its return is ignored; the order-change notification is the visible result. |
| RPC-02083 `PlaceBuyOrder` | `marketsvc.py:255-267` forwards the nine placement values and ignores the RPC result, then emits `OnClientEvent_OrderPlaced`. The principal UI caller is `buyThisTypeWindow.py:700`; the developer slash service also calls it at `eve/devtools/script/svc_slash.py:1462`. |
| RPC-02085 `PlacePlexSellOrder` | `marketsvc.py:277-279` returns the server result to `sellMulti.py:463`, where `wereOrdersCreated` controls the success follow-up. The server boolean therefore remains client-visible. |
| RPC-02086 `StartupCheck` | `eve/client/script/ui/shared/market/quote.py:62-77` calls `marketProxy.StartupCheck`, caches a recent refusal and rethrows the same UserError. `quote.py:254-283` calls this before opening market trade UI, and `eve/client/script/ui/services/menuSvcExtras/marketMenu.py:10-21` is another full-tree call path. Success has no consumed payload. |
| RPC-02954 and RPC-02955 `InitiateTrade` | `eve/client/script/ui/station/pvptrade/pvptradesvc.py:34-35` calls the service and passes the returned session to the window. The window calls `List` at `pvptradesvc.py:25,43-50` and reads the row in `pvptradewnd.py:44-55` (container ID and traders); `invControllers.py:1664` consumes the list's items. The peer receives `OnTradeInitiate`, handled at `pvptradesvc.py:41-50`. Callers reach this path through `tradeUtil.py:7-15`, invoked by `userentry.py:622-634`, `chat/client/member_list_entry.py:405-421`, and `xmppchatclient/xmppchatuserentry2.py:405-422`. The separate `spacecomponents/client/components/itemtrader_legacy.py:20-29` method with the same local name is not a `trademgr` RPC. |

For these server calls the client consumes the two market `None` replies, the PLEX sell boolean, and the trade moniker followed by its bound `List` row. It also consumes the peer's trade-initiation notification.

## EveJS v0.12.9 comparison

- `server/src/services/market/marketProxyService.js:7043-7272` modifies the persisted order and returns null; lines `7274-7276` delegate `ModifyPlexCharOrder` to that behavior.
- `marketProxyService.js:6858-6913` places buy orders and returns null on success; `7009-7041` sells PLEX and returns whether it filled or created an order; `6181-6189` returns null after StartupCheck.
- `server/src/services/trade/tradeMgrService.js:1847-1946` validates an online peer at the same docked location, persists an open trade, sends `OnTradeInitiate` to the peer, and returns a moniker.
- eve.rs matches these response and notification contracts. `dotnet/src/Eve.Host/Rpc/EconomyRpc.cs:36-43` registers `MarketProxyService`; `dotnet/src/Eve.Host/Rpc/StationRpc.cs:36-44` supplies `TradeMgrService`, and `dotnet/src/Eve.Host/Hosting/HostComposition.cs:279` registers the client's `tradeMgr` alias.

## Verification

Compile check passed: `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --no-restore -p:UseSharedCompilation=false -p:BuildProjectReferences=false -m:1` (0 warnings, 0 errors). The focused Host test is DB-backed and was not run locally because the task forbids local PostgreSQL use. Dispatch it on Actions with `Eve.Host.Tests` and filter `FullyQualifiedName~Registered_market_and_trade_calls_reply_and_update_persisted_state`. No graphical-client session was used.
