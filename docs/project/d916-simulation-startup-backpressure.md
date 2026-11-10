# d916d77 simulation startup backpressure

## 検証した事実

- main の CI run 1683（`d916d77b670d`）は `test_a_ship_undocks_flies_and_docks_as_the_reference_did[race-1]` で失敗した。undock RPC は `CustomNotify` を返し、後続の ballpark と dock 通知が欠けていた。
- 同じ run の `live-logs/host.out` に `the simulation link: outbound queue is full (64 frames)` が記録されている。
- `RegisterEssSystemsAsync` は pinned SDE の対象 nullsec 全てに ESS を登録する（`dotnet/src/Eve.Host/Composition/EssComposition.cs:44-65,69-88`）。この SDE に同じ条件を照会すると 3,285 系が対象だった。
- `d916d77` は ESS 配置前に全系のアンカーを一括取得するよう変更した。その後のループは配置ごとに同期 `PlaceStructure` を呼び、`SimClient.Send` は満杯の bounded queue を link failure にする（`dotnet/src/Eve.Host/Composition/EssComposition.cs:233-253`, `dotnet/src/Eve.Space/SimClient.cs:27-29,584-603,1174`）。これにより 3,285 件の配置が 64 枠へ一気に送られる。
- `SimClient.SendAsync` は queue に空きが出るまで待機する既存の backpressure 経路である（`dotnet/src/Eve.Space/SimClient.cs:659-689`）。単系 ESS 配置はこの経路を使う。

## 変更と確認

- `ISimulation.PlaceStructureAsync` は bounded queue が満杯でも待機して通常 frame を送る。最初の修正は起動時に全構造物を送るため socket が開く前に時間切れになった（CI run 1684）。
- 現行コードは起動時の全系配置をやめ、`SpaceComposition` で `SystemEntered` を購読し、その系の ESS だけを配置する（`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:148-150,624-626`）。`PgEssStore.PlacementAsync` が対象系の1行だけを取得し、`PlaceEssBallsAsync` がキャッシュ済み SDE anchor と `PlaceStructureAsync` で配置する（`dotnet/src/Eve.Services.Space/Ess/PgEssStore.cs:210-222`, `dotnet/src/Eve.Host/Composition/EssComposition.cs:217-258`）。
- 切断や不正な SDE データは系単位でログに残して捕捉し、Host 起動を失敗させない（`EssComposition.cs:220-258`）。simulation が未接続のとき元の one-way `PlaceStructure` が drop していた挙動を保つ。
- `CharacterHostTests.EssDeployment.Host_places_the_claimable_nullsec_ess_when_a_pilot_enters_its_system` は未訪問系には ESS ball がなく、pilot が入った系には ESS ball が現れることを実 simulation と real Host registration で確認する。
- CI run 1685（`5fb8e2075083`）は system-entry 修正後に green。server stack + headless client E2E、Windows、全 .NET host shards を含む selected jobs が success した。
- ローカルの旧 backpressure revision では `SimClientTests` 42/42、`StructureCompositionTests` 20/20、`CharacterHostTests` 75 passed / 0 failed / 1 skipped を確認した。skip は pass と数えない。

## 未確認

- graphical V24.01 client では確認していない。CI run 1685 は headless client を通した検証である。
- `CharacterHostTests.Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` はローカル実行で skip されたため pass ではない。
