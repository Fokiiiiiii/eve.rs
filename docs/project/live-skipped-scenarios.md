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
- この時点ではEveJSのfresh fixtureによる全10ケースの自己比較は未完了だった。現行fixtureでwarpとmoduleを再確認した結果は、後段「直接EveJSでの追試」に記録する。targetの過去の直接再生は300秒timeoutとなり、pass/failを得ていない。

## 2026-11-16 最終再実行

上の実行記録後、nightly相当の環境で10件を再実行した。`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`、pinned SDE `.cache/sde/eve-online-static-data-3396210-jsonl`、`EVE_MARKET_SEED=skip`、各run固有の空きfront/gateway/SSO portを使い、出力を `/tmp/cx4-live-skipped-final/` 以下に保持した。`.github/workflows/live-slow.yml:132,157-159,191-192` に合わせ、slow casesには `EVE_LIVE_SLOW=1` と既定station `60015249` を設定した。二つのrace testはnightlyの既定 `EVE_SKILL_TRAINING_SPEED=1.0`（`CharacterComposition.cs:51`）で実行した。completion testだけは `.github/workflows/ci-nightly.yml:69-74` と同じ `EVE_SKILL_TRAINING_SPEED=60000` と `EVE_LIVE_SLOW=1` を設定した。

| ケース | 最終結果 | ログ |
| --- | --- | --- |
| `test_the_skill_queue_is_what_the_reference_does[race-1]` | 成功 | `/tmp/cx4-live-skipped-final/training-reference-rate/run.log` (`.. [100%]`) |
| `test_the_skill_queue_is_what_the_reference_does[race-4]` | 成功 | `/tmp/cx4-live-skipped-final/training-reference-rate/run.log` (`.. [100%]`) |
| `test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` | 成功、1 passed | `/tmp/cx4-live-skipped-final/training-speed-60000/run.log` |
| `test_a_station_dock_from_beyond_the_range_is_accepted_on_arrival_without_a_second_command` | 成功 | `/tmp/cx4-live-skipped-final/movement-dock-arrival/run.log` |
| `test_a_ship_warps_to_a_planet_as_the_reference_did` | 成功 | `/tmp/cx4-live-skipped-final/warp-planet-transitions/run.log` |
| `test_a_ship_warps_to_a_gate_and_jumps_as_the_reference_did` | 成功 | `/tmp/cx4-live-skipped-final/jump-gate/run.log` |
| `test_two_ships_see_each_other_as_they_did_in_the_reference` | 成功 | `/tmp/cx4-live-skipped-final/others-see-each-other/run.log` |
| `test_a_ship_locks_others_as_it_did_in_the_reference` | 成功 | `/tmp/cx4-live-skipped-final/targeting-lock/run.log` |
| `test_a_ship_works_its_afterburner_as_it_did_in_the_reference` | 成功、1 passed in 75.86s | `/tmp/cx4-live-skipped-final/module-afterburner-client-wait/run.log` |
| `test_a_ship_is_damaged_flown_hurt_and_mended_as_it_was_in_the_reference` | 成功 | `/tmp/cx4-live-skipped-final/health-damage-repair/run.log` |

この10件では最終試行の失敗・skipはない。quiet出力のケースはログにpytestの`.`と`[100%]`があり、runnerの終了codeも0。最初のwarp試行はpytest collection時に編集中のtest fileで `IndentationError` となり、scenario自体は起動していなかった。修正後の専用runと最終runは成功した。

### warp比較の訂正

planet warpは途中の比較器失敗を調べた。V24 `eve/client/script/remote/michelle.py:482-511` はballparkがbindされる前のowner Destiny updateを捨て、`SetState` (`:968-1005`) からballparkを作る。比較器側は古い`AddBalls2`だけから参照propsを拾っていたため、初回`SetState`に含まれるEveJS側のprop IDsを分類できていなかった。また、EveJS fixtureの10個のstation/customs-office ballとeve.rsの8個のscene ballを同じID列と比較していた。最終比較では各側のwarp前`SetState`からball IDsをdecodeし、その側に元からあったballだけを一つの`RemoveBalls` updateで除去したこと、wait flag、受信時刻を確認する。参照propsの異なるリスト内容は互いに同一とは要求しない。`docs/reference/compat/DEVIATIONS.md:365` の差分#21と同じballpark差で、wireのaction名、Destiny stamp、通知時刻と有効ID集合の検査は残した。

WarpTo landing pointをfixtureと実行時の両方で独立に乱数化するEveJS同士の比較では、片方の距離半径2,500mだけでなく二点間の最大距離5,000mが上限になる。EveJS `server/src/space/destiny/simulation/warpJitter.js:3-8,18-35` がradius 2,500mを定義するため、テストは5,000m以内を許し、stop distance、warp factor、ship IDは引き続き完全一致で検査する。V24 `environment/effects/Warp.py:455-527` はballがWARP modeを抜けるまでwarp effectを処理する。eve.rsはdropout後も停止時のposition/velocity/zero speed/`Stop`を送るので、比較器はそれらを共通action比較から外す前に個別検証し、次のwarp要求の応答も確認した。

初回module再実行はraw `OnTarget` を待つpredicateがV24の`OnMultiEvent` wrapperを見ず、8秒のwatchを使い切ってcap通知の範囲まで取り込んだ。V24 `godma.py:208-288` のgroup/filter規則でclient-visible eventを復元してからcompletionを待つようにし、同じafterburnerケースを再実行して成功した。

現行EveJSとの自己比較では、最初にactor stream内のattributeID 18を船item IDで区別せず値順に比べたため、149.5 GJを148.38829 GJの期待値と比べて失敗した。V24 `godma.py:246-252` はOnModuleAttributeChangeを `(ownerID, itemID, attributeID)` ごとに集約し、`clientDogmaLocation.py:310-342` は行のitem IDへ適用する。比較器もactor shipのmapped item IDに限定し、EveJS self-comparisonとeve.rsの同じafterburner testを再実行して両方passした。追加のeve.rs実行は `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`、`EVE_LIVE_SLOW=1`、`EVE_START_STATION=60015249` で1 passed in 80.04s。`clientDogmaLocation.py:310-342` の適用とEveJS `server/src/space/runtime/dogmaEvents.js:86-103,133-135` の通知生成は [ballpark-bind-wire-audit.md](ballpark-bind-wire-audit.md) に記録した。

### 直接EveJSでの追試

現行EveJSをpinned SDEで起動してwarpとmodule scenarioを一時fixtureへ記録した。記録は `/tmp/cx4-evejs-current-recording/warp.json` と `module.json`、server logは `/tmp/cx4-evejs-current-recording/recorder.log`。歴史的fixtureへの直接再生ではwarp着地点が3,473.9m異なり、150秒内に`Stop`が観測されなかった。またmoduleのcurrent outputは歴史的captureとevent order/time/capacitor cadenceが異なった。warp jitter半径2,500mの独立した二点の距離上限は5,000mなので、この着地点差は有効範囲内。現在fixtureを使った自己比較は、warp 1 passed（`/tmp/cx4-evejs-selfcheck/logs/evejs-server.out`）とmodule 1 passed（`/tmp/cx4-evejs-selfcheck/module-logs/evejs-server.out`）。最初のEveJS起動はNode既定heap 2GBでSDE読込中にOOMとなり、`run-evejs-live.sh` が案内する `EVEJS_NODE_OPTIONS=--max-old-space-size=8192` で再試行した。

### 未確認

- グラフィカルV24.01クライアントを起動した操作確認はしていない。結果は実Hostに対するheadless clientのwire/E2E検査。
- EveJS現行captureを使った直接実行はwarpとmoduleのみ。残り8ケースはV24 client callsite、EveJS source/capture、eve.rs live resultで照合したが、EveJSに同じ10シナリオを再生していない。

## 2026-11-17 nightly相当の再実行

`eve_p4` 上で再実行した。SDE build 3396210 と mission build 3405148 はロード済みだった。全runで `EVE_MARKET_SEED=skip`、`KEEP_LOGS=1`、空いているportを使い、runnerの終了を待ってからログを確認した。7つのslow scenarioは `.github/workflows/live-slow.yml:139-142,188-197` と同じくscenario fileごとに別Hostで実行し、`EVE_LIVE_SLOW=1` と既定station `60015249` を設定した。race testも既定training speed（`EVE_SKILL_TRAINING_SPEED` unset、`CharacterComposition.cs:51` の既定値 `1`）のfresh Hostで実行した。fast-training testは `.github/workflows/ci-nightly.yml:69-74,109-117` と同じ `EVE_SKILL_TRAINING_SPEED=60000`、`EVE_LIVE_SLOW=1` の専用Hostで実行した。

| ケース | nightly相当runの結果 | ログ |
| --- | --- | --- |
| `test_the_skill_queue_is_what_the_reference_does[race-1]` | 成功 | `/tmp/cx4-live-skipped-rerun-2026-11-17/training-races.log` (`2 passed`) |
| `test_the_skill_queue_is_what_the_reference_does[race-4]` | 成功 | `/tmp/cx4-live-skipped-rerun-2026-11-17/training-races.log` (`2 passed`) |
| `test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` (`EVE_SKILL_TRAINING_SPEED=60000`) | 成功、1 passed | `/tmp/cx4-live-skipped-rerun-2026-11-17/run.log:39-45`; JUnit `/tmp/cx4-live-skipped-rerun-2026-11-17/training.xml` (1 test, 0 failures, 0 skipped) |
| `test_a_station_dock_from_beyond_the_range_is_accepted_on_arrival_without_a_second_command` | 成功、1 passed in 266.18s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:4-25` |
| `test_a_ship_warps_to_a_planet_as_the_reference_did` | 成功、1 passed in 94.61s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:26-47` |
| `test_a_ship_warps_to_a_gate_and_jumps_as_the_reference_did` | 成功、1 passed in 109.32s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:48-69` |
| `test_two_ships_see_each_other_as_they_did_in_the_reference` | 成功、1 passed in 122.90s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:70-91` |
| `test_a_ship_locks_others_as_it_did_in_the_reference` | 成功、1 passed in 257.05s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:92-113` |
| `test_a_ship_works_its_afterburner_as_it_did_in_the_reference` | 成功、1 passed in 75.97s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:114-135` |
| `test_a_ship_is_damaged_flown_hurt_and_mended_as_it_was_in_the_reference` | 成功、1 passed in 37.69s | `/tmp/cx4-live-skipped-rerun-2026-11-17/isolated-slow.log:136-157` |

最終の10ケースはすべて成功し、skipは0件。race testは独立Hostで2 passed、fast-trainingは独立Hostで1 passed、slow scenarioは各々独立Hostで7 passed。GUI版V24.01クライアントの操作確認ではなく、実Hostに接続するheadless clientのE2E確認である。

### 再実行中に見えた失敗の分類

一度、slowの9ケース（2 race parameterと7 scenario）を一つのHostへまとめて実行したところ `test_others.py` と `test_target.py` が失敗し、ballpark slimの `charID` / `ownerID` が記録からruntime IDへ写像した期待値より1大きいという比較差が出た（`/tmp/cx4-live-skipped-rerun-2026-11-17/slow-only.log`）。これはnightly相当の最終結果には数えていない。nightlyは `test_file` ごとのmatrix jobを起動し、各jobでrunnerを一度実行する（`live-slow.yml:139-142,188-197`）；一つのHostに複数scenario fileを連結したrunはその隔離条件と異なる。各fileを別Hostで再実行すると、`test_others.py` と `test_target.py` は含め全7ケースが成功した。

この差は最終的に「nightly相当のHost分離を欠いた調査runの環境差」と分類した。テスト期待値を緩和したり、server codeを変更したりしていない。wire上のidentity field自体はclient-visibleであり、V24 `eve/client/script/remote/michelle.py:527-534` はshipの `charID` を返し、`:1208-1216` は `ownerID` / `charID` をowner情報のprimingに使う。EveJSも `server/src/space/destiny/index.js:390-394,541-547` で `ownerID` と `entity.characterID` をslimへ出す。実行時IDの対応はscenario helper `tools/headless-client/tests/test_undock.py:566-622` が各runで作るcharacterへ写像する。複数fileを同じHostに積んだときだけ出た比較差であり、nightly相当のfresh Hostで同じclient-visible assertionが通ることを確認した。共有Hostでの異なるscenario間のstate干渉そのものは、このjobのnightly条件外なので別途は検証していない。

最初の試行ではslow `EVE_RUN` のtest pathをrepository root基準ではなく `tests/...` とし、pytest collectionが `file or directory not found` で終了した。シナリオは一件も収集・実行されていない。その後CIと同じroot基準の `tools/headless-client/tests/...` へ直して上記の実行を行い、このcollection失敗は最終ケースの失敗数には含めていない。

この再実行ではEveJS serverを起動した直接再生はしていない。成功した結果は既存のEveJS fixtureとのwire比較を含むheadless testの結果である。

## 2026-11-17 origin/main merge後の再確認

`origin/main` の `bd43206d3e54` をmergeした作業treeで10件を再確認した。各caseはnightlyと同様に独立したHostで実行し、`EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p4`、pinned SDE build 3396210、mission build 3405148、`EVE_MARKET_SEED=skip` と個別の空きportを使った。二つのraceは `EVE_SKILL_TRAINING_SPEED` をunsetにして実行した。fast-trainingは `.github/workflows/ci-nightly.yml:69-74,109-117` と同じくspeed `60000`、`EVE_LIVE_SLOW=1`。7つのslow scenarioは `.github/workflows/live-slow.yml:188-197` と同じく `EVE_LIVE_SLOW=1`、station `60015249` で実行した。出力は `/tmp/cx4-live-skipped-postmerge-2026-11-17b/` に保存した。

| ケース | 結果 | 実行ログ |
| --- | --- | --- |
| `test_the_skill_queue_is_what_the_reference_does` (`race-1`, `race-4`) | 成功、2 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/training-races/runner.log` |
| `test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level` (`EVE_SKILL_TRAINING_SPEED=60000`) | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/fast-training/runner.log` と `training.xml` |
| `test_a_station_dock_from_beyond_the_range_is_accepted_on_arrival_without_a_second_command` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/movement-dock/runner.log` |
| `test_a_ship_warps_to_a_planet_as_the_reference_did` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/warp-planet/runner.log` |
| `test_a_ship_warps_to_a_gate_and_jumps_as_the_reference_did` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/gate-jump/runner.log` |
| `test_two_ships_see_each_other_as_they_did_in_the_reference` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/two-ships/runner.log` |
| `test_a_ship_locks_others_as_it_did_in_the_reference` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/target-lock/runner.log` |
| `test_a_ship_works_its_afterburner_as_it_did_in_the_reference` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/afterburner/runner.log` |
| `test_a_ship_is_damaged_flown_hurt_and_mended_as_it_was_in_the_reference` | 成功、1 passed | `/tmp/cx4-live-skipped-postmerge-2026-11-17b/health-repair/runner.log` |

全10ケースが成功し、今回のrunでskipは0件。slowのrunnerはnightlyと同じquiet形式のためログにpytestのdotと`[100%]`が残り、各runnerの終了codeは0。speed testのJUnit XMLも1 test・0 failure・0 skipped。実Hostに対するheadless clientの検証であり、GUI版V24.01クライアントの操作は未確認。EveJS serverの直接再生もこの再確認では行っていない。
