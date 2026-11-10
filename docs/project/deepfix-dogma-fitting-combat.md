# Dogma・Fitting・Combat 深掘り修正記録

確認日: 2026-11-10。今回、実際の差として確認し修正した範囲はレーザー結晶の耐久処理である。Dogma・fitting・combat 全領域を完了した記録ではない。未確認範囲は末尾に記す。

## 修正した差

### 1. ダメージを受けるレーザー結晶を弾薬数として消費していた

V24.01 クライアントでは、装填結晶の tooltip が `attributeDamage` を dogma location から読み取る (`eve/client/script/ui/inflight/shipModuleButton/moduleButtonTooltip.py:227-235,259-267`)。インベントリ tooltip は `crystalsGetDamaged` を持ち数量0のアイテムを対象にし、`GetDamageForCrystals` の結果をキャッシュする (`eve/client/script/ui/shared/tooltip/itemDamage.py:42-55,94-115`; `eve/client/script/ui/services/inventorysvc.py:381-388`)。`OnModuleAttributeChanges` は damage state attribute を更新し、quantity 0 ではアイテムを unload する (`eve/client/script/dogma/clientDogmaLocation.py:310-324,334-337`)。

EveJS は SDE の attribute 786/783/784/9 から結晶耐久条件を作り、サイクルごとの volatility hit で `moduleState.damage` を更新する (`server/src/services/dogma/crystalDurability.js:12-44`; `server/src/space/runtime/loadedChargeState.js:618-743`)。結晶は通常 ammo のように1発ごとに quantity を減らさず、耐久到達時だけ inventory item を削除し、quantity 0 の charge transition を通知する (`server/src/space/runtime/loadedChargeState.js:745-814`; `server/src/space/runtime/weaponCycle.js:506-520,556-564`)。アイテム damage は hitpoints 比で返す (`server/src/services/inventory/invBrokerService.js:11865-11898`)。

変更前の `AmmoHostTests.A_damageable_laser_crystal_keeps_its_round_and_is_worn_by_each_firing_cycle` は `PerCycle == 0` を要求し、旧実装の `PerCycle == 1` を失敗として検出した。Rust sim link v96 では charge の SDE volatility と現在 damage を渡し、レーザー結晶の rounds は発射ごとに減らさない。sim は damage attribute 3 の絶対値を通知し、耐久到達時は続けて quantity attribute 805 を0にして効果を停止する。Host は SimChargeSpent の絶対 damage をモジュール・装填 slot・type に照合して一度だけ保存し、摩耗結晶を singleton 化し、hitpoints 到達時にアイテムを削除する。再装填時にも item state から摩耗を再構成する。

### 2. 実SDE値で4,000発後に結晶が消耗しきらなかった

Pinned SDE build 3396210 の `typeDogma.jsonl:9870`、type 23071 Imperial Navy Multifrequency S は `hp=1`、`crystalVolatilityChance=1`、`crystalVolatilityDamage=0.00025`、`crystalsGetDamaged=1` である。EveJS は各サイクルの新 damage を小数12桁に丸める (`server/src/services/dogma/crystalDurability.js:41-43`)。

変更前の新しい sim regression test は、実SDEの volatility damage を4,000回加えた状態で耐久到達と charge removal を要求し失敗した。Rust の単純加算値は `0.9999999999999176` となり、1.0 に達しなかった。Rust sim に EveJS と同じ12桁の丸めを追加した。修正後は同じ4,000サイクルで damage が1.0、quantity 0、停止通知の順になる。

## V24.01 クライアント全体の照合範囲

`/home/user/eve-logic/eve/client` 全体で crystal damage / `GetDamageForCrystals` / `OnModuleAttributeChanges` / `OnDamageStateChanged` の参照を検索した。結晶表示・取得経路は上記 `inventorysvc.py`、`itemDamage.py`、`moduleButtonTooltip.py`、damage/quantity 通知の消費は `clientDogmaLocation.py` と `environment/godma.py:1342-1367` にある。後者は同一 batch に quantity 0 があればその item の他 attribute change を捨てるため、焼損時の最終 damage 表示は必須にせず、DB の削除と quantity 0 unload を正としている。`moondefencebutton.py:587` の `crystalsGetDamaged` 参照は starbase defense tooltip、`hangarView.py:801` は一般的な ship damage event listener であり、結晶 volatility の別処理ではない。

## EveJS とシミュレーション比較

- 指定 EveJS checkout の durability policy を Node から直接実行した。type 23071 の実SDE値、hitごとの決定論的 roll 0 で4,000回評価し、最終 damage 1.0 / `burnedOut=true` を確認した。
- eve.rs は `weapons::tests::a_real_sde_crystal_burns_out_on_its_four_thousandth_volatility_hit` で、実sim cycle、最終 dogma damage/quantity change、charge spent を検査する。`AmmoHostTests` ではHost側の永続化、duplicate report、焼損削除、Dogma attributeをDB上で検査する。`HomeStationHostTests.Registered_invbroker_returns_persisted_crystal_damage_from_item_state` は登録済み invbroker の wire 応答を実SDE crystal item で検査する。
- EveJS は `Math.random` を使い、Rust は module/cycle 時刻を鍵にした決定論的 hash roll を使う。cycle の再処理で摩耗結果を重複させないための実装差であり、SDE chance threshold は同じである。固定の roll 0 と chance 0/1 のケース以外で分布の統計比較はしていない。
- EveJS の durability policy は実行したが、EveJS server 全体とDBを起動した end-to-end 比較はしていない。GUI クライアント上での操作もしていない。

## 検証済み

`eve_p3` を使用し、Pinned SDE build 3396210 と mission SDE build 3405148 をロードした。Host tests 初回は `sde_mission.skinrComponentPointValues` がDBにないため起動失敗した。CIと同じ `eve-sde ensure --missions` を実行して不足schemaをロードし、再実行した。

- Rust: `cargo fmt --all -- --check`、`cargo check -p eve-space --tests`、`cargo clippy -p eve-space --all-targets -- -D warnings` は成功。
- Rust focused sim tests: crystal 関連3件 pass、skip 0。sim link fixture の valid/invalid decode/encode 各 focused test は pass、skip 0。
- .NET Release build `/warnaserror`: `Eve.Host.Tests` を含む変更関連プロジェクトのビルドは0 warning / 0 error。
- `Eve.Services.Space.Tests` の `AmmoHostTests`: 13 passed、0 failed、0 skipped。
- `Eve.Services.Inventory.Tests` の `InvBrokerTests|InventoryRpcFixTests`: 68 passed、0 failed、0 skipped。
- `Eve.Ships.Tests` の ammo / reload / crystal volatility / item dogma / module bonus / mutated module filter: 27 passed、0 failed、0 skipped。
- `Eve.Services.Dogma.Tests.ReloadServiceTests`: 9 passed、0 failed、0 skipped。
- `Eve.Space.Tests.SimCodecTests`: 495 passed、0 failed、0 skipped。
- `Eve.Host.Tests.HomeStationHostTests`: 4 passed、0 failed、0 skipped。
- Node から EveJS policy helper を使った実SDE値4,000-cycle比較: pass。

## 未確認・残る範囲

- 今回は一般的な dogma stacking / attribute evaluation、skill bonus、fitting validation・保存fit、module activation の他の effect、capacitor、heat、drone damage、damage application、repair、shield/armor recharge を再監査・再実行していない。既存の監査・status は `docs/project/audit-fixes-dogma.md`、`docs/project/audit-fixes-fitting.md`、`docs/project/COMBAT_STATUS.md` に記録されているが、この変更の検証として数えていない。
- GUI V24.01 client、EveJS server 全体との同一DB end-to-end、長期の volatility chance 分布、並行する複数 turret / weapon bank での結晶寿命は未確認。
- push 後の GitHub Actions main CI は未確認。
