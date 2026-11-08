# Sentry pending Crimewatch event handoff

## 実装状態

- Sceneのsecurity-status loss／criminal-flag pending列をそれぞれ256件に制限し、古いflag状態・期限切れ・シーン退出で破棄します。購読者がいない間は同じ上限付きキューへ戻し、`Scene::poll` と `SceneWorld::tell_what_happened` の両方で期限を確認します（`crates/eve-space/src/scene/mod.rs:13, 97-101, 958-1012`、`crates/eve-space/src/brains/world.rs:50-57, 165-197`、`crates/eve-space/src/scene/session.rs:749-753`）。
- Hostの既存Crimewatch状態から `FlagAt(now)` と `FlagUntilMs` を使ってflag種別と実期限をSimへ渡します。SimLink kind 140をprotocol 92に更新し、Unix ms期限をpilot状態へ保存してFILETIMEへ変換します。固定の15分値はscene側へ重複実装していません（`dotnet/src/Eve.Crimewatch/CombatState.cs:24, 45, 61-65`、`dotnet/src/Eve.Services.Space/SpaceSessions.Modules.cs:533, 570-581`）。
- 通常退出と破壊処理の両方で、そのshipのpendingイベントを削除します（`crates/eve-space/src/scene/docking.rs:149`、`crates/eve-space/src/death.rs:94`）。
- module tasklet内のcycle乱数は `t.world().random_int(...).ok()` を使い、親Task状態へエラーを伝播する `TaskCtx::random_int` を呼びません（`crates/eve-behaviors/src/actions/combatmodules.rs:22-29, 64-68`）。

## クライアントと参照実装の根拠

- V24.01 `behaviors/monitors/combat.py:368-373` は `MESSAGE_SEC_STATUS_LOST_EVENT` を購読します。`OnSecStatusLost` は `combat.py:385-406` でballpark、同一bubble、距離を確認してから処理し、`AddBallAsTargetAndResetTree` は `combat.py:411-422` で既存targetを重複登録しません。全client sourceの検索では `CRIMINAL_FLAG_ACTIVE` と `CRIMEWATCH_FLAG_ACTIVE` の直接consumerは見つかっていません。client向けstatus-loss引数（ownerID, shipID）は変更していません。
- 現行EveJS `/home/user/evejs-261028.1/src/server/src/space/npc/npcBehaviorLoop.js:5767-5797` の `noteIncomingAggression` はattackerをpreferred targetへ即時設定します。この経路に遅延購読用のpending queueはなく、同じ遅延購読シナリオを実行する対応経路はありません。eve.rsに元からあるqueueの保持期間をlive Crimewatch状態に制限し、client wire shapeは変えていません。
- EVE University Wikiはcriminal timerが15分であることを記載しています（[GCC](https://wiki.eveuniversity.org/GCC)、[Security status](https://wiki.eveuniversity.org/Security_status)）。実装ではHostの現在状態と期限を使います。

## 確認済み

- `cargo +1.94.1 fmt --all -- --check`、`cargo +1.94.1 check -p eve-space -p eve-behaviors`、workspace全target Clippy: 成功。
- `brains::world_tests`: 19 passed。security-status、active-criminal、no-range tasklet、pilot exit/destruction、SimLink fixtureの追加focused実行も成功。SimLink fixture integration: 13 passed。
- `Eve.Services.Space.Tests.SpaceServiceTests`: `eve_p6` にpinned SDE build 3396210をロード後、162 passed / 0 failed / 0 skipped。初回はSDE未投入かつ並列接続上限で失敗したため、SDEを投入し低並列で再実行しました。
- `Eve.Space.Tests.SimCodecTests`: stale expected kind名を更新後、477 passed / 0 failed / 0 skipped。
- コードcommit `f54cdf2a` のCI run 1539（`42086919673`）はfailure。Rust workspaceの2件をローカル再現し、`an_undock_puts_the_ship_in_space_and_binding_sends_it_the_scene_as_marshalled_notifications` から空のDoDestinyUpdateを期待する1項目を除き、`tether_host_policy_dispatches_to_the_real_scene_and_transitions_return_to_the_host` の期待protocolを92へ更新しました。該当2 focused tests、fmt、`eve-space` check、workspace Clippyは成功。選択agent-test run 1343（`42086952755`）とCIのend-to-end、他の完了shardはsuccess。Rust fix was included in commit `15e30ac7`; push-triggered main CI run 1563 for `ae916d91` passed all jobs, and selected agent-test run 1361 also passed.

## Review follow-up (2026-11-06)

- The Haiku finding is already fixed on main in `15e30ac7` (current `ae916d91`); no further source edit was needed. The queues are capped at 256 in `crates/eve-space/src/scene/mod.rs:12-13,96-100`, pruned at poll and before brain notifications (`scene/session.rs:751`, `brains/world.rs:50-52`), retained only while the Host-supplied Crimewatch expiry is live (`scene/mod.rs:990-1013`), and cleared when a ship leaves or is destroyed (`scene/docking.rs:149`, `death.rs:94`). Regression coverage includes bounded queues, expired/live late subscription, leaving and destruction (`brains/world_tests.rs:253-337`).
- The tasklet calls the raw `World::random_int` result and converts it with `.ok()` (`crates/eve-behaviors/src/actions/combatmodules.rs:21-29,62-70`); its `on_call` path does not call `TaskCtx::random_int`.
- Full V24.01 source search found the direct `MESSAGE_SEC_STATUS_LOST_EVENT` consumer in `/home/user/eve-logic/behaviors/monitors/combat.py:368-406`: it subscribes by solar system, accepts `(ownerID, shipID)`, and checks ballpark, bubble, and optional distance before targeting. Target deduplication is at `combat.py:410-419`. No direct `CRIMINAL_FLAG_ACTIVE` consumer was found.
- Local verification: Rust world tests 19 passed, combat-module tests 6 passed, SimLink fixture tests 13 passed; Rust format, package check and workspace Clippy passed. The p3 DB-backed criminal/suspect Host tests passed 2/2 with 0 skipped; the .NET valid-frame roundtrip test passed 298 cases with 0 skipped.
- GitHub Actions on `ae916d91`: main CI run 1563 and selected agent-test run 1361 passed. Graphical V24.01 client verification was not performed.

## 未実施の検証

- 実際のV24.01 graphical clientでのlogin／sentry combat walkは行っていません。
- EveJSはソース経路を確認しましたが、遅延購読queueに相当する実行シナリオがないため実行テストはありません。
