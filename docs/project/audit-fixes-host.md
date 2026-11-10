# Host audit fixes: report 90-audit-host-r15

監査対象: `/home/user/reports/90-audit-host-r15.md` (2026-11-08)。各所見をeve.rsの実装、V24.01クライアント (`/home/user/eve-logic`)、EveJS (`/home/user/evejs-261108.1`) と照合した。3件とも実際の欠陥だった。誤検知として除外した所見はない。

## P2 — CRABリンク後にCharacterEnergy RPCと通知が古い値を返す

### 根拠と変更

- クライアントの`characterEnergySvc.py:23-36`は`OnCharacterEnergyChanged`をキャッシュし、初回に`GetMyEnergyState`を呼ぶ。`tooltip/item.py:104-139`は同じ状態からCRABの残量と起動可否を表示する。リンク操作のメニュー呼び出し箇所は`menusvc.py:1976-1979`。
- EveJS 261108.1のCRABリンク処理は`mobileAnalysisBeaconRuntime.js:806-808`で共通CharacterEnergyサービスを使い、成功後`1877-1883`でCharacterEnergy通知を発行する。状態計算・消費・通知は`characterEnergyMgrService.js:194-221, 266-318`。そのpayload builder (`:101-113`)は5つの数値を常にmarshal Realにする。Hostも`PyReal`と`PyLong`のwire型を一致させた。
- 修正前のHostはRPC値をGameStoreから返し、リンク処理は`analysis_beacon_character_energy`だけを消費していた。Host側の読み取りは`dotnet/src/Eve.Host/CharacterRpcServices.cs`、共有状態とロック付き消費は`dotnet/src/Eve.Services.Space/Beacons/CharacterEnergyState.cs`および`AnalysisBeaconStore.cs:131-210`、リンク確定後の通知は`AnalysisBeaconDirector.cs`。本番登録とコールバック配線は`dotnet/src/Eve.Host/Rpc/InventoryRpc.cs`および`Composition/SpaceComposition.cs`。
- `AnalysisBeaconHostTests.Linking_a_crab_updates_the_character_energy_rpc_and_notifies_the_linking_client`はBeyonceの`CallComponentFromClient`から`linkWithShip.InitiateLink`を実行し、RPC値、6フィールドの通知wire shape、別Storeからの再読込を確認する。同キャラクターの並行消費と枯渇後状態も確認する。

### 修正前後の検証

- 修正前のテスト専用worktreeではRPCが`100`を返し、期待値`66.66666667`と一致せず失敗した。さらにEveJSのwire型を厳密にするテストを先に追加し、修正前のfocused実行で整数値5フィールドが`PyInt`となるため失敗することも確認した。
- 修正後はリンク成功後のRPC・通知・永続読込が一致する。CEQコストは既存ルールのまま。

## P2 — SKINライセンス消費と権利付与の永続化にクラッシュ窓がある

### 根拠と変更

- V24.01の`menuSvcExtras/menuFunctions.py:395-424`は確認後に`cosmeticsSvc.ActivateShipSkinLicense`を呼ぶ。`skinsPanel.py:35-38, 153-156`は`OnSkinLicenseActivated`を受けて一覧を再読込する。
- EveJS 261108.1の`shipCosmeticsMgrService.js:452-523`は権利付与、ライセンス消費、在庫通知と`OnSkinLicenseActivated`を処理する。
- 修正前HostはメモリのGameStoreをdirtyにした後、別のPostgreSQLトランザクションで在庫を消費していた。GameStoreの遅延flush前に終了すると権利が失われ得る。
- `skin_license_activation_intents`を新設し、`ShipCosmeticsMgrRpcService.cs`で在庫消費と権利付与intentを同じinventory transactionに記録する。GameStore flush後にintentを完了し、起動時に`CharacterMigrations.cs`から冪等に回復する。migrationは`SkinActivationMigrations/0001_ship_skin_activation_intents.sql`。本番のGameStoreは`LegacyGameStoreComposition.cs:80-81`で対象テーブルを読み、Host RPCより先に回復処理を行う。
- `ShipCosmeticsMgrRpcServiceTests.License_activation_survives_reload_before_the_deferred_game_store_flush`はflushを保留し、新cacheからの再起動回復、永続readerの権利行、intent削除、アイテム消費を確認する。`HomeStationHostTests.Registered_skin_activation_sends_two_scalar_args_only_after_commit_and_none_on_failure`は実Host registry経由で通知のwire形・順序、失敗時無通知、在庫永続化を確認する。

### 修正前後の検証

- 修正前のテスト専用worktreeでは新しいcacheから権利行を読めず失敗した。
- 修正後は回復後の永続readerに権利があり、intentは削除される。アイテム通知の後に`OnSkinLicenseActivated`が送られる。

## P3 — ワープ中または発進処理中にwarpが始まるとejectが成立し得る

### 根拠と変更

- V24.01の`menuSvcExtras/invItemFunctions.py:42-53`はeject RPCの直前に船ball modeを検査し、warp中は`ShipInWarp`を返す。UIからの呼び出しは`menusvc.py:3465-3469`。
- EveJS 261108.1の`ejectRuntime.js:684-755`にはこのwarp検査がない。eve.rsではクライアントの通常経路と直接RPCの双方で状態変更を守るため、`SpaceContainers.Launch.cs`の操作開始時、非同期pose取得後、inventory transaction直前に共有`SpaceSessions.IsWarping`を確認する。
- 本番のeject service登録は`dotnet/src/Eve.Host/Rpc/StationRpc.cs:57-60`。
- `SpaceContainerTests.Launching_from_a_space_container_is_refused_while_the_pilot_is_warping`はeject RPC service経由でエラーと在庫・space item・spawn通知の不変を確認する。`Launch_in_progress_is_refused_if_warp_starts_before_the_inventory_commit`はpose応答を止め、その間にwarpを開始してから再開する競合を再現する。

### 修正前後の検証

- 修正前のテスト専用worktreeでは、既warp状態とpose照会中に始まったwarpの両ケースで例外が出ず失敗した。
- 修正後はいずれも`ShipInWarp`で拒否され、在庫、space item、spawn通知は変化しない。

## 実行した検証

- `Eve.Host`、`Eve.Services.Space`、両テストプロジェクトをReleaseでbuild。すべて警告0、エラー0。
- `eve_p4`と`eve_p10`の各DBで、Host関連9クラス41件とSpaceの`AnalysisBeaconHostTests`/`SpaceContainerTests` 43件を実行。成功し、スキップ0。
- テストDBにはSDE build 3396210を用いた。初回のDB未準備時に失敗した試行は成功扱いしていない。p4初回Host試行は必要なmission SDEが未ロード、p10初回試行はDB未作成だったため、DBを用意して同じフィルターを再実行した。
- 各不具合の追加回帰テストは修正前のテスト専用worktreeでも実行し、記載した不一致で失敗することを確認した。assertionは弱めていない。
- EveJSの該当実装をソースで追跡した。対応するEveJSテストは検索で見つからず、EveJSサーバー自体は実行していない。

## 未確認

- V24.01のGUIクライアントでは実行していない。クライアントの呼び出し・通知経路は`/home/user/eve-logic/eve/client`全体を検索し、該当するPythonコードを確認した。`InitiateLink`の関数本体はPythonソース内に見つからず、メニューの呼び出し箇所までを根拠としている。
- SKIN回復テストはflush未完了状態から新cacheで回復する。OSプロセスの強制終了試験はしていない。
- ejectの検証はwarp開始とinventory commitが同一瞬間に競合する実時間raceを網羅しない。
- CRABの並行テストはPostgreSQL storeを使うが、複数の実Hostプロセスと実simulatorを使った負荷競合は試していない。
- MCPコード検索は利用不可だったため、ソース検索に`rg`を使った。
