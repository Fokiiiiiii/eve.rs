# 以前スキップされたライブシナリオ

実行日: 2026-11-16。すべて eve.rs を実ホストした headless client に対して実行した。DB は `eve_p4`、SDE は pinned build 3396210。`EVE_LIVE_SLOW=1` が必要な7シナリオでは同変数を設定し、トレーニング速度のケースには `.github/workflows/ci-nightly.yml` と同じ `EVE_SKILL_TRAINING_SPEED=60000` を設定した。各runは別の空きportを使い、`KEEP_LOGS=1` で `/tmp/cx4-live-skipped/` 以下へ出力した。

## 実行結果

| ケース | 結果 | ログ |
| --- | --- | --- |
| `test_the_skill_queue_is_what_the_reference_does[race-1]` | 成功 | `/tmp/cx4-live-skipped/skill-queue-default-races.log` (`.. [100%]`) |
| `test_the_skill_queue_is_what_the_reference_does[race-4]` | 成功 | `/tmp/cx4-live-skipped/skill-queue-default-races.log` (`.. [100%]`) |
| `test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` (`EVE_SKILL_TRAINING_SPEED=60000`) | 成功、1 passed | `/tmp/cx4-live-skipped/recheck-training-fast-sliced-runner.log` |
| `test_a_station_dock_from_beyond_the_range_is_accepted_on_arrival_without_a_second_command` | 成功 | `/tmp/cx4-live-skipped/station-dock-from-beyond-range.log` (`. [100%]`) |
| `test_a_ship_warps_to_a_planet_as_the_reference_did` | 成功、1 passed in 94.63s | `/tmp/cx4-live-skipped/recheck-warp-planet2/runner.log` |
| `test_a_ship_warps_to_a_gate_and_jumps_as_the_reference_did` | 成功、1 passed in 64.38s | `/tmp/cx4-live-skipped/recheck-jump4/runner.log` |
| `test_two_ships_see_each_other_as_they_did_in_the_reference` | 成功、1 passed in 122.78s | `/tmp/cx4-live-skipped/others-final9/runner.log` |
| `test_a_ship_locks_others_as_it_did_in_the_reference` | 成功、1 passed in 257.16s | `/tmp/cx4-live-skipped/recheck-target/runner.log` |
| `test_a_ship_works_its_afterburner_as_it_did_in_the_reference` | 成功 | `/tmp/cx4-live-skipped/afterburner.log` (`. [100%]`) |
| `test_a_ship_is_damaged_flown_hurt_and_mended_as_it_was_in_the_reference` | 成功、1 passed in 37.59s | `/tmp/cx4-live-skipped/recheck-health-real-runner.log` |

全10件が成功し、今回の実行でスキップされたケースはない。短い3ログはpytestのquiet出力がdotのみだが、runnerは終了コード0で終了した。

## 比較で見つけた古い期待値

- 惑星warpの最初の再実行は、fixtureの旧順 `SetMaxSpeed, WarpTo, OnSpecialFX, SetSpeedFraction` を期待して失敗した。現行EveJSの `server/src/space/destiny/commands/warpCommands.js:628-669` は同一stampの `OnSpecialFX, SetSpeedFraction, WarpTo` を組み立て、V24 `eve/client/script/remote/michelle.py:482-511` はその順で受け取ったDestiny actionを展開して `FlushState` に渡す。旧fixtureのみをこの定義に合わせて正規化し、warp action名、FX/speed payload、ship ID、stop distance、warp factor、2,500 m以内の着地点を比較するよう修正した。最終再実行は成功した。
- ゲートwarp/jumpの初回再実行は、undock通知が次のballpark bind待ち中に届く境界、fixtureと実行時の非Destiny通知順、group 99の参照装飾物と実sentri ID、warp-to-gate stop distanceの比較で失敗した。V24 `michelle.py:1098-1160` はDestiny updateをstampごとに適用し、`SetState` (`:968-1005`) はballpark全体を状態bufferとslimから再構築する。テストはundockとbindのDestiny action列をまとめ、通知系ごとに比較するようにした。action順、stamp group、wait flag、payload、session遷移値は引き続き検証する。
- jumpの通知順も実行結果は `DoDestinyUpdate` の後に `OnJumpQueueUpdate` だった。EveJS `server/src/space/transitions.js:3836-3871` はjump-outを開始してからjump queue通知を送る。fixtureの逆順を要求せず、V24の別々の処理streamごとに順序とpayloadを検証する。これはサーバー不具合ではなく古いcaptureのevent順期待だった。
- fixtureのgroup 99 sceneryとEVE-RSのgroup 99 sentryはitem IDが異なる。互換記録 `docs/reference/compat/DEVIATIONS.md:369-371` の差分に従い、このjump scenarioではgroup/category/typeの件数を比較し、他のslimと状態を比較する。SDE identityおよびAddBalls2とSetStateの一致はundock/sentryテスト側が担う。
- EveJSのfixtureはjump後の船の `SetBallAgility` / `SetBallMass` / `SetMaxSpeed` を別actionで送る。EVE-RSのjump時 `SetState` bufferにもfree-ballのmass、max velocity、inertiaがある。テストは `tools/headless-client/evecli/describe.py` のstate decoderでfixtureと実行時の値を比較する。V24は `SetState` でbufferを読み直しslimを追加するため、重複actionがないこと自体はクライアントの状態差ではない。
- jump後の `OnModuleAttributeChanges` は追加通知だが、SDE dogma attributes 1175-1177 (`heatHi`, `heatMed`, `heatLow`) を船に対して0へ戻す内容だった。EVE-RSのjump処理は `crates/eve-space/src/scene/jump.rs:374` でheatをresetする。V24 `eve/client/script/dogma/clientDogmaLocation.py:310-342` はロード済みmoduleのheat attributeを更新するため、テストは通知、ship ID、3属性、値、時刻を確認する。
- Gate warpのfixtureとEVE-RSは停止距離の表現が異なる。EveJS `server/src/space/destiny/simulation/warpTargets.js:200-246` とEVE-RS `crates/eve-space/src/gate.rs:70-105` は同じゲート周辺の着地点を異なるstop distanceとpointで表現する。テストはgate用にstop distanceの完全一致を要求せず、packet shape、warp factor、0〜2,500 mのstop範囲、warp dropout後に同じgateへジャンプできることを検証する。惑星warpの比較はstop distanceを引き続き完全一致で検証する。

## 未確認

- グラフィカルV24.01クライアントでの操作確認はしていない。今回の成功はheadless clientによる実Host end-to-end確認。
- EveJSサーバーを今回の全10ケースで再起動して同一シナリオを再実行する比較は未完了。EveJS実装ソース、既存のEveJS capture fixture、V24.01 client callsiteを照合した。targetのEveJS直接再生は別の試行で300秒timeoutとなり、pass/failを得ていない。
