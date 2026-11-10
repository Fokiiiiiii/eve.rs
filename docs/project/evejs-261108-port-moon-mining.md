# EveJS 261108.1 port: moon mining

## 調査結果

対象は Standup Moon Drill を載せた Athanor / Tatara の手動抽出である。CCP の Moon Mining ガイドは、抽出開始後にチャンクを精錬所へ牽引し、抽出終了時に待機状態となり、ドリルで破砕すると採掘可能な小惑星ができること、抽出時間が 6～56 日であること、未破砕なら抽出終了から 3 時間後に自動破砕することを説明している。ガイドはチャンクモデルの容積境界や破砕中の 25 秒時点までは規定していない。

- CCP, [Moon Mining](https://support.eveonline.com/hc/en-us/articles/115005404229-Moon-Mining)
- CCP, [Metenox Moon Drill](https://support.eveonline.com/hc/en-us/articles/14341380413212-Metenox-Moon-Drill)

V24.01 クライアントでは `schedulingSvc.py:43-75` が抽出開始・手動破砕・キャンセルの RPC を呼び、`moonminingExtraHudBtns.py:127-197` が進行中・破砕中・完了の UI と 30 秒破砕タイマーを扱う。`MultiEffect.py:100-155` は `graphicInfo` の `phase` / `effectStarted` / `sequence` / `triggers` からフェーズ演出を再生・更新し、`structureDrillingPlatform.py:20-40` はストラクチャ slim の `closestMoonID` を Moon Mining turret set へ渡す。`fxSequencer.py:333-385` の `OnSpecialFX` は送信元、ドリル module、対象、effect GUID、開始状態と `graphicInfo` を受け取る。

Metenox は手動の抽出スケジュールとは別系統である。クライアントの `moonmining/autoMoonMining/client/auto_moon_miner_service.py:9-29` は `autoMoonMining` に `RequestMiningDetails` と `RequestMiningCycleOutput` を要求する。CCP も Metenox を時間をかけて raw moon materials を集める自動採掘施設として説明している。したがって、手動チャンクの牽引・破砕エフェクトを Metenox に流用していない。

## eve.rs に既にあった動作

- `MoonExtractions.cs:37-42, 88-162, 209-240` は手動抽出の期間、永続化、開始・到着・破砕・キャンセルの状態遷移を実装済みだった。durable scheduler とイベントトランザクションも既存の仕組みを使う。
- `SpaceMoonFields.cs:55-82` は抽出終了イベントから採掘可能な鉱石フィールドを作る。`MoonOwnerNotifications.cs:42-50, 63-80` は開始・到着・破砕・終了イベントの通知経路を持つ。
- `.NET` には Metenox の構造・サービス識別や配置制約の基盤があるが、この調査で `autoMoonMining` の RPC/cycle 処理は見つからなかった。これは今回追加していない。

## 今回追加した動作

- `SpaceMoonChunks.cs:65-108, 110-134` が開始、チャンク到着、ドリル発射、破砕、終了イベントを購読し、システム進入時に DB の抽出状態から再構成する。再構成は抽出行をロックし、永続 inventory / `space_items` を再利用するため、同時復元や再起動でチャンクが重複しない。
- `SpaceMoonChunks.cs:136-217, 286-315` は SDE の moon 座標、moon chunk type の描画属性、精錬所と Standup Moon Drill の実 item を使って scene entity と `effects.MultiEffect` を作る。抽出中は牽引位置を時刻から計算し、到着時に stasis、手動破砕時に fracture phase に移る。`StructureMoonMiningSlim.cs:11-40` と `SpaceStructures.cs:181-219` は V24 クライアントが turret set と moon を解決できるよう、ドリル module tuple と `closestMoonID` を structure slim に載せる。
- `MoonExtractions.cs:211-234, 260-271, 323-340` は手動破砕から 25 秒後にチャンクを爆発除去し、30 秒後の既存 fracture job で鉱石フィールドを作る。爆発済み時刻を `MoonExtractionStore.cs:10-31, 100-110, 190-196` と migration `dotnet/src/Eve.Structures/Migrations/0010_moon_chunk_exploded.sql` に保存するため、再起動後に爆発済みチャンクを再生成しない。
- シミュレーション wire protocol 94 に chunk upsert/remove と structure moon-mining slim 更新を追加した。破砕・自然破砕では destruction effect 3、キャンセル / オフラインでは静かな除去を送る（`SpaceMoonChunks.cs:92-105, 220-245`）。通常の `OnMoonExtractionUpdate` は既存の RPC / extraction 通知経路を維持し、視覚フェーズは V24 の `OnSpecialFX` 形に合わせた sim message から送る。
- 既存 fixture generator で wire fixture を生成し直した。実 Host registry を経由する RPC テストは `MoonExtractionStartHostTests.cs:200-222`、DB を使う空間投影・到着・破砕・爆発・再起動テストは `MoonMiningChunkTests.cs:23-160` にある。

## EveJS との差分と不採用項目

- EveJS 261108.1 の `moonMiningSpaceRuntime.js:146-205, 225-345, 361-465, 668-711` は chunk path / phase effect / spawn / movement / restore を実装する。`moonExtractionsService.js:671-690, 1073-1094, 1215-1253, 1381-1419` は発射時のフェーズ更新、25 秒爆発、抽出開始、到着・自動破砕の処理を持つ。eve.rs では同じクライアント可視動作を既存の DB・scheduler・event bus・SDE 所有者へ組み替えた。
- EveJS `moonExtractionRules.js:224-235` のチャンク shell tier 境界（14 / 28 / 42 日相当）は現時点で EveJS 実装と SDE の type/group/render data 以上の独立した TQ 根拠を確認できていない。コードは既存の抽出 volume 計算からモデルを選ぶためこのルールを機能参照として使うが、境界自体を CCP 公式仕様としては扱わない。実クライアントで各境界を通る表示は未確認。
- EveJS 261108.1 にある GM 用 extraction-time advance helper は採用しない。対象はプレイヤーの V24 RPC と phase effect でなく、管理用時刻操作だからである。
- EveJS に専用 moon mining test file / npm test script はなく、ローカルの SDE/scene 全体を使った runtime 比較はできなかった。合成 scene を使う read-only probe では `spawnChunkForExtraction` の chunk type、tractor phase、module / target と `effects.MultiEffect` の構成を確認したが、EveJS の DB 経由 end-to-end 検証ではない。

## 実行した検証

- Release build: `Eve.Space`, `Eve.Ships`, `Eve.Structures`, `Eve.Services.Space`, `Eve.Host`。各 build は 0 warning / 0 error。
- Rust: `cargo fmt --all --check`; `cargo check -p eve-space`; `cargo clippy -p eve-space -- -D warnings`; `cargo test -p eve-space moon_mining` (2 passed); `cargo test -p eve-space --test simlink_fixtures` (13 passed)。
- DB `eve_p3` を使った .NET focused class tests: `Eve.Space.Tests` 550 passed、`Eve.Structures.Tests` 25 passed、`Eve.Services.Space.Tests` 73 passed、`Eve.Ships.Tests` 9 passed、`Eve.Host.Tests` 28 passed。skip はなし。

## 未確認

- V24.01 の graphical client での実プレイ確認、および TQ server trace は未実施。
- CCP 公開資料には shell tier の正確な容積境界、破砕アニメーション開始から chunk 消去までの 25 秒値、Metenox の cycle RPC wire shape が載っていない。これらは公式仕様として断定しない。
- eve.rs の `OnSpecialFX` を受信した実クライアントで各 phase が描画されること、Athanor と Tatara の両方での実クライアント表示は未確認。DB-backed headless tests と sim codec tests は通過した。
