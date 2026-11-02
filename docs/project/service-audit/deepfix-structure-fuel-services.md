# Upwell構造物の燃料・サービス・補強・アンカー監査

## 確認した燃料サイクル欠陥と修正

- V24.01クライアントの全コード検索では、オンライン切替の呼び出し元は `eve/client/script/ui/shared/fitting/fittingSlotController.py:325-332` から `eve/client/script/dogma/clientDogmaLocation.py:696-718` に至る経路だった。オンラインは `SetModuleOnline(locationID, moduleID)`、オフラインは `TakeModuleOffline(locationID, moduleID)` を呼ぶ。オンライン失敗時は `UserError` を再送出し、オフライン失敗時は表示効果を戻す。UIはRPCの戻り値を使わない。
- EveJS 261028.1の `src/server/src/services/structure/structureServiceModules.js:380-443` は、燃料候補の合計量が1サイクル分に届かないと `NOT_ENOUGH_FUEL`、消費量0、アイテム変更なしを返す。同ファイル `:656-778` はサイクル分の燃料不足時にサービスモジュールをオフラインにする。オンライン時刻と次回サイクルは `:449-505` で記録される。Dogma側は `src/server/src/services/dogma/dogmaService/moduleOperation.js:2088-2091` で不足をプレイヤー向け通知エラーにする。
- eve.rsの旧 `StructureFuel.HourAsync` は `dotnet/src/Eve.Structures/StructureFuel.cs` で `allowPartial: true` により残燃料を先に消費してからFuelOutへ進んでいた。必要な合計量の事前確認を行い、不足時は燃料を残したままFuelOutへ移るよう変更した。実際の燃料量は引き続きfuel bay authorityと同じDBトランザクションが所有する。
- 回帰テスト `dotnet/tests/Eve.Structures.Tests/StructureFuelTests.cs` は、40個必要なサイクルに20個しかない場合、20個が残り全サービスがオフライン、構造物がLow Powerになることを検証する。修正前はこのテストが `Expected: 20, Actual: 0` で失敗し、修正後は通過した。

## クライアント全体から確認した関連経路

- サービス可用性: `eve/client/script/ui/services/structure/structureServices.py:28-56` が `structureSettings.CharacterGetServices` を取得し、`OnStructureServiceChanged` で再取得後に `OnStructureServiceUpdated` を散布する。全体検索でUI消費元として `eve/client/script/ui/shared/dockedUI/lobbyWnd.py:302-304`、情報ウィンドウとして `eve/client/script/ui/shared/info/infosvc.py:840-843`、ジャンプ経路判定として `eve/client/script/parklife/autopilot.py:624-627` を確認した。
- 補強時間: `eve/client/script/ui/structure/structureBrowser/reinforcementTimeWnd.py:170-172` が曜日・時刻を `UpdateReinforceTimesFromClient` に渡す。`reinforceTimersBundle.py:17-34` は現在値に既定値を使い、次回値・適用時刻の欠損を `None` として読む。構造物状態通知は `structureEntryMyCorp.py:423-425` と `structureBrowserController.py:101-106` が一覧を更新し、`cosmeticFitting.py:118-121` は現在構造物の表示を遅延更新する。
- eve.rsのDogma経路: `dotnet/src/Eve.Services.Dogma/DogmaIMService.cs:199-228` はオンライン切替を `CharacterShips.SetOnlineAsync` に渡す。構造物Dogma読取モデルは `dotnet/src/Eve.Host/Persistence/Structures/StructureControlReadModel.cs:30-65` で組み立てられ、`dotnet/src/Eve.Host/Composition/StructureComposition.cs:296-297` は燃料ジョブを登録する。これらから、`StructureFuel.OnlineAsync` を呼ぶ本番RPC接続が存在しないことも確認した。構造物のモジュールをDogma RPCからオンライン化する経路は本監査の未修正項目であり、燃料修正だけではプレイヤー操作の完了にならない。
- 通知経路: 変更前のeve.rsでは `StructureTimers.ServiceChangedTopic` に購読者がなく、オンライン・オフラインのサービス変更をクライアントへ配信していなかった。`StructureNotifications` に購読を追加し、構造物内のセッションへ `OnStructureServiceChanged(clientID, structureID)` を送る。EveJSの `src/server/src/services/structure/structureLifecycleFanout.js:342-363` も同じ通知を変更構造物内のセッションへ配る。クライアントの再読込・UI消費元は上記 `structureServices.py:28-56` と `lobbyWnd.py:302-304`、`infosvc.py:840-843`、`autopilot.py:624-627`。
- 追加で確認した燃料差異: `StructureFuel.ScheduleNextHourAsync` は次のUTC時刻境界にjobを置くが、EveJSは各モジュールのオンライン時刻から1時間後を初回期限にする（`structureServiceModules.js:449-505, 600-637`）。また `StructureTimers.FuelOutAsync` はstructure_servicesをofflineにする一方、fitting.module_stateは更新しない。制御Dogmaの読取は後者を参照するため、燃料切れ後のUI状態・Dogma通知を含めた同期は追加検証・修正が必要。
- アンカー・補強の状態遷移通知はクライアントが上記状態更新を消費するところまで確認した。既存状態の永続化では `StructureSnapshot.AnchoringMs` と `StructureStore` の `anchoring_ms` が使われ、復帰後のタイマーがこの値を読む。個別の実クライアント動作はこの作業では実行していない。

## データ根拠と未確認事項

- 公式SDE固定値は `sde/lock.json` のbuild `3396210`。燃料ルールローダー `dotnet/src/Eve.Host/Persistence/Structures/ServiceFuelRulesSource.cs:8-43` はservice module typeのdogma属性2109（時間あたり）と2110（オンライン時）を読む。ローカルPostgreSQLにはSDE schemaがなく、今回の作業で個別moduleの属性値をDBから再照合できていない。値のハードコード追加はしていない。
- 力学の照合先: [EVE University: Fitting Upwell structures](https://wiki.eveuniversity.org/Fitting_upwell_structures), [EVE University: Vulnerability](https://wiki.eveuniversity.org/Vulnerability), [EVE University: Anchoring structures](https://wiki.eveuniversity.org/Anchoring_structures). UIとの互換判断は上記V24.01クライアントcallsiteを優先する。
- ローカル確認: `Eve.Structures` と `Eve.Structures.Tests` をビルド。DBロック経由で `eve_p7` の対象ケース1件、その後 `StructureFuelTests` 全7件が成功し、スキップはなかった。通知変更では `Eve.Host` と `Eve.Host.Tests` のビルドが成功し、DBロック経由の `StructureNotificationsTests` は2件成功、スキップなし。GitHub Actions run `41436825715` は成功。実グラフィカルクライアント・SDE属性行の個別比較は行っていない。
- push記録: `4610441bd191` を `main` に反映し、そのpush-triggered CI run `41436825715` は成功。
- 通知変更を含む `8a837cd9` は、追加の `origin/main` をマージしてHostのビルドとDBテストを再実行後、`0e95daff8944` としてmainにpushした。push-triggered CI run `41445515368` は現在監視中。
- RED GATE追跡: CI run `41443510202` とこのpushのrun `41445515368` は `.NET (host tests 3/6)` と `(4/6)` が失敗し、`Login_and_onboarding_rpcs_keep_client_wire_contracts_with_transport_kwargs` と `Air_and_login_support_rpcs_return_client_shapes_through_the_host` が原因。`airnpe.py:101-111` はskip RPCを呼び、`infoPanelSvc.py:395-397` は `OnTutorialSkipped` でoperations panelを更新し、`airnpe.py:63-70` は `OnAirNpeStateChanged` で状態を更新する。両通知を順番に受けて応答まで読むwireテストへ修正した。`24b938f3` にコミット済みで、ローカルの対象2件は成功。mainへの反映前にorigin/mainの再確認を行う。
