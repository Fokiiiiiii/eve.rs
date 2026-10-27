# configサービス深掘り監査

対象はV24.01クライアントの全 `/home/user/eve-logic` 呼び出しと、eve.rs Host の `config` サービス。クライアントのサービス入口は `carbon/common/script/sys/cfg.py:169-182` の `GetConfigSvc`。bound object / moniker 経由の `config` 呼び出しは全体検索で見つからなかった。

## クライアントから呼ばれるメソッド

| RPC | 全クライアント側の入口 | 応答の利用 |
| --- | --- | --- |
| `GetMultiAllianceShortNamesEx` | `eve/common/script/sys/eveCfg.py:378-379` の alliance レコードセット | `EveAllianceShortName` は `shortName` を `name` / `description` に割当て (`eveCfg.py:1259-1261`)。利用例: `killRights.py:154`, `bountyWindow.py:773,817,856,894`, `overviewUtil.py:34`。 |
| `GetAverageMarketPrices` | `eveCfg.py:399-403,522-524`。起動時と遅延ロードで取得 | `inventorycommon/typeHelpers.py:91-109` が `averagePrice` / `adjustedPrice` を返し、多数のマーケット・インベントリ・UI利用箇所がこの共通関数を通る。欠損は `None`。 |
| `GetBlackListedPlanets` | `eveCfg.py:475` | `planetaryProductionPlanetContentPiece.py:18`, `colonyResourcesAgencyPlanetContentPiece.py:15` が membership を調べる。 |
| `GetMultiCorpTickerNamesEx` | `eveCfg.py:479-484` の corporation ticker レコードセット | `CorporationTicker` は `tickerName` を `name` / `description` に割当て (`eveCfg.py:1244-1245`)。利用例: `uilog.py:340`, `infoContentBase.py:481,682`, `kill_mail.py:131`。 |
| `GetMultiLocationsEx` | `eveCfg.py:665-666` の locations レコードセット。まず `cfg.py:537-604` でローカルfetcherを使い、残りをremote fetcherへ渡す | `locationName`, `solarSystemID`, 座標を一般のlocation recordset利用者が参照する。`GetRawName` の主なfallbackは `eveCfg.py:1301-1308`、位置名処理は `localization/propertyHandlers/locationPropertyHandler.py:27-56`。 |
| `GetMultiOwnersEx` | `eveCfg.py:548-550` の owners レコードセット。共通の `Recordset._Prime` が未取得keyを一つのlist引数にして呼ぶ (`cfg.py:537-604`) | `EveOwners.GetRawName` は `ownerNameID` がない場合 `ownerName` を使う (`eveCfg.py:1206-1212`)。`IsCharacter` / `IsCorporation` / `IsAlliance` は `typeID` を読む (`eveCfg.py:1220-1227`)。Agent ID の利用例は `eve/client/script/ui/shared/industry/browserResearch.py:112-113`, `eve/client/script/ui/shared/neocom/addressBook/addressbookService.py:336-337`。 |
| `GetOldStationData` | `eveCfg.py:741-752` | 応答がtruthyで `.columns` がある場合だけ置換するので空listは有効な「削除済みstationなし」。後続利用は `infosvc.py:2118-2121`, `uisvc.py:249-254`, `locationPropertyHandler.py:52-56`。 |
| `GetStationSolarSystemsByOwner` | `infosvc.py:1163`, `mapFilterSettledByCorp.py:16-17`, `mapViewColorHandler.py:199-205` | 各行の `solarSystemID` を所有者filter / map色付けに使う。 |

これ以外に、クライアントが実際にremote `config` へ送るメソッドは見つからなかった。`GetOwnersEx`, `GetCorpTickerNamesEx`, `GetAllianceShortNamesEx`, `GetLocationsEx`, `GetLocationsLocal`, `GetPositionEx`, `GetPositionsEx` はHost互換route。クライアントの位置recordsetは `cfg.py:537-604` のlocal fetcherを先に呼ぶ。positions recordsetは `eveCfg.py:396-397` で初期化されるが、全体検索で読出しcallerはなかった。

## RPC共通の挙動

- Host登録は `[Service("config")]` の `ConfigService` (`dotnet/src/Eve.Services.CharacterInfo/ConfigService.cs:13`)、実際のHost service listは `dotnet/src/Eve.Host/Rpc/CoreRpc.cs:90-100`。今回の回帰テストはsocket経由で `config` を呼ぶ。
- クライアントのrecordset fetch引数は位置引数のlist。個別 `GetStationSolarSystemsByOwner` は位置引数1つ。request dataをkeyword引数にするcallerはない。cached client callsのtransport keyword `machoVersion` は `ObjectCallGPCS.py:399-408` で加わり、client service layer `service.py:365-401` が消費する。Host dispatcherもサービスへ渡す前に取り除く (`dotnet/src/Eve.Services/PacketDispatcher.cs:301-307`)。Host testはkwargs付きwire callでこれを確認する。
- IDsはclient側で `int(key)` に変換される (`cfg.py:637-646`)。したがってIDとしての `None` はclientが送る形ではない。nullable fieldは応答で `None` になる。`GetMultiLocationsEx` はID 0のnone rowを受け付ける。
- Hostはlist / tupleと `PyInt` / `PyLong` を受け、scalarや不正IDには明示 `UserError` を返す (`ConfigService.cs:258-285`)。malformed listは `CONFIG_IDENTIFIER_LIST_INVALID`、不正IDは `CONFIG_IDENTIFIER_INVALID`。未知recordは該当rowを省略し、後の `Recordset.Get` が `KeyError('RecordNotFound', key)` にする (`cfg.py:637-646`)。
- `RequestedIds` のraw list上限はowner 10,000件、その他のmulti-fetchは500件。clientの `Recordset._Prime` は `keysToGet` 全件を一度のlist引数で送る (`cfg.py:537-604`)。このHost保護上限を超えた場合は `CONFIG_BULK_LIMIT_EXCEEDED`。全ソースのcallsite確認ではこの上限超えの実client要求を確認できず、巨大batchの実機量は今回のheadless確認対象外。
- これらのメソッドはread-only。状態更新・通知を行わず、クライアントも完了後通知を待たない。応答データ源はSDEカタログ、character / corporation / alliance store、market store、structure read model、owner所持item store。データの再起動後読み出しは永続storeの責務で、config RPC自体は保存処理を持たない。

## メソッドごとのHost / reply / EveJS照合

| メソッド | Host wiring・入力 | clientが読むwire shape・状態・通知 | EveJS 261018 |
| --- | --- | --- | --- |
| `GetMultiAllianceShortNamesEx` | `ConfigService.cs:191-212`。`CorporationStore` の生存allianceをIDで引き、unknown/deletedはrowを作らない。 | `([allianceID, shortName], rows)` rowset。clientは `shortName` を読む。read-only / 通知なし。 | `configService.js:773-790`。short name rowset。 |
| `GetAverageMarketPrices` | `ConfigService.cs:78-95`。実Hostは `CoreRpc.cs:94` から `MarketStore.LatestAverageSalesAsync` を渡す。引数なし。 | `PyDict[typeID, KeyVal(averagePrice, adjustedPrice)]`、数値はwire real。clientは両fieldを読む。売上なしのtypeはkeyなしで、client helperは `None`。read-only / 通知なし。 | `configService.js:888-891`, `averageMarketPrices.js:33-59` はSDE `basePrice` を両fieldに使う。eve.rsは売上のあるtypeをMarketStoreから読み、両priceに同日のvolume-weighted averageを返す。意図差分は `docs/reference/compat/DEVIATIONS.md:1456-1461` に記録済み。 |
| `GetBlackListedPlanets` | `ConfigService.cs:64-65`。引数なし。 | 空 `PyList`。clientはmembership testのみ。read-only / 通知なし。 | `configService.js:878-881` も空list。 |
| `GetMultiCorpTickerNamesEx` | `ConfigService.cs:160-189`。Corporation storeからtickerを取得。 | `([corporationID, tickerName, shape1, shape2, shape3, color1, color2, color3], rows)`。clientが表示に使うのは `tickerName`。未提供のoptional fieldsは `None`。read-only / 通知なし。 | `configService.js:797-852`。ticker rowset。 |
| `GetMultiLocationsEx` | `ConfigService.cs:214-256`。SDE station/system、永続structure directory、owner確認済みitemを照会。ID 0を明示処理。 | `([locationID, locationName, solarSystemID, x, y, z, locationNameID], rows)`。未知・他人のitemはrowなし。clientは名前とsystem/位置を読む。nullable system/name IDは `None`。read-only / 通知なし。 | `configService.js:489-772`。locations rowset。 |
| `GetMultiOwnersEx` | `ConfigService.cs:97-158`。character / corporation / alliance永続storeとSDE ownersを照会。SDE agent ownersは `AgentComposition.cs:42-43` の `npcCharacters` から `AgentCatalog.BuildOwners` が生成し、実serviceへ `CoreRpc.cs:93-97` で注入。 | `([ownerID, ownerName, typeID, gender, ownerNameID], rows)`。agentは `typeID=1373`, SDE name/gender, `ownerNameID=None`。ownerName fallbackとtype判定は上記client refs。unknownはrowなし、次のclient `Get` がRecordNotFound。read-only / 通知なし。 | `configService.js:381-481` はagentを含むrowset。agent projectionは `buildAgentOwnerRow` (`configService.js:352-368`)。EveJSの合成unknown/item rowsはclient側に架空ownerを作るため採用しない。 |
| `GetOldStationData` | `ConfigService.cs:71-72`。引数なし。 | 空 `PyList`。clientは空なら既存初期値を保つ。read-only / 通知なし。 | `configService.js:883-886` も空list。 |
| `GetStationSolarSystemsByOwner` | `ConfigService.cs:39-62`。SDE station catalogをownerで絞り、solar systemごとにgroupし、ID順に返す。引数はowner ID 1つ。 | `PyList[util.KeyVal(ownerID, solarSystemID, stationCount)]`。clientが必要とする `solarSystemID` と一致。read-only / 通知なし。 | `configService.js:859-871`。共通station dataは `corporationState.js:944-982`。 |

`GetOwnersEx` / ticker / alliance / location / position aliasは `ConfigService.Recordsets.cs:10-35` で同じ実装へ委譲する。 `GetPositionEx` / `GetPositionsEx` の7列・nullable角度もHost socket testで確認対象に含めるが、clientからの呼び出しはない。

## 回帰確認

`dotnet/tests/Eve.Host.Tests/ConfigRecordsetHostWireTests.cs` は実Host socketでservice名、transport `machoVersion` kwarg、PyInt/PyLong、owner SDE row、ticker/alliance values、nullable fields、station/system/location/position values、alias routesを検証する。`ConfigOwnerBurstHostWireTests.cs` は通常session内のowner batchとmalformed requestのHost exception envelopeを検証する。`AgentCatalogHostWireTests.cs` の認証fixtureもHostの20-byte password hash validationに合わせて更新した。Pinned-SDEを読む `AgentCatalogSdeTests` 2件は今回のrunでskipされており、SDEテーブルからの実ロードはPostgreSQLなしでは確認していない。

実行結果:

- focused Host test: 4 passed, 0 skipped (configのwire tests 3件とAgentCatalog wire test 1件)。
- Host.Tests全体: 489 passed, 227 skipped, 101 failed。実行時に `EVE_PG_URL` を明示的に外したため、DB依存ケースが `EVE_PG_URL` 不足または `PgConnection.ToConnectionString` で接続前に失敗した。PostgreSQL接続は行っていない。skip数はpassに含めていない。
- AgentCatalog wire testは不正な1-byte認証fixtureを20-byteに直した後、focused実行で通過した。

この確認はheadless Host wire test。GUI clientとの接続と、DBを使った再起動横断の確認は今回行わない。
