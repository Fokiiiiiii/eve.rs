# ライブシナリオのトリアージ (2026-11-16)

## 実行結果

作業開始時にバックグラウンドで動作していた `tools/dev/run-eve-live.sh` の実行を最後まで追跡した。実行環境は `eve_p3`、コードは `96c0d04c0f36c0b4345d53f100bb15e2eafcbef9`、ログは `/tmp/cx3-live-logs/complete3.log` と `/tmp/cx3-live-logs/complete3/`。Host はSDE 3396210、mission data 3405148を読み、Rust simulation も起動して Host に接続した。native market seed は `EVE_MARKET_SEED=skip` だった。

`run-eve-live.sh` が選ぶ16モジュールを実行し、pytest の進捗記号は合計129件（`.` 119件、`s` 10件、`F` 0件、`E` 0件）。失敗サマリーはなく、全テストの進捗が `[100%]` まで到達した。バックグラウンド実行元は pytest の終了コードを別ファイルへ保存していなかったため、終了コード自体は回収できていない。ここではログに残るpytestの結果記号を検証結果として記録する。

実行対象は `test_live.py`, `test_login_to_character.py`, `test_trace_replay.py`, `test_paperdoll_selection.py`, `test_sso_login_live.py`, `test_undock.py`, `test_movement.py`, `test_warp.py`, `test_jump.py`, `test_others.py`, `test_target.py`, `test_module.py`, `test_health.py`, `test_market.py`, `test_structures.py`, `test_skinr_live.py`（`tools/dev/run-eve-live.sh:177-179`）。

## 失敗の分類

### 1. V24.01クライアントが遭遇するゲーム動作の不具合

完了したライブ実行では該当なし。Host レベルの追加回帰テストやサーバー修正は行っていない。

先行作業で報告された「およそ12個の F」については、テスト名・assertion・tracebackを含むログが残っていなかった。利用できた `/tmp/cx3-live-logs/` の過去ログは下記の起動失敗で、失敗したpytestケースの記録ではない。完了した実行では F は再現しなかったため、先行報告の各Fをゲーム動作の不具合と判定する根拠はない。

### 2. タイミングまたはテストデータ依存の失敗

完了したライブ実行では、タイミング依存または古い期待値によるpytest失敗はなかった。assertionの緩和やテスト変更は行っていない。

以前の起動ログ `complete.log` では、テスト開始前のmarket seedがESIのregion 10000002 / page 117 / row 510で不正値を受け取り、Hostが停止した。例外は `MarketSeedV4.ParseOrders`（`dotnet/src/Eve.Market/MarketCatalog.cs:406-408`）から発生しており、pytestの期待値失敗ではない。これは外部ESIデータに依存する起動時データ問題として3にも記録する。成功した実行は `EVE_MARKET_SEED=skip` でこの依存を避けている。

### 3. ライブ実行環境・起動時に不足していたもの

次の失敗はすべてテストケース開始前のものであり、ゲームRPCの結果ではない。

| 記録 | 観測した失敗 | 判定・必要な環境 |
|---|---|---|
| `/tmp/eve-live-triage/startup-collision.log` | gateway の `127.0.0.1:26101` が既に使用中でrunnerが終了 | 既存listenerとポートが競合した。runnerの仕様どおり、空いている `EVE_LIVE_PORT` / `EVE_PUBLIC_GATEWAY_PORT` / `EVE_SSO_TEST_PORT` が必要（`tools/dev/run-eve-live.sh:11-12,52-60`）。 |
| `/tmp/eve-live-triage/run.log` | 別ポートでの起動ログはbuild開始行だけで終わり、pytestの進捗も終了状態も記録されていない | 完了したテスト実行には数えない。この起動失敗の原因と終了状態は特定できなかった。 |
| `/tmp/cx3-live-logs/run2.log`, `host.out` | Host起動時に `42P01: relation "sde.mapSolarSystems" does not exist`。呼び出し元は `ReadEssQuadrantsAsync`（`dotnet/src/Eve.Host/Composition/EssComposition.cs:97-104`） | PostgreSQLのbase SDE schemaに `sde.mapSolarSystems` がなかった。mission用 `sde_mission` のロードだけではこのHost queryを満たさない。CIのbootstrapはbase `eve-sde ensure --pg` と `ensure --missions --pg` の両方を実行する（`.github/workflows/ci-nightly.yml:100-103`）。成功した実行後に確認したDBには `sde.mapSolarSystems` が存在した。 |
| `/tmp/cx3-live-logs/complete.log`, `host.out` | native market seed中に上記ESIの不正order値を検出しHostが停止。pytest未開始 | 動的なESI seedを使わないシナリオ実行では `EVE_MARKET_SEED=skip` が必要。成功実行はこの設定で起動した。開発用check-allも同設定を使う（`tools/dev/check-all.sh:14`）。 |
| `/tmp/cx3-live-logs/complete2.log` | `.venv/bin/python: No such file or directory` | Python headless-clientの仮想環境がなかった。`requirements-dev.txt` を入れた `.venv` が必要（runnerの起動箇所 `tools/dev/run-eve-live.sh:177-179`）。 |

最終実行ではHost、`eve-net`、`eve-sim`、Python headless clientがすべて起動した。simulation欠落による失敗はなかった。テストはPython `evecli.client.Client` を使う（`tools/headless-client/tests/test_live.py:19-28`）ため、GUI版V24.01クライアント実行は未確認。

完了ログでskipされた10件はpassに数えない。

- `test_login_to_character.py::test_the_skill_queue_is_what_the_reference_does` の2 race cases（`tools/headless-client/tests/test_login_to_character.py:1163-1171`）: 実測skill speedがrecorded value `1.0` と一致しないためskip。Hostの既定値は `EVE_SKILL_TRAINING_SPEED=1`（`dotnet/src/Eve.Host/Composition/CharacterComposition.cs:51`）だが、実行時のspeed値はログに残っておらず原因は未確定。
- `test_login_to_character.py::test_a_queue_that_finishes_while_the_client_watches_is_told_level_by_level`（line 1224）: recorded speed `60000.0` が設定されていないためskip。専用のnightly jobは `EVE_SKILL_TRAINING_SPEED=60000` と `EVE_LIVE_SLOW=1` を指定する（`.github/workflows/ci-nightly.yml:69-73,109-117`）。
- `test_movement.py::test_a_station_dock_from_beyond_the_range_is_accepted_on_arrival_without_a_second_command`（line 149）、`test_warp.py::test_a_ship_warps_to_a_planet_as_the_reference_did`（line 78）、`test_jump.py::test_a_ship_warps_to_a_gate_and_jumps_as_the_reference_did`（line 104）、`test_others.py::test_two_ships_see_each_other_as_they_did_in_the_reference`（line 84）、`test_target.py::test_a_ship_locks_others_as_it_did_in_the_reference`（line 93）、`test_module.py::test_a_ship_works_its_afterburner_as_it_did_in_the_reference`（line 66）、`test_health.py::test_a_ship_is_damaged_flown_hurt_and_mended_as_it_was_in_the_reference`（line 125）: 各シナリオが要求する時間を待つ `EVE_LIVE_SLOW=1` が未設定。

## 未確認事項

- 先行報告の約12個のFは、テスト名・assertion・例外が保存されておらず、個別の判定・V24.01 client callsiteとEveJS handlerの照合はできなかった。完了実行では再現していない。
- GUI版V24.01クライアントとEveJS実行による比較はしていない。今回、再現したゲーム動作の失敗がなかったため、特定のRPC挙動に関する差分判断は行っていない。
- 上記10 skipのシナリオは未検証であり、passとして扱わない。
