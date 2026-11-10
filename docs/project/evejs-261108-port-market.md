# EveJS 261108.1: Market / contract port audit

対象は Market Seed v4、market order matching の価格・発行時刻優先、private contract fee。参照は eve.rs の `Eve.Market` / `Eve.Trade`、V24.01 client `/home/user/eve-logic`、EveJS `/home/user/evejs-261108.1` と前版 `/home/user/evejs-261028.1`。

## 結果

| 項目 | 判定 | eve.rs での対応 |
| --- | --- | --- |
| Market Seed v4 | 修正 | SDE の NPC station と type に加え、V24 player order の最大 90 日を超える order だけを snapshot に残す。 |
| 価格・issue time による約定順 | 既存 | `MarketMatching` と DB の batch walk が価格、issue time、ID の順で候補を消費する。追加変更なし。 |
| private contract の broker fee | 修正 | availability 1–3 は type によらず 10,000 ISK、sales tax と deposit は 0。成功時の wallet debit、契約、item escrow、expiry、通知は既存 transaction 内で記録する。 |

## 根拠と変更

### Market Seed v4

V24.01 の buy UI は immediate、1、3、7、14、30、90 日を選択肢にし、sell UI も最大 90 日を表示する（`eve/client/script/ui/shared/market/buyThisTypeWindow.py:110-124`, `sellMulti.py:313-329`）。ESI row を受け取る `MarketCatalog.EsiOrder` に owner ID はなく、既存 capture は SDE NPC station と既知 type だけで判定していた（`dotnet/src/Eve.Market/MarketCatalog.cs:80-92,385-415`）。そのため NPC station に出された 90 日の player order が NPC seed に混ざる穴があった。

EveJS v4 README は通常の 90 日を超える duration を NPC order の信号に使うと明記する（`tools/market-seederv4/README.md:3-11,23-27`）。共有 Rust importer は境界を 90 日、NPC 判定を `duration > threshold` とする（`tools/market-seederv2/src/main.rs:305-307,1219,1547-1550`）。これに合わせ、eve.rs は `duration > 90` の row のみを既知 type と SDE NPC station 条件に加えて保持する。

回帰テストは 365 日の NPC sell/buy、unknown type、非 NPC station、90 日の station order を同じ ESI capture に入れ、保持 2 件、DB import 2 件、book の価格を確認する（`dotnet/tests/Eve.Market.Tests/MarketSeedImportTests.cs:15-41`）。既存の capture fixtures も NPC order を表す 365 日に更新した。90 日以下を保持しないことはこの DB-backed test で明示的に確認する。

### matching と partial fill

CCP の [Buy and Sell Orders](https://support.eveonline.com/hc/en-us/articles/203218932-Buy-and-Sell-Orders) は、buy は最も高い価格、sell は最も低い価格から、数量が尽きるまで複数 order に充当するルールを説明する。V24.01 client は同価格の asks を issueDate、jumps、orderID で、bids を issueDate、距離/range、orderID で並べ直す（`eve/client/script/ui/services/marketsvc.py:28-70,701-715`）。whole client の `marketsvc.GetOrders` から `marketProxy.GetOrders` まで追い、注文の作成・変更は broker 経由で即時に match されることを確認した。

新 EveJS market server は asks/bids の merge 後に price、issue time、station ID、order ID で整列し（`externalservices/market-server/src/state.rs:2142-2149,2217-2227`）、service が返された book を順に処理して maker 数量を `min(remaining, available)` で部分充当する（`server/src/services/market/marketProxyService.js:4412-4447,5203-5238`）。旧版は price の次を station ID / order ID としており issue time を優先しなかった（`/home/user/evejs-261028.1/externalservices/market-server/src/state.rs:2144-2150,2177-2186`）。

eve.rs にはこの primary priority と partial fill が既にあったため、今回は変更していない。`MarketMatching.Plan` は価格、`IssuedMs`、ID の順、`MarketFillWalk` は必要数まで候補を部分充当する（`dotnet/src/Eve.Market/MarketMatching.cs:43-76`）。`MarketTrading.MatchAsync` は同順を batch で読み、maker の残数・escrow を同一 transaction で更新して `PartialFill` 通知を作る（`dotnet/src/Eve.Market/MarketTrading.cs:138-180,333-408,439-449`）。test は seed/player の価格と発行時刻優先、minimum volume、期限切れ、130 件を越える book の partial fill を確認する（`dotnet/tests/Eve.Market.Tests/MatchingTests.cs:5-33`, `OrderBookMatchTests.cs:9-32`）。EveJS の追加 tertiary tie-break は変更しなかった。client の tie sort は jumps/range に依存し、Tranquility の execution tie を固定する証拠は見つからなかったため、今回は price/time の release rule までを扱う。

`Eve.Trade.TradeAuthority` は2人の直接 trade の item/ISK custody と accept manifest を持ち、market order book や contract fee を担当しない（`dotnet/src/Eve.Trade/TradeAuthority.cs:15-45,343-405`）。そのためこの変更の書き換え対象ではない。

### private contract fee

V24.01 `createContractWnd.GetFeeTextAndAmounts` は availability が public より大きい場合、10,000 ISK の minimum broker fee と 0 tax / 0 deposit を表示し、skill ベースの公開料計算を呼ばない（`eve/client/script/ui/shared/neocom/contracts/createContractWnd.py:624-654`）。確定時は `isPrivate = bool(assigneeID)` を `ContractStruct` に載せ、`contracts.CreateContract` を呼ぶ（同 `:878-890`）。`contractscommon.py:17,84-108` の公開 fee 計算は percentage/minimum を使う。private auction の character assignee は client が拒否し、corp/alliance availability の private auction は private fee path の対象。

EveJS 261108.1 は通常の private contract に `PRIVATE_CONTRACT_BROKER_FEE=10000` を導入し、必要額を先に確認して不足時は `CustomInfo` と具体的な金額を返す（`server/src/services/contracts/contractRuntimeState.js:61,709-720,1961-1985`）。auction は既存 terms で private broker 10,000 / deposit 0 を扱う（同 `:1153-1190,1385-1402`）。前版は auction 以外の private fee debit を作らず、reward だけを preflight していた（`/home/user/evejs-261028.1/server/src/services/contracts/contractRuntimeState.js:1939-1947`）。

eve.rs の `ContractFeesCalculator` は private availability にも公開 percentage を適用していたため、availability 1–3 に固定 10,000 / tax 0 / deposit 0 の branch を追加した（`dotnet/src/Eve.Services.Core/ContractFees.cs:16-55`）。`GameRpcServices.CreateContract` は private では Accounting/Broker Relations と broker adjustment を読まず、wallet 不足を EveJS と同じ `CustomInfo` の説明文に変換する（`dotnet/src/Eve.Services.Core/GameRpcServices.cs:194-212,239-263`）。`PersistenceContractWriteModel` は wallet debit、expiry、Created/Assigned 通知を一 transaction で commit する既存実装を使う（`dotnet/src/Eve.Host/Persistence/Contracts/PersistenceContractWriteModel.cs:152-172`）。

Host wire E2E は public 10M item exchange の broker/tax/deposit `40k/100k/100k` と private 10M contract の `10k/0/0`、item escrow、wallet 残高、expiry job、Created → Created → Assigned 通知順を検証する。wallet を 9,999 ISK に下げた後の再作成は `CustomInfo` を返し、wallet、残り inventory、contract row、expiry job、通知が変化しないことも確認する（`dotnet/tests/Eve.Host.Tests/ContractProxyPersistenceWireTests.cs:167-328`）。

## 今回 port しなかった項目

- price/time matching は eve.rs に既存だったため二重実装しない。
- EveJS の market daemon の SQLite/SQL ordering 実装は移植しない。eve.rs の既存 Postgres book、batch lock、transaction を使う。
- EveJS の station ID tertiary tie-break は不明確な Tranquility behavior を新たに固定するため採らない。V24 client には location-dependent jump/range の tie sort があり、issue time より後の実約定順を裏付ける trace は今回見つけていない。
- market の fee、NPC stock、seed quantity に推定値や captured packet は加えない。seed は pinned SDE の station/type と ESI snapshot row から導出する。

## 検証

- 変更前に seed import は期待 2 件に対して 3 件を保持、private fee は 10,000 / 0 / 0 に対して公開式の 40,000 / 100,000 / 0 を記録する失敗を再現した。
- 追加した資金不足 wire ケースは最初 `exceptions.RuntimeError` を返した。明示 mapping 後は `eveexceptions.UserError` (`CustomInfo`) と同じ EveJS 文面になり、DB state が rollback する。
- `Eve.Market.Tests`: `MarketSeedImportTests`, `MarketSeedV4Tests`, `MatchingTests`, `OrderBookMatchTests` — 13 passed, 0 failed, 0 skipped。Postgres は `eve_p5`、SDE build 3396210 をロード。
- `Eve.Services.Core.Tests.ContractFeesCalculatorTests` — 17 passed, 0 failed, 0 skipped。
- `Eve.Host.Tests`: contract create retry, Host registered contract read/mutations, malformed multi-contract rejection, DB-backed public/private create — 4 passed, 0 failed, 0 skipped。
- EveJS `cargo test equal_issue_times_keep_deterministic_station_and_order_ties` — 1 passed。EveJS server package に contract test script / contract test file はなく、contract rule は前版と現版の source diff で追跡した。

**未確認:** graphical V24.01 client、実 Tranquility での取引 trace、EveJS private contract flow の実行。変更後の Release build / warning count はこの記録作成時点では未確認。
