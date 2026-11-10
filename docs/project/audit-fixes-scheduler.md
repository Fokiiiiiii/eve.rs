# Scheduler audit fixes: wormhole slim state (2026-11-10)

## 判定

監査報告の P2「slim-state 配信に失敗しても期限ジョブが消費される」は実在しました。修正前の `WormholeJobs.UpdateSlimStateAsync` は、配信を `AfterCommit` callback に登録していました。Scheduler は one-shot job の削除を commit してから callback を呼び、callback 例外はログに記録して処理を続けます。さらに旧 Space callback もシミュレーション送信の例外を捕捉していました。そのため一時的な送信失敗は job の retry になりませんでした。

監査報告に記載された EveJS の generic scheduler retry は、wormhole age 配信の直接の経路とは確認できませんでした。同報告の参照は `evejs-261028.1` ですが、この作業で指定された `evejs-261108.1` では、`wormholeAge` と `wormholeSize` を各 scene tick で再計算し、既存 slim entity と値が違う場合に `broadcastSlimItemChanges` します（`server/src/services/exploration/wormholes/wormholeRuntime.js:603-624,693-694,1334-1450,1647-1657`、`server/src/space/runtime/scene/tick.js:503-510`）。Scheduler の generic retry 自体は存在しますが、wormhole の表示値を届ける経路との比較根拠には使っていません。監査報告の P2 判定は eve.rs の実経路とクライアント参照から独立して確認しました。

## 実装

- `WormholeJobs.UpdateSlimStateAsync` は durable handler の実行中に `SlimStateSyncRequested` を呼びます。例外を捕捉しないため、配信失敗は Scheduler の既存 retry/backoff 経路に戻ります。
- Host の `SpaceSessions.BindWormholes` は通常の wormhole 通知と retryable slim-state 配信を同じ `WormholeWorld` に結び、Host composition から呼ばれます（`dotnet/src/Eve.Services.Space/SpaceWormholes.cs:70-84`、`dotnet/src/Eve.Host/Composition/SpaceComposition.cs:294`）。配信値は `EndsForConnectionAsync` の authoritative state から読み、両端に `wormholeAge` と `wormholeSize` を送ります（`SpaceWormholes.cs:135-143`）。
- Transit 完了後の `StateChanged` callback は引き続き post-commit です。すでに commit された jump を slim update の失敗で retry したり取り消したりしないためです。

## クライアント動作の根拠

V24.01 は `infoContentBase.py:1519-1536` で `slimItem.wormholeAge` と `wormholeSize` から情報ウィンドウ表示を選びます。グラフィック controller も `clientControllerRegistry.py:153-165` で両値を `WH_Age` / `WH_Size` に反映し、wormhole space object は `wormhole.py:42-45` の `OnSlimItemUpdated` から controller を更新します。したがって配信漏れは情報表示と wormhole model の両方を古い状態に残し得ます。

EveJS 261108.1 は `expiresAtMs` 等の authoritative pair state から age/size を算出し、scene tick ごとの差分を slim update として送ります。eve.rs は durable job の失敗時再送で同じ client-visible state に追いつく実装にしています。値の算出は保存済み wormhole state に基づき、packet や age bucket を固定値にしていません。

## 検証

- 回帰テストを先に追加し、PostgreSQL と pinned SDE を準備した状態で修正前の実装を実行しました。シミュレーション送信を一度失敗させた後、Scheduler job がすでに消えており `Assert.NotNull(pending)` が失敗しました。
- 修正後の `A_failed_age_bucket_slim_update_retries_after_a_scheduler_restart` は失敗を注入し、job が `pending` / attempts `1` になること、Scheduler を作り直して retry した後に job が消えること、両端へ authoritative age/size の `SimSetSlimField` が送られることを検証します。
- Release build（`-warnaserror`）は `Eve.Wormholes`、`Eve.Services.Space`、`Eve.Host`、`Eve.Services.Space.Tests`、`Eve.Wormholes.Tests`、`Eve.Host.Tests`、`Eve.Scheduler.Tests` がすべて 0 warnings / 0 errors でした。
- DB-backed / 関連クラスの結果はすべて `eve_p11` 上で Pass、Skip なしです。Services.Space 224 件、Wormholes 15 件、Scheduler 18 件、Host wormhole wire 4 件。
- eve.rs テストは `FakeSim` で送信失敗と再送を確認しています。Node から EveJS runtime を直接ロードする試行は、参照 checkout に `picocolors` がなく失敗したため、EveJS の実行時比較は未確認です。

## 未確認

- graphical V24.01 client と稼働中 Host / simulation IPC での表示更新は実行していません。V24.01 の callsite と controller の静的追跡、および eve.rs の scheduler-to-simulation integration test までを確認済みです。
- EveJS 261108.1 の実行時比較は `picocolors` 不足で未実施です。上記の EveJS 動作は参照コードの静的確認です。
