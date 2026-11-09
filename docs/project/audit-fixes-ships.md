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
- push 前の main CI run 1651（`582b968d`）は全 job 成功。push 後の CI run 1653（`7f9b36e9`）も全 job 成功。
# Ammo audit findings (90-audit-eve-ships-r14)

監査報告 `/home/user/reports/90-audit-eve-ships-r14.md` の3件を、eve.rs の実装、V24.01 クライアント全体の callsite、EveJS の処理と照合した。3件とも再現する不具合だったため修正した。誤検出として除外した finding はない。

## 1. fitting flag への ammo unload

`UnloadAmmo` の destination が tuple/list なら第3要素を flag として読む。V24.01 の `clientDogmaLocation.py:1088-1127` は `UnloadAmmoToContainer` の destination tuple を RPC に渡し、`fittingSlotController.py:143-153` と `fittingSvc.py:1218-1220` に unload callsite がある。クライアント全体を検索すると、この操作は dogma location、inventory controllers/cache、fitting UI、ship-module menu から呼ばれる（後述の callsite一覧）。EveJS `server/src/services/dogma/dogmaService/moduleOperation.js:3995-3999` は module slot flag への移動を `CantMoveChargesBetweenModules` で拒否する。

eve.rs は slot flag を unload 前に拒否し、同じ user error を返す。`CharacterHostTests.DogmaIM_unload_ammo_rejects_a_fitting_destination_without_changing_items_or_notifying` は Host RPC、charge item の場所・flag・数量、他 module、通知なしを確認する。変更前の isolated baseline ではこのケースが失敗し、拒否 error がなく charge 配置が変わることを確認した。

## 2. inventory container からの load / container への unload

V24.01 `clientDogmaLocation.py:980-1027,1061-1072,1088-1127` は選択 stack の実 location を `LoadAmmo` に渡し、unload は `(locationID, ownerID, flag)` と quantity を渡す。inventory UI の経路は `invControllers.py:193-210,503-517` と `invCache.py:1018,1050,1119`、fitting 経路は `fittingUtil.py:207`、`fittingSlotController.py:152,188`、`tryFit.py:187` にある。呼び出し先は同じ `clientDogmaLocation` RPC である。EveJS `moduleOperation.js:2943-2981,2819-2839,4089-4096` は船に隣接した container の到達性を確認し、destination location / owner / flag を inventory move に渡す。

Host は container source の owner と到達性を解決し、corporation source では division の `Take` role を確認する。domain transaction は選択 stack の owner/location/flag を検証し、全量移動では pilot-owned fitted charge に owner を移し、unload では owner を保ったまま container stack に merge または作成する。reload 完了時にも container の到達性と Host 側の権限を再確認する。

Host の DB 回帰テストは個人 container の load/unload と restart 後の状態、corp container の権限不足・許可後の owner 移行、距離外 container の拒否、通知と wire return IDs を確認する。変更前の isolated baseline では個人 container load、corp 権限不足、距離外 source の各ケースが失敗した。個人 container unload は変更前から通っていた。

## 3. ship cargo hold 容量

V24.01 `clientDogmaLocation.py:1106-1132` は ammo unload 時の `NotEnoughCargoSpace` を捕捉し、cargo capacity 不足をユーザーに知らせる。EveJS `moduleOperation.js:4089-4096` は一般 inventory move に委譲し、`inventory/itemStore.js:6656-6718` の destination 検査には容量計算がない。この点は EveJS の挙動を踏襲せず、V24.01 の client-visible failure contract と ship hold の authoritative capacity に合わせた。

eve.rs は全対象 module の実移動量を transaction 前に合算し、既存 cargo 内容量と照合してから変更する。`CharacterHostTests.DogmaIM_unload_ammo_fits_the_exact_remaining_cargo_volume_and_merges_the_stack` は残量ちょうどの成功を、`...preflights_all_modules_when_ship_cargo_has_room_for_only_one` は部分成功を許さず item と通知を不変に保つことを確認する。変更前の isolated baseline では複数 module の容量超過ケースが失敗した。

## クライアント全体の検索範囲

`/home/user/eve-logic/eve/client` 全体を `LoadAmmo|UnloadAmmo|chargeLocationID|UnloadAmmoToContainer` で検索した。該当 callsite は以下のファイルにあった。

- `script/dogma/clientDogmaLocation.py:152,973-1027,1061-1072,1088-1140`
- `script/environment/invControllers.py:193-210,503-517`
- `script/environment/invCache.py:1018,1050,1119`
- `script/environment/fittingSvc.py:1220`
- `script/ui/inflight/shipModuleButton/shipmodulebutton.py:737`
- `script/ui/services/menusvc.py:2407`
- `script/ui/shared/fitting/fittingUtil.py:207`
- `script/ui/shared/fitting/fittingSlotController.py:143-188`
- `script/ui/shared/fittingScreen/ghostFittingSvc.py:972-976`
- `script/ui/shared/fittingScreen/fittingPanels/minihangar.py:290`
- `script/ui/shared/fittingScreen/tryFit.py:187`
- `script/ui/shared/fittingScreen/fittingRadialMenu.py:37` (UI action declaration; its handler is the ghost fitting service)

client checkout は `1dbb5711a072a146e1d228365668ac05655eefe1`。EveJS checkout `/home/user/evejs-261028.1` はソース delivery で、`node_modules` がなく package scripts も定義されていないため実行比較はできなかった。ソースの行単位照合のみ行った。

## main CI red の解消

作業前に main の run 1649（`73af9f0e`）が headless `test_a_new_character_has_the_skills_the_reference_gives` の notification 順序 assertion で失敗していることを確認した。main の `214efbfd` は `SelectCharacterID` の最初に `OnSubscriptionTimeUpdateServer` を送り、その後 `OnCharacterSelected` を送る。EveJS `subscriptionNotices.js:4-10,93-101` は subscription time を SelectCharacterID の最初に知らせると明記し、`charService.js:1531-1537` で選択処理冒頭に送る。V24 client `plexPanel.py:160` は通知を受け、表示中なら PLEX panel を更新する。よって live headless test の eve.rs 経路を旧順序の期待から、先頭2通知が `OnSubscriptionTimeUpdateServer`, `OnCharacterSelected` であるという厳密な期待に更新した。比較対象数も両通知を除いた後に検証するため、session change の検査は維持した。

## 検証状況

- 修正前に追加テストだけを isolated baseline worktree に適用し、fitting flag 拒否、personal/corporate container source、範囲外 source、multi-module cargo overflow の regression を確認した。変更前の結果は失敗で、成功扱い・skip 扱いにはしていない。
- 修正後は Host 対象クラス 491 passed / 0 failed / 1 skipped、Ships の `AmmoTests` / `ReloadTests` 15 passed / 0 failed / 0 skipped、別クラス `AmmoRaceTests` 3 passed / 0 failed / 0 skipped、`ShipErrorTests` 11 passed / 0 failed / 0 skipped、Dogma の隣接2クラス 3 passed / 0 failed / 0 skipped。
- 変更対象 Host / Ships / Dogma test projects の Release build はそれぞれ 0 warnings / 0 errors。
- Host の1 skipped は `CharacterHostTests.Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` に既存 skip 属性があるため。pass には数えていない。
- GUI client / 実 client trace は未確認。EveJS は実行せず、ソース参照のみ。

## インスペース grouped unload の追補

容量不足の Host 経路も確認したところ、dock 内の grouped unload と異なり、インスペース処理はモジュールごとにシミュレーションを更新してから個別 transaction で在庫を更新し、`ShipRefused` を UserError に変換していなかった。2基の gun が各40発を保持し、cargo に42発分の空きがあるケースでは、V24.01 が呼ぶ grouped `UnloadAmmo` は全量を先に検査すべきだが、元の Host 経路は1基目の40発だけ移して2基目を無言で拒否していた。

V24.01 `clientDogmaLocation.py:1127-1132` は容量不足を `NotEnoughCargoSpace` として扱い、`1136-1140` は複数 module ID の `UnloadAmmo` を一度に呼ぶ。EveJS `moduleOperation.js:4042-4051,4088-4099` も grouped request の対象を順に処理する。eve.rs は各 gun の実残弾差を simulation から集めた後、全対象の在庫移動と容量検査を単一 domain transaction で行う。拒否時は `ShipErrors.Of` で `NotEnoughCargoSpace` を返し、inventory を変えず、既に simulation から引いた残弾を `RoundsChange.Add` で戻す。実残弾差を使うため、発射済みで DB の item 数量より simulation の残弾が少ない場合も、DB 数量を使った保守的な誤拒否をしない。

正しい再現条件に直した Host E2E テストは、修正前に `Assert.Throws` が「exception が出なかった」と失敗した。修正後は `CharacterHostTests.DogmaIM_unload_ammo_in_space_returns_the_cargo_capacity_user_error_without_mutating_items` が実 Host 登録 route を通り、exact UserError payload、両 charge item と cargo の不変、通知なしを確認する。`ReloadServiceTests` の active-gun ケースは sim が5発を取り除き DB charge が80発ある状態で、5発分の room なら5発だけ unload されること、room が4発分なら正しい error・inventory 不変・5発の simulation restore request を確認する。

Host のこのテストでは `SpaceSessions` を構成したが、pilot を実 simulator に参加させていない。稼働 gun の前後差と容量 refusal 後の restore は Dogma test rig の `FakeRounds` で検証した。GUI client と実 Rust simulator 上の grouped ammo unload は未確認。

最初の Host テスト草案は `EVE_SIM=off` で実行してしまい、インスペース route を通っていなかったため、その結果は回帰確認に使わなかった。`simSocket` を設定して Host の `SpaceSessions` を構成した後、旧 per-module route で exception が出ない red を確認し、その後の実装で同じ Host test が通ることを確認した。

## 最終検証

`eve_p5` に対し、修正対象プロジェクトを Release build し warning 0 / error 0 を確認した。変更に関連する選択テストは次のとおり。

最新 `origin/main` を merge した後にも `Eve.Host.Tests`, `Eve.Ships.Tests`, `Eve.Services.Dogma.Tests` を `-warnaserror` 付き Release build し、すべて warning 0 / error 0 を確認した。

- `Eve.Host.Tests.CharacterHostTests`: 62 passed / 0 failed / 1 skipped。
- `Eve.Ships.Tests` の `AmmoTests`, `AmmoRaceTests`, `ReloadTests`: 18 passed / 0 failed / 0 skipped。
- `Eve.Services.Dogma.Tests` の `ReloadServiceTests`, `ShipErrorTests`, `MissingDogmaRpcTests`, `StructureControlInfoTests`: 23 passed / 0 failed / 0 skipped。
- merge 後の `tools/dev/run-eve-live.sh` で `test_a_new_character_has_the_skills_the_reference_gives` を実 Host / front / Rust simulation に対して実行し、4 race case が passed / 0 failed / 0 skipped。初回は `EVE_MARKET_SEED=auto` による Host 起動待ちで test 開始前に終了し、`EVE_MARKET_SEED=skip` で再実行した。

Host の skip は `Confirmed_ship_clone_jump_moves_the_capsule_settles_state_and_notifies_after_space_entry` で、`EVE_ABYSSAL_SIM_BINARY` と `EVE_ABYSSAL_SIM_SDE` がないため実行されなかった。pass に含めていない。client GUI と実 Rust simulator 上の grouped ammo unload は未確認。EveJS runtime は実行せず、ソース delivery のため行単位照合のみ行った。
