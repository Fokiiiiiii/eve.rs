# Mining / compression / hauling deep fix handoff

## 判定

確認できた不具合は、採掘 cycle の成果物が種類や船の SDE 容量に関係なく通常カーゴへ入ることと、部分的に残る容量を yield 計算へ反映しないこと。鉱石、氷、ガス、月面フィールドの採掘は `MineAsteroidAsync` に合流するため、同じ保存先選択と transaction を使う。採掘量・廃棄量の既存式は維持し、hold の残容量を clamp に渡し、transfer 量と critical bonus をその上限に収める。

## 全体経路と判断根拠

- V24.01 の採掘結果は inventory item の `flagID` を使ってホールドに現れる。`inventorycommon/const.py:3390-3419` は dogma 容量属性から採掘ホールドを列挙し、`eve/client/script/ui/shared/inventory/treeData.py:323-330` が各ホールドを ship tree に置く。`eve/client/script/environment/invCache.py:220-232` は item change を適用して `OnItemChange` を発火し、`invContainers.py:228-245` が専用ホールド controller を定義する。`notifications/client/oreMinedNotificationAdapter.py:17-33` は採掘ログと asteroid scanner を更新する。よって保存 item の flag が UI 上の保管先を決める。Ship dogma は `dotnet/src/Eve.Dogma/DogmaStatic.cs:10-13` のとおり SDE から読み込まれる。
- V24.01 の採掘仕様は [EVE University: Mining](https://wiki.eveuniversity.org/Mining) にある。採掘物は cargo または mining hold に入り、選択した格納先が満杯になると cycle は ore を届けない。EveJS 261028.1 は `server/src/services/mining/miningInventory.js:112-188` で一般・鉱石・氷・ガス hold の適合と優先順を決め、`server/src/services/mining/miningRuntime.js:551-566` で適合 hold が存在する場合は満杯でも通常 cargo に流さない。これを実装判断に使った。
- eve.rs の drone と fitted module の work cycle は `dotnet/src/Eve.Services.Space/SpaceContainers.Work.cs:329-371` から `MineAsteroidAsync` へ進み、コミット後に `OnItemChange`、module の場合は続けて `OnOreMined` を送る。`dotnet/src/Eve.Ships/CharacterShips.Cargo.cs` は hold 選択、容量 clamp、成果物、asteroid 残量、ledger、`OreMinedEvent` を管理する。flag 以外の状態遷移・通知順は変えず、既存トランザクションを保った。
- Moon extraction は `dotnet/src/Eve.Services.Space/SpaceMoonFields.cs:56-96` の extraction event handler で field を materialize し、月面 ore はその field 内の asteroid として通常 mining cycle に入る。`CharacterShips.Cargo.cs` が extraction observer metadata を mining event に保存し、`dotnet/src/Eve.Host/MiningLedgerRecorder.cs:15-37` が ledger と moon observer event を記録する。`dotnet/tests/Eve.Services.Space.Tests/MoonFieldTests.cs` の field と期限切れケースも確認した。moon ore だけ別の cargo grant 経路はない。
- V24.01 の in-space compression caller は `itemcompression/client/itemCompression.py:8-15`（6 要素 tuple、`None` は失敗）、`itemcompression/client/ui/compress_in_space_controller.py:12-19, 48-69`（cargo と一般・鉱石・氷・ガス hold を走査し部分失敗を表示）、`itemcompression/client/ui/compression_window.py:84-114`、`eve/client/script/ui/services/menusvc.py:3402-3403`。Host は `dotnet/src/Eve.Host/OperationalRpcServices.cs:203-207` と `dotnet/src/Eve.Host/Rpc/ItemRpc.cs:58-63` から `SpaceContainers.Compression.cs:32-95` に接続し、fleet、type list、距離、tether、所持先を検証してから item change を送る。`CharacterShips.Compression.cs:20-68` は圧縮済み SDE type と元 quantity を同じ hold に置換・結合し transaction で commit する。EveJS の `server/src/services/mining/inSpaceCompressionMgrService.js:36-72` と `miningIndustry.js:628-668` も同じ 6 値と 1:1 quantity を返す。[EVE University: Compression](https://wiki.eveuniversity.org/Compression) の圧縮後体積（鉱石 1%、氷・ガス 10%）は SDE の圧縮 type の volume によるため、quantity を割る処理は追加しなかった。
- hauling の入口として client の一括圧縮が上記5つの ship hold を読むこと、圧縮が同じ hold に item change を出すことを追った。通常 inventory 移動は `dotnet/src/Eve.Ships/CharacterShips.Containers.cs:380-465` の一括 space transfer が type/hold allowlist と容量を検証してから変更する。圧縮後も item flag が維持され、V24.01 の inventory tree と transfer path が同じ格納先を扱う。

## 修正

`CharacterShips.Cargo.cs` は成果物の種類に応じて専用 asteroid / gas / ice hold、次に一般 mining hold を選び、SDE ship dogma の容量が正で、かつ item が入る hold だけを候補にする。候補がない船だけ通常カーゴを使う。選択した hold の容量拒否、item 作成、`ItemChanged` の flag を一貫させた。選択済み mining hold が満杯なら `CARGO_FULL` を返し、asteroid や cargo を変更しない。

今回 `MineAsteroidAsync` は、その保存先の `Room.Free` と yield type の SDE unit volume から転送可能な全数を求め、鉱石残量と cycle volume に対する clamp を既存 `MiningYield` に渡す。通常量は転送可能数まで、critical bonus は通常量を差し引いた残りまでに制限する。廃棄量は既存式で asteroid の通常採掘後の残量までに制限される。転送可能な整数単位がない場合は `CARGO_FULL` で拒否し、asteroid、在庫、mining fact は変更しない。成功時は在庫、asteroid 数量、cycle ledger、`OreMinedEvent` を元の PostgreSQL transaction で確定し、Host は commit 後に `OnItemChange`、続けて `OnOreMined` を送る。重複 cycle は ledger から返すため、再通知・再 grant しない。

V24 の全 client consumer も追跡した。`notifications/client/oreMinedNotificationAdapter.py:17-27` は `quantity_added`, `quantity_removed`, `amountWasted`, `amountCritBonus` などを読む。`mining/client/asteroid_scanning_service.py:40-43` は normal と wasted を scanner の asteroid 数量から引き、`mining/client/ui/asteroid_quantity_gauge_circular.py:144-148` は normal・wasted・critical signal をゲージ表示に使う。`mining/client/mining_overlay_controller.py:50-55` は採掘音を、`eve/client/script/ui/inflight/bracketsAndTargets/asteroid_bracket.py:93-99` と `eve/client/script/environment/spaceObject/ship.py:481-485` は critical bonus を使う。Host の回帰は wire quantity と `OnItemChange` → `OnOreMined` の順を検証する。

EveJS との小数境界の差は `docs/reference/compat/DEVIATIONS.md` に記録した。EveJS の `floor(0.3 / 0.1)` は IEEE 754 の丸めで 2 になる一方、eve.rs は既存 `RoomTolerance` 内で 3 units が 0.3 m³ に収まることを認める。

回帰テスト `dotnet/tests/Eve.Services.Space.Tests/SpaceContainerTests.cs:452-519` は SDE の Venture（general mining hold 134）、Cockroach fixture（asteroid hold 182）、Caldari rookie ship（cargo 5）を比較し、採掘ホールドが満杯でも cargo に流れず asteroid が残ることを確認する。`dotnet/tests/Eve.Services.Space.Tests/DroneHostTests.cs:627-666` は Cockroach 上の採掘ドローンが枯渇した asteroid から採掘した鉱石を flag 182 で永続化することを検証する。`dotnet/tests/Eve.Ships.Tests/GasCloudMiningTests.cs:11-37` は Prospect の gas cycle が一般 mining hold に入ることを、`IceMiningHoldTests.cs:8-37` は Endurance の ice cycle が SDE 容量どおり一般 mining hold に入ることを検証する。以前の確認では mining hold のある船の2行が cargo flag 5 になるためテストが失敗した。push 後の CI でも DroneHostTests が従来 cargo flag 5 を要求して失敗し、`eve_p5` の再実行で同じ失敗（生成 item flag 182）を再現したため、その期待値を修正した。

clamp の DB 回帰は `dotnet/tests/Eve.Services.Space.Tests/WorkCycleHostTests.cs` に追加し、部分容量3 unit・0 unit・厳密に1 unit、waste と critical bonus の同時発生、Endurance の ice hold fallback、Venture の gas hold fallback、重複 cycle の inventory/event/notification 一回性を通す。`SpaceContainerTests`、`DroneHostTests`、`MoonFieldTests` と Ships 側 `MiningProfileTests`、`GasCloudMiningTests`、`IceMiningHoldTests` は直接呼び出し元の隣接回帰として再実行する。

## 検証範囲

- DB-backed headless tests は自分の `eve_p5` で実行。Space: SpaceContainerTests / MoonFieldTests / MiningActivationTests / CompressionHostTests は 46 passed・0 skipped。Ships: GasCloudMiningTests / MiningProfileTests / IceMiningHoldTests は 9 passed・0 skipped。CI で失敗した DroneHostTests の採掘ドローンケースは、flag 5 の旧条件でローカル再現し、flag 182 に直した後、同クラスと関連 Space 回帰クラスで 74 passed・0 skipped。
- 今回の DB 回帰は `eve_p3` で WorkCycleHostTests 18 passed・0 skipped、DroneHostTests / SpaceContainerTests / MoonFieldTests 63 passed・0 skipped、MiningProfileTests / GasCloudMiningTests / IceMiningHoldTests 9 passed・0 skipped。
- `Eve.Ships`、`Eve.Services.Space.Tests`、`Eve.Ships.Tests` の build は警告・エラーなし。
- EveJS 261028.1 の `miningMath.computeMiningResult` を実行し、partial 2-unit と waste/critical 150-unit シナリオを確認した。0.3 m³ / 0.1 m³ の境界は EveJS が IEEE 754 により2 units、eve.rs が既存容量許容差で3 unitsになる点を DEVIATIONS に記録した。EveJS の完全な server/runtime フローは起動していない。
- 実 graphical V24.01 client での画面確認は行っていない。テストは headless で、UI の見た目そのものはこの記録の検証対象外。
