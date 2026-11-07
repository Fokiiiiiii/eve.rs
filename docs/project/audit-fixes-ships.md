# Ship services audit fixes (r14)

## 確認結果

監査レポートの5件は、コードと V24.01 クライアント、EveJS 261028.1 を照合した結果、すべて実在する不具合でした。誤検知として見送った項目はありません。

### P2 — プローブ回収をアイテム返却と記録削除の同一トランザクションにした

回収対象の所有者・solar system を確かめた上で probe 行をロックし、launcher / cargo のチャージ更新と probe 行削除を同じ PostgreSQL transaction で commit します。回収サービスは commit 後にアイテム変更通知、続けて `OnRemoveProbe` とシミュレーション上の削除を送ります。同じ ID の再試行は行がないため再返却しません。実装は `dotnet/src/Eve.Ships/CharacterShips.Probes.cs:193-225`、行ロックと削除は `dotnet/src/Eve.Ships/PgProbeStore.cs:122-163`、通知順は `dotnet/src/Eve.Services.Space/SpaceProbes.cs:210-225` です。

V24 は `scanSvc.py:324-341` から `scanMgr.RecoverProbes` を呼び、EveJS は `scanMgrService.js:2854-2883` で記録削除後にチャージを返します。実装では永続アイテムと probe 行の更新を原子的にするため、この二つを一つの transaction にまとめました。ホスト側は `dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:71-85` から実 `SpaceProbeScans` handler に到達します。

回収の削除 trigger を失敗させる回帰テストでは、失敗時にチャージ・記録・通知が変わらず、service 再生成後の再試行が一度だけ返却することを確認しました。別 service instance からの同時回収も二重返却・二重通知になりません。

### P2 — probe 発射のチャージ消費と全 probe 記録を同一 transaction にした

キャラクター単位の transaction lock を取り、launcher / charge の現状と active probe 数を再検証してから、要求数すべての ID・記録を作成し、チャージ減算と全記録挿入を commit します。ボール配置と `OnNewProbe` は commit 後です。実装は `dotnet/src/Eve.Ships/CharacterShips.Probes.cs:110-173`、サービスの commit 後処理は `dotnet/src/Eve.Services.Space/SpaceProbes.cs:125-169` です。

V24 の入口は `scanSvc.py:487-495`、EveJS の入口・検証は `moduleOperation.js:3652-3677` と `moduleOperation.js:452-565`、発射処理は `moduleOperation.js:1032-1158` です。ホストでは `dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:82-94` が実 `SpaceSessions` を `dogmaIM.LaunchProbes` に注入します。`dotnet/tests/Eve.Host.Tests/DogmaModuleActivationHostWireTests.cs:30-64` は登録済み Host route の module ID / count を検証します。

2個目の probe insert を失敗させたテストでは charge と先行 insert がすべて rollback され、ボール・通知も発生しません。異なる `SpaceProbes` instance の同時発射も active 上限を越えず、commit した側だけがチャージを消費して通知します。

### P2 — CPU / powergrid 不足の UserError 名と引数を V24 に合わせた

Dogma の現状態から module の必要量、ship の残量、総出力を計算して `FitFailure` に渡し、`NotEnoughCpu` / `NotEnoughPower` と `moduleName=(UE_TYPEID,typeID)`, `moduleType`, `require`, `remaining`, `total` を返します。実装は `dotnet/src/Eve.Fitting/FittingRules.cs:103-127`、拒否変換は `dotnet/src/Eve.Services.Ships/ShipErrors.cs:22-41`、`CharacterShips.SetOnlineAsync` の接続は `dotnet/src/Eve.Ships/CharacterShips.cs:681-691` です。

V24 の定義は `baseDogma.py:28-47`、EveJS の変換は `moduleOperation.js:2066-2079` です。`ShipEventTests.Onlining_a_module_without_CPU_or_power_returns_the_clients_UserError_arguments` は実 `DogmaIMService.SetModuleOnline` 呼び出しで wire error 名・全引数、拒否時に通知や state change がないことを確認します。

### P2 — 必要な全スキルを `FittingHasSkillPrerequisites` で返す

`FittingRules.MissingSkill` が不足中の全スキルとレベルを SDE 属性順に保持し、`CharacterShips` が全 skill type を static data から確保します。拒否は `requiredSkills`, item type ID, `skillCount` を持つ `FittingHasSkillPrerequisites` になります。実装は `dotnet/src/Eve.Fitting/FittingRules.cs:85-101`、`dotnet/src/Eve.Ships/CharacterShips.cs:92-113,681-685`、`dotnet/src/Eve.Services.Ships/ShipErrors.cs:67-89` です。

V24 の callsite は `clientDogmaLocation.py:447-456`、EveJS の在庫 fitting error は `invBrokerService.js:8312-8333` です。`FitFittingTests.Fitting_a_module_with_multiple_missing_skills_returns_all_prerequisites_in_the_client_error` は実 inventory `Add` route を使い、SDE 上の Gatling Pulse Laser I に不足する2スキルと item location が変わらないことを確認します。

### P3 — hardpoint 拒否で総枠数と使用数を保持する

fitting rule は Dogma の残り枠と現 fitted turret / launcher 数から総枠数を復元し、候補以外の使用数と合わせて失敗データに保持します。hardpoint 拒否時は総数が正で使用数が総数以上なら「全枠使用中」、総数が0なら「枠なし」と伝えます。実装は `dotnet/src/Eve.Fitting/FittingRules.cs:261-274`、通知は `dotnet/src/Eve.Services.Ships/ShipErrors.cs:92-103` です。

EveJS は `liveFittingState.js:1151-1180` で総枠を得て、`invBrokerService.js:5701-5736` で総枠・使用数を比較・保持し、`invBrokerService.js:8293-8305` で文言を選びます。`RulesTests.Turrets_and_launchers_need_a_hard_point_each` は枠なしと満杯の双方の数値を検証します。DB-backed `FitFittingTests.Fitting_past_the_last_turret_hardpoint_reports_that_the_hardpoints_are_full` は SDE の Machariel に7基の turret を fit し、8基目の拒否文と item が hangar に残ることを確認します。

## テスト・ビルド

修正前に新しい失敗回帰テストを実行し、元の誤動作を確認しました。probe 発射は insert failure 後に charge が5個まで減り、回収は削除 failure 後に charge が6個になりました。CPU / power とスキル拒否は `CustomNotify` を返し、満杯 Machariel の拒否文は「この船には turret hardpoints がない」でした。

`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4` を使い、最終確認では次の選択テストが全件成功しました（skip 0 件）。

- `Eve.Services.Space.Tests`: `ScanProbeHostTests` と module online を参照する4クラス、59 passed。
- `Eve.Services.Dogma.Tests`: `ShipErrorTests`, `ShipEventTests`, `OnlineCommandTests`、24 passed。
- `Eve.Services.Inventory.Tests`: `FitFittingTests`, `CargoCapacityInventoryTests`、15 passed。
- `Eve.Fitting.Tests`: `RulesTests`, `RealShipTests`, `IndustrialInvulnerabilityFittingTests`、66 passed。
- `Eve.Ships.Tests`: `ShipTests`, `RealSdeShipSweepTests`, `CargoCapacityPersistenceTests`、36 passed。
- `Eve.Host.Tests`: `Probe_launch_reaches_the_registered_bound_dogma_route_with_the_client_arguments`、1 passed。

変更した本体 / test project は Release build を `-warnaserror` 付きで実行し、すべて warning 0 / error 0 でした。これは選択クラスの結果であり、全テスト suite の実行結果ではありません。

## 未確認

- V24.01 の graphical client での表示・操作は行っていません。
- `AbyssalHostFlowTests.Tournament_cutoff_waits_for_actual_host_remote_repair_result` と `Tournament_fancy_registered_wire_reads_live_two_team_metrics` は外部 Rust simulator を要します。`EVE_ABYSSAL_SIM_BINARY` と `EVE_ABYSSAL_SIM_SDE` がこの環境にないため実行していません。
- GitHub Actions / main CI の結果は push 後に確認します。
