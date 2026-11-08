# Fitting 監査 R14 の修正記録

対象は `/home/user/reports/90-audit-eve-fitting-r14.md` の2件。どちらもクライアントと EveJS の根拠を確認し、実際の不具合と判定した。偽陽性として除外した指摘はない。

## 高: モジュールスタックから保存フィットへ装着できない

### 根拠と判定

- V24.01 クライアント全体を `FitFitting(`、`itemsToFit`、`InvalidFittingDataShipNotShip` で走査した。該当処理は `eve/client/script/environment/fittingSvc.py:582-590` で、ハンガー内スタックの itemID を候補に含め、必要数を stacksize 分だけ減らす。`:618` で `shipInv.FitFitting(...)` を呼ぶ。
- eve.rs の旧経路 `dotnet/src/Eve.Services.Inventory/InvBrokerService.Fitting.cs:124-131,163-179` はモジュール用に数量1を要求する一方、`dotnet/src/Eve.Ships/CharacterShips.cs` は分割装着を拒否していた。したがって候補に選ばれた非 singleton スタックは装着されず不足として数えられる。
- EveJS の `server/src/services/inventory/invBrokerService.js:2718-2738` は装着スロット向け数量を1に制限する。`server/src/services/inventory/invBroker/fittingOperations.js:291-321,1539-1557` は移動後のIDを装着・オンライン状態に使い、残量がある元スタックを次のスロット候補へ戻す。

### 修正

`CharacterShips.MoveAsync` はモジュールスタックの1個を同一トランザクション内で分割し、装着先を singleton にする。dogma の装着判定と module state、戻り値、移動通知には実際に移動した itemID を使う。`FitFitting` は分割後のIDをオンライン化し、元スタックの残量があれば同種の後続スロットでも候補にする。最後の1個で元ID自体が移動する場合に古い残量通知が後から出ないよう、同じバッチで移動するIDの中間 `OnItemChange` を除く。

回帰テストは保存フィットの単一スロット装着・新IDのオンライン状態・残量、クライアント数量なしの inventory `Add`、1つのスタックから2スロットへ供給するケース、装着条件不一致時にスタックを保持して不足を報告するケースを確認する。成功後の同じ fitting RPC の再試行では追加通知や重複装着がなく、別スロットへの2つの並行移動でもアイテムを二重使用・消失しないことも確認する。失敗条件のケースでは fake success や残量消費が起きないことも検証する。

## 低: 不正な ship type のエラーに数値IDを型名として渡す

### 根拠と判定

- V24.01 の `eve/client/script/environment/fittingSvc.py:361-365` は既存型の表示名 `shipTypeName` を `InvalidFittingDataShipNotShip` の `typeName` に渡す。
- EveJS の `server/src/_secondary/fitting/fittingStore.js:694-706` は型情報から `shipType.name` を取得し、`server/src/_secondary/fitting/fittingStore.js:1056-1059` で同じ値を UserError に渡す。
- 旧 eve.rs の `dotnet/src/Eve.Services.Core/FittingMgrServices.cs` は `typeName` に `PyInt` を設定していた。V24.01 SDE の type 2046 は英語名 `Damage Control I` を持ち、カテゴリは船・構造物ではないため、この拒否を実データで再現できる。

### 修正

Host の型 lookup をカテゴリと英語表示名の両方を返す形にし、型カテゴリの検証結果から `typeName` を `PyStr` で返す。実 SDE をロードした Host テストとサービス境界の回帰テストで wire 値を確認する。

## 検証結果

- 変更前に高 severity の回帰テストを追加し、スタック装着不足を確認してから修正した。同じスタックを2スロットへ使うテストも、候補再利用前に不足を検出し、再利用後は古い残量通知の順序不整合を検出した。低 severity の回帰テストは変更前に `PyInt(2046)` と期待する `PyStr("Damage Control I")` の差を検出した。
- `eve_p3`（`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3`）を使った Release テスト: `FittingMgrServicesTests` 16/16、`FittingCoverageTests` と `UnportedFeatureRpcServicesTests` 13/13、Inventory の `FitFittingTests`・`CargoCapacityInventoryTests`・`InventoryServiceTests` 20/20、Host の `CrateServicePersistenceTests`・SDE境界テスト・登録済みHost wireテスト・`CorporationReadRpcHostTests` は各フィルター実行合計6/6、`Eve.Ships.Tests` の直接移動参照11クラス 86/86、Dogma の `OnlineCommandTests` と `ReloadServiceTests` 9/9。
- 登録済み `invbroker.FitFitting` を TCP Host から呼ぶ新しい PostgreSQL wire test は1/1。スタック残量と singleton 装着・online状態の永続化、通知順、RPC再試行時の無通知を確認した。
- `FitFittingTests` 単独でも19/19、skipなしで再実行した。追加した並行移動と同じRPCの再試行もこの実行に含む。
- Space の直接参照19クラスは一括実行で409/410通過し、無関係な `SpaceServiceTests.When_the_simulation_says_the_ship_has_jumped...` が一度、MovementPolicy 拒否で失敗した。同テスト単独の再実行と `SpaceServiceTests` 全体の単独再実行は通過（168/168）。一括実行で他の242件は通過。失敗の根本原因は特定できていないため、フレークが解消したとは断定しない。
- `AbyssalHostFlowTests` は追加の保守的な参照確認として実行し、21件通過、20件skip。skip 理由はローカルに `EVE_ABYSSAL_SIM_BINARY` / `EVE_ABYSSAL_SIM_SDE` が設定されていないこと。これらは成功数に含めない。このクラスの該当箇所は `SetOnlineAsync` で、今回変更した `MoveAsync` / fitting batch 経路の直接カバレッジには含めない。
- 変更した本体5プロジェクトとテスト3プロジェクトを Release、`-warnaserror` で個別ビルドし、各ビルドは0 warning・0 error。

## 未確認

- グラフィカル V24.01 クライアントでの保存フィット操作は実施していない。
- EveJS の該当ソースは比較したが、ランタイムを起動して同じシナリオは実行していない。server の `package.json` に test script はなく、fitting 用テストファイルも見つからなかった。
- Space の一括スライスにおける無関係なJumpテストの1回の失敗は再実行で再現しなかったが、発生原因は未確認。
