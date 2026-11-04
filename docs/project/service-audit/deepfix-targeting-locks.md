# ターゲットロック深掘り引き継ぎ

## 調査したプレイヤー経路

V24.01 全体を検索。通常の `TryLockTarget` 呼び出しは次のとおり。

- UI: `nodegraph/client/actions/target.py:33`、`eve/client/script/ui/eveCommands.py:2878`、`eve/client/script/ui/services/menusvc.py:2829`、`eve/client/script/ui/shared/fleet/fleetbroadcastexports.py:150`、`fleetBroadcastCont.py:199-200`、`eve/client/script/ui/inflight/drone.py:299`、`moonmining.py:2263`、`drones/droneEntry.py:228`。
- TargetMgr のオートターゲットとトグル: `eve/client/script/parklife/targetMgr.py:467, 1305-1317, 1401`。開発用呼び出しは `eve/devtools/script/form_tournament.py:818`。
- `TryLockTarget` はすでにロック中・ロック処理中、ballpark slim がない対象、targetable でない group、自分の structure を早期に除外する（`targetMgr.py:1305-1317`）。`_LockTarget` はローカル countdown を開始して Dogma の `AddTarget(tid)` を呼ぶ。返答は `(flag, targetList)`。flag が false のときだけクライアントが即時に追加し、true のときは `OnTargets` を待つ（`targetMgr.py:1362-1371`、Host の同じ wire shape は `DogmaIMService.Targeting.cs:49-57`）。
- UI の `IsInTargetingRange` は ball の surface distance と ship の `maxTargetRange` を比べるだけの事前確認。fleet broadcast は false なら呼び出さないが、通常の操作はサーバー側拒否を扱う（`targetMgr.py:1334-1351`、`fleetBroadcastCont.py:199-200`）。
- `_LockTarget` は `DeniedShipChanged` と `TargetingAttemptCancelled` だけを抑制し、それ以外は FSD user error として表示する（`targetMgr.py:1362-1381`）。Host は距離拒否を `TargetNotWithinRangeGeneric(desiredRange, targetName, actualDistance)`、枠不足を `TargetingSystemsAlreadyFullyUtilized(limit)`、自己・warp・jam・invulnerability を個別の client error に対応づける（`DogmaIMService.Targeting.cs:17-46`）。
- `AddTarget` wire は Host の `DogmaIMService.Targeting.cs:53-57` から `SpaceSessions.OrderAsync`（`SpaceSessions.Modules.cs:40-89`）、sim command、`Scene::target_order` に届く。sim が返す refusal は Host が client error に変換し、受理した lock は `OnTargets` notification に変わって `SpaceSessions.cs:865-866,1029-1033` で service notification として転送される。
- `OnTargets` は `godma.py:243-246` から配信され、`targetMgr.py:579-596` が add/clear/lost/otheradd/otherlost を処理する。ロックを受けた側の threat state は `targetMgr.py:1189-1205` で解除される。自身が失った対象が `Exploding` のときだけ死亡アニメーションを使う（`targetMgr.py:1130-1144`）。キャンセルは `OnFailLockTarget` によりローカル countdown を止める（`targetMgr.py:1286-1302`、module UI `shipmodulebutton.py:611-613`）。
- `OnModuleAttributeChanges` は `godma.py:1341-1361` から dogma 状態へ入り、`clientDogmaLocation.py:309-345` が更新する。bracket UI は scan-resolution / signature の更新時に lock timer と経過割合を再計算する（`ui/inflight/bracket.py:1064-1124`、`ui/inflight/bracketsAndTargets/inSpaceBracket.py:383-443`）。godma は `OnTargets` を属性通知より先に配信する（`godma.py:230-246`）。

Passive targeting と NPC の別入口も全体検索した。passive targeting は `eve/common/script/dogma/pythonEffects/migratedeffects.py:217` の `AddTargetEx`、NPC logistics/combat は `behaviors/actions/logistics.py:354-365` と `behaviors/actions/combattargets.py:421-422`。structure-owned cursor は `playerOwnedTargetMgr.py:83-89` の `AddTargetOBO` という別 RPC。今回の変更対象は通常 ship Dogma `AddTarget` と sim に統合された lock origin であり、structure-owned cursor RPC はこの経路に含めていない。

## 判断根拠

- EVE University [Targeting](https://wiki.eveuniversity.org/Targeting) は lock 秒数を `40,000 / (scan resolution × asinh(signature radius)^2)` とし、scan resolution / signature radius の双方で時間が変わること、通常 ship 300 km、carrier/supercarrier 以外の ship と Upwell structure の 490 km 上限を説明する（2026-04-30 更新ページの §§Targeting time / Targeting range）。
- クライアントの countdown 計算と属性変更時の進捗保持は `eve/client/script/ui/inflight/bracketMgr.py:1118-1143`、`bracket.py:1117-1124`、`inSpaceBracket.py:435-443`。式は Rust millisecond 表現で `40,000,000 / (scanResolution × asinh(signatureRadius)^2)`。EveJS 261028.1 は `src/server/src/space/runtime/scene/targeting.js:1140-1322` で上限超過時に newest pending lock を先に外し、scan-resolution / signature-radius の変更で残り時間の進捗率を保って deadline を再計算し、range/warp/target loss を即時再検証する。
- Pinned SDE は build 3396210（`sde/lock.json`）。eve_p3 のその SDE において category 65 は Structure、group 547 は Carrier、659 は Supercarrier。Rust はこれらの SDE ID から source hull の例外を判定する。EveJS とクライアントのローカル range check は属性値を使うだけなので、EVEU の上限を権威ある sim check に適用する差分は `docs/reference/compat/DEVIATIONS.md` に記録した。
- `ShipSpec` を受け取る sim dispatcher は `crates/eve-space/src/sim/dispatch.rs:981-982` から `Scene::respec` に渡す。skill 更新で Host の ship snapshot だけが変わり sim が古い値を保持していたため、`CharacterDogmaSync` から `SpaceSessions.RefreshShipAsync` を属性通知前に呼ぶ。Host 接続は `SpaceComposition.cs:221`。

## 修正と回帰テスト

1. `targeting/lock.rs:3-18`: range check に SDE hull/category ごとの hard cap を適用。range refusal は通常の target-range error のまま返す。
2. `targeting/mod.rs:163-181`、`targeting/settle.rs:3-41`: target の signature または source scan resolution が変わったとき、過去の進捗率を維持して pending deadline を再計算。
3. `scene/session.rs:652-660,730-738`: ShipSpec 更新を targeting attribute change と照合し、pending lock を rebase、縮んだ target cap では newest pending を先に cancel し、次に newest held lock を解除、その後 pending/held lock の range と warp を再検証。
4. `ewar.rs:590-595`: EWAR の scan-resolution / signature / range 更新後に pending locks を再計算し、held/pending targets の失効を即時処理。
5. `death.rs:94-96`: ship destruction の `lost(..., "Exploding")` / `otherlost` を `Destroyed` より先に出す。
6. `CharacterDogmaSync.cs:22-23,122-137,158-173` と `SpaceComposition.cs:221`: trained skill / expert grant が ship dogma を変えたら sim の ShipSpec を先に refresh し、その後 client attribute notification を送る。

sim 回帰テストは `targeting/tests.rs:158-323,374-399,522-663,839-861,899-950`。lock formula と default/floor/ceiling、即時完了、scan damp と painter、ShipSpec scan/signature 更新、surface distance と exact range edge、ship/structure/carrier caps、target cap の newest-drop 順、変更後の range 失効、Exploding 通知順、warp/leave/cancel を直接 assertion する。`modules/tests.rs` の MWD regression は signature radius の増加が incoming pending lock を retime する。Dogma PgFact (`DogmaServiceTests.cs:364-389`) は skill と expert grant の両方で refresh callback が attribute event より先に呼ばれることを検証する。Host wire error tests は `TargetingTests.cs:121-159`。Space Host order/refusal/concurrency tests は `SpaceServiceTests.cs:1246-1361`。

実施した mutation check: Scene respec の lock-rebase 呼び出しを外すと ShipSpec scan/signature regression が落ち、EWAR の settle 呼び出しを外すと damp による held-target range test が落ちた。hard cap を外すと `targeting_range_caps_apply_to_ships_and_structures_but_not_carrier_hulls`、lock 時間式の係数を変えると式テスト、respec の target-limit enforcement を外すと newest-drop 順テスト、`Destroyed` を target-loss 通知の前へ移すと Exploding 順テストがそれぞれ失敗した。各変更を復元して同じテストが成功することを確認した。

### SafeLogoff と target-loss 状態

V24.01 全 `TryLockTarget` 呼び出し元は前掲の `targetMgr.py` に集約する。`targetMgr.py:1362-1381` はローカル lock countdown を始め、`AddTarget` RPC の `UserError` でその試行を戻す。`gameui.py:975-979` は `OnSafeLogoffFailed` を受け取ると safe-logoff countdown 自体を停止して理由を表示する。EVE University [Timers](https://wiki.eveuniversity.org/Timers) の safe logoff 条件は、ロック中・ロックされている最中・ロックを保持中なら中断されること。EveJS 261028.1 `server/src/space/runtime/scene/targeting.js:1324-1394` は lock を受理する前に対象と距離を検証し、受理後に pending lock を記録して safe-logoff runtime を呼ぶ。`server/src/services/ship/safeLogoffRuntime.js:199-203` は開始元と対象 pilot の双方を該当理由で中断し、`targeting.js:606-707` は完了時に source の `add` と target の `otheradd` を送る。

そのため Host は `OrderAsync` (`SpaceSessions.Modules.cs:40-102`) が sim から受理を得た後だけ safe logoff を中断し、accepted pending target ID も cache に即時記録する。`SpaceSessions.cs:861-905,942-943` は通知 envelope の `OnTarget` add/lost/clear を source の outgoing lock cache に反映してから同じ sim 通知を client に転送する。incoming `otheradd` と accepted request は対象 pilot の safe logoff も中断する。`SpaceSessions.SafeLogoff.cs:31-35,100-108` は outgoing held/pending と他 pilot からの incoming held/pending lock の双方を開始条件で拒否する。destroy / disconnect / scene shutdown cleanup は `SpaceSessions.cs:991-992,1165-1168,1297-1300` で cache を破棄する。

`SpaceServiceTests.cs:333-386` は拒否された lock が走行中の safe-logoff timer を壊さないこと、add/lost/clear の状態遷移、`safe logoff` 中の accepted pending lock が両 pilot の timer を正しい client label で止め、その間は双方が再開できず、lost 後は再開できることを確認する。before-fix ではこの3件がそれぞれ refused lock による timer failure、OnTarget add の見落とし、pending target pilot の timer 継続を検出した。修正後の `eve_p3` focused run は 3/3、skip 0。

## ローカル検証と引き継ぎ状態

- merge後の .NET build: `Eve.Services.Space`、`Eve.Services.Dogma`、`Eve.Host` は成功、0 warning / 0 error。
- merge後の Rust checks: `cargo +1.94.1 fmt --all -- --check`、`cargo +1.94.1 check -p eve-space`、`cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` は成功。
- merge後の Rust tests: `targeting::tests::` 48/48、MWD signature lock-time regression 1/1、`ewar::tests::` 7/7、`others_match_reference` 5/5 が成功。
- `eve_p3` の `db-lock.sh` 経由: `SpaceServiceTests` 157/157、`DogmaServiceTests|ShipEventTests|TargetingTests` 30/30 が成功。skip 0。
- SafeLogoff の拒否、add/lost/clear、pending lock の両 pilot 側中断を追加テストで確認。SpaceServiceTests 全体に含めて実行済み。
- `18bb1d8fa4d8a9dd87725dd7c34c801bea211e71` の push-triggered CI run 1448 は Rust tests と Windows Rust workspace で `modules::tests::a_lock_the_dampened_ship_can_no_longer_reach_is_given_up` が失敗した。End to end、.NET build、Rust format/clippy は成功。Agent test run 1260 は成功。
- 上の失敗はテストの観測漏れだった。`scene.activate` が同期で返す `OnTarget` を捨て、後続 poll のみ検査していたため、assertion を activation と poll 両方の出力に対して維持した。
- 修正前の focused Rust test は `modules/tests.rs:1721` で失敗を再現。修正後は同 test が成功し、`modules::tests::` 全 161 件も成功。`cargo +1.94.1 fmt --all -- --check`、`cargo +1.94.1 check -p eve-space`、workspace clippy も成功。
- 修正 commit `f7c0e9e14ccd2fe97c42cdd5339cd2b6b85146ab` を main に push。push-triggered CI run 1449 は全 job success、`python3 tools/dev/ci-status.py failures f7c0e9e14ccd2fe97c42cdd5339cd2b6b85146ab` は `No failed tests or jobs`。
- 実 graphical V24.01 client session の操作確認は行っていない。
