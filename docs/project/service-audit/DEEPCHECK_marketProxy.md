# marketProxy 深掘り監査（2026-10-27）

## 結論

V24.01 の eve-logic 全体から、marketProxy への直接 RPC を 26 個確認した。bound-object / moniker 経由の marketProxy 呼び出しは見つからない。Host は CharacterServicesComposition.EconomyRpc で実サービスを登録する。未使用の追加 export は GetMarketGroups。

修正した gap はバックエンド PostgreSQL 障害のエラー形。市場停止時の NpgsqlException を EveJS と同じ CustomInfo UserError（info は “Market is currently offline, elysian says sorry!”）に変換する。service mapper を strict-RPC 判定より先に適用し、既知の市場停止は本番相当の strict 設定でも generic failure にならない。未知の例外は引き続き strict failure になる。

## 呼び出しと wire shape

marketsvc.py は eve/client/script/ui/services/marketsvc.py、marketSvc.py は eve/client/script/ui/shared/marketSvc.py を指す。クライアントの通常引数はすべて positional。transport の machoVersion は Host が application kwargs から取り除く。引数変換は Integer / Int32 が PyInt と PyLong を受け、Money は PyInt / PyLong / PyReal を受ける。None は fee、日付、division key、変更時の期待値などクライアントが使う位置で受理する。GetHistoryForManyTypeIDs は client の Python set（wire 上の __builtin__.set）に加え、list / tuple も扱う。

| RPC | eve-logic の全直接呼び出し位置 | 引数と client 消費形状 | eve.rs の返答・状態 |
|---|---|---|---|
| StartupCheck | eve/client/script/ui/shared/market/quote.py:69 | 引数なし。呼出し側は UserError を捕捉（同:63-77）。 | market.orders の準備完了なら None。DB 接続不能・未準備は CustomInfo UserError。 |
| GetRegionBest | eve/client/script/ui/services/marketsvc.py:223-224 | 引数なし。 | typeID から best-order row への dict。読み取りのみ。 |
| GetPlexBest | marketsvc.py:226-227 | 引数なし。 | PLEX best-order row を持つ dict。読み取りのみ。 |
| PlaceBuyOrder | marketsvc.py:265 | stationID, typeID, price, quantity, range, minVolume, duration, useCorp, expectedBrokerFee の 9 positional。最終 fee は None 可（同:256-268）。 | 成功は None。MarketTrading が注文・wallet を永続化し、receipt publish 後に client cache を無効化して通知。 |
| BuyMultipleItems | marketsvc.py:270-271 | stationID, itemList, useCorp。itemList 要素は typeID / quantity / price / minVolume。 | 作成された注文 rowset を返す。MarketQuote は結果の typeID、buyMultiFromBase.py:305-307 は orderID を読む。各 receipt を publish。 |
| PlaceMultiSellOrder | marketsvc.py:275 | itemList, useCorp, duration, expectedBrokerFee。itemList の各要素に typeID / itemID / stationID / quantity / price。 | trade / resting order の有無を bool で返し、各 line の receipt を publish。 |
| PlacePlexSellOrder | marketsvc.py:278; sellMulti.py:430-464 | PLEX item, useCorp, duration, expectedBrokerFee。UI は corporate checkbox の値も渡せる。 | typeID 44992 を検証し bool を返す。MarketTrading に保存し receipt を publish。 |
| CancelCharOrder | marketsvc.py:281 | orderID, regionID の 2 positional。server と EveJS は orderID を使う。 | 成功は None。取消を永続化して cache invalidation と OnOwnOrdersChanged を送る。 |
| ModifyCharOrder | marketsvc.py:287 | orderID, newPrice, bid, stationID, solarSystemID, oldPrice, range, volRemaining, issueDate の 9 positional。server は id / price / oldPrice / issueDate を stale check に使い、期待値 None も許容。 | 成功は None。変更を永続化して cache invalidation と通知。 |
| ModifyPlexCharOrder | marketsvc.py:293 | ModifyCharOrder と同じ 9 positional。 | ModifyCharOrder と同じ処理へ委譲。 |
| GetStationAsks | marketsvc.py:299 | 引数なし。呼出し側は station / structure を先に検査（同:297-300）。 | typeID → best ask row の dict。読み取りのみ。 |
| GetSystemAsks | marketsvc.py:304 | 引数なし。呼出し側は solar system を検査（同:302-305）。 | typeID → best ask row の dict。読み取りのみ。 |
| GetHistoryForManyTypeIDs | marketsvc.py:318 | 1 引数は client の set。 | typeID → [old rowset, new rowset] の dict。読み取りのみ。 |
| GetPlexHistory | marketsvc.py:321 | 引数なし。 | PLEX typeID → [old, new] rowset の dict。読み取りのみ。 |
| GetPlexOldPriceHistory | marketsvc.py:334 | 引数なし。 | old history rowset。読み取りのみ。 |
| GetPlexNewPriceHistory | marketsvc.py:335 | 引数なし。 | new history rowset。読み取りのみ。 |
| GetOldPriceHistory | marketsvc.py:337 | typeID 1 引数。 | old history rowset。読み取りのみ。 |
| GetNewPriceHistory | marketsvc.py:338 | typeID 1 引数。 | new history rowset。読み取りのみ。 |
| GetCharOrders | marketsvc.py:388（GetMyOrders） | 引数なし。 | owner order rowset。UI は bid / price / volRemaining / escrow / stationID 等を読む（personalMarketOrders.py:41-75）。読み取りのみ。 |
| GetCorporationOrders | marketsvc.py:391; corpMarketOrders.py:10-11 | 引数なし。 | corporation order rowset。権限拒否は UserError。読み取りのみ。 |
| GetMarketOrderHistory | marketsvc.py:394 | 引数なし。 | 完了 order rowset。UI は isCorp を含め利用（marketHistory.py:136,141,159-162）。期限 sweep が状態を変えた場合は expiry notification を発行。 |
| GetCharEscrow | marketsvc.py:400-401（GetMyMarketEscrow） | 引数なし。 | iskEscrow / itemsEscrow の KeyVal。UI は exception を捕捉してゼロへ fallback（同:397-409）。expiry sweep が状態を変えた場合は通知。 |
| GetPlexOrders | marketsvc.py:730-731 | 引数なし。 | [asks, bids] tuple（cached method result）。読み取りのみ。 |
| GetOrders | marketsvc.py:733-734 | typeID 1 引数。 | [asks, bids] tuple（cached method result）。読み取りのみ。 |
| CharGetTransactions | eve/client/script/ui/shared/marketSvc.py:20-23 | fromDate は省略時 None。 | list of KeyVal。client は transactionDate / typeID / price / quantity / stationID / buyer・seller・account fields を読む（同:36-50, 52-64）。読み取りのみ。 |
| CorpGetTransactions | marketSvc.py:24-29 | fromDate / accountKey はどちらも省略時 None。 | list of KeyVal。同じ client converter が消費。division 読取り拒否は client key CrpAccessDenied と reason tuple。読み取りのみ。 |

## Host 登録、例外、状態、通知

- CharacterServicesComposition.EconomyRpc は MarketProxyService を production service list に追加する。Dispatcher は CallReq の service / method を registry から引き、ApplicationKwargs で machoVersion を除いて handler に渡す。追加 socket test は実 HostServer、実 PacketDispatcher、実サービスで strictRpc: true を使う。さらに HostComposition.BuildAsync で production registry の marketProxy 登録を検査する既存 PgFact がある（MarketTradeHostWireTests.Registered_market_and_trade_calls_reply_and_update_persisted_state）。
- wire regression は machoVersion=496 とともに StartupCheck、GetStationAsks、CharGetTransactions(None)、GetOrders(PyLong(35)) を送る。port 1 の閉じた socket を使い DB は使用しない。全て response code 15、eveexceptions.UserError / CustomInfo と EveJS の正確な info を返し、RuntimeError ではないことを検査する。
- IServiceExceptionMapper は NpgsqlException を市場停止 UserError へ変換する。変換は strict-RPC の未知例外処理より先。MarketError / fee / access denial はサービス側で client UserError に変換され、未知例外だけが strict failure または通常の unexpected failure になる。
- 成功 mutation は MarketTrading が注文、約定、wallet を保存した後に receipt を publish する。MarketNotifier は objectCaching.InvalidateCachedMethodCall で marketProxy の book と owner cache を無効化し、その後 OnOwnOrdersChanged(orders, reason, isCorp) を送る。client はこの通知で GetCharOrders / GetOrders / GetSystemAsks / GetStationAsks / GetMarketOrderHistory / GetPlexBest cache を無効化し、escrow event を scatter する（marketsvc.py:113-127）。corporation order, PLEX order cache も Host が直接 invalidation する。client の注文変更通知の受信・消費を確認した。
- 持続化の既存 regression は MarketTradeHostWireTests が PlaceBuyOrder → 永続 order 読み直し → ModifyCharOrder、PlacePlexSellOrder → 永続 order 読み直し → ModifyPlexCharOrder を確認する。ProtocolTests は買い・売り・取消後の OnOwnOrdersChanged と読み取り wire row を確認する。いずれも PgFact のため、この job では実行していない。

## EveJS / gameplay との比較

- EveJS throwMarketUnavailable は接続・daemon 失敗を同じ CustomInfo と固定 info にする（marketProxyService.js:1988-1995）。StartupCheck は daemon startupCheck を呼び、失敗を同じ helper に渡す（同:6283-6291）。今回の Host mapper と StartupCheck はこの client-visible error に合わせた。
- EveJS の各 Handle_* は同じ positional args を読み、read/write failure を同じ helper に送る。キャンセルは先頭 orderID を使い、buy / sell fee は末尾 optional 引数として読む（同:6967-6968, 7090-7091, 7129-7154）。
- PlacePlexSellOrder については EveJS が useCorp=true を拒否する。一方 client は sell UI の corporate checkbox 値を SellPlex まで渡す（sellMulti.py:430-464）。owner policy は根拠のない personal-only 拒否を取り除くと記録している（docs/reference/compat/DEVIATIONS.md:3979）。EVE University の [PLEX](https://wiki.eveuniversity.org/PLEX) は redeemed PLEX を通常の移動・取引可能な item と説明するが、corporate wallet の扱いまでは明記しない。現行 eve.rs は client の corp flag を処理する。これは EveJS との差として明示し、独立した corporation PLEX 約定シナリオは DB test では走らせていない。
- V24.01 client による直接 marketProxy method の探索結果には MachoBindObject、moniker bind、marketProxy 名義の bound method 呼び出しはなかった。GetMarketGroups は server export だが client caller はなかった。

## 実行した確認

- dotnet build dotnet/tests/Eve.Host.Tests --no-restore -p:UseSharedCompilation=false — 成功。
- Market_proxy_database_failures_are_client_user_errors_over_the_host_route — 1 passed。DB URL 未設定、test は loopback port 1 の接続拒否のみ。
- Eve.Host.Tests 全 shard は DB URL を外して実行。この run は 501 passed / 101 failed / 227 skipped（合計829）。一部の integration fixture は EVE_PG_URL 不在を理由に失敗し、StructureDataRpcPersistenceTests と ShipKillCounterPersistenceTests にも assertion failure が出た。DB 依存テストを成功扱いにはしない。
