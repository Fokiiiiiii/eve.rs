# EveJS 261108.1 port: moon mining

## 調査結果

対象は Standup Moon Drill を載せた Athanor / Tatara の手動抽出である。CCP の Moon Mining ガイドは、抽出開始後にチャンクを精錬所へ牽引し、抽出終了時に待機状態となり、ドリルで破砕すると採掘可能な小惑星ができること、抽出時間が 6～56 日であること、未破砕なら抽出終了から 3 時間後に自動破砕することを説明している。ガイドはチャンクモデルの容積境界や破砕中の 25 秒時点までは規定していない。

- CCP, [Moon Mining](https://support.eveonline.com/hc/en-us/articles/115005404229-Moon-Mining)
- CCP, [Metenox Moon Drill](https://support.eveonline.com/hc/en-us/articles/14341380413212-Metenox-Moon-Drill)

V24.01 クライアントでは `schedulingSvc.py:43-75` が抽出開始・手動破砕・キャンセルの RPC を呼び、`moonminingExtraHudBtns.py:127-197` が進行中・破砕中・完了の UI と 30 秒破砕タイマーを扱う。`MultiEffect.py:100-155` は `graphicInfo` の `phase` / `effectStarted` / `sequence` / `triggers` からフェーズ演出を再生・更新し、`structureDrillingPlatform.py:20-40` はストラクチャ slim の `closestMoonID` を Moon Mining turret set へ渡す。`fxSequencer.py:333-385` の `OnSpecialFX` は送信元、ドリル module、対象、effect GUID、開始状態と `graphicInfo` を受け取る。

Metenox は手動の抽出スケジュールとは別系統である。クライアントの `moonmining/autoMoonMining/client/auto_moon_miner_service.py:9-29` は `autoMoonMining` に `RequestMiningDetails` と `RequestMiningCycleOutput` を要求する。CCP も Metenox を時間をかけて raw moon materials を集める自動採掘施設として説明している。したがって、手動チャンクの牽引・破砕エフェクトを Metenox に流用していない。

## eve.rs に既にあった動作

- `MoonExtractions.cs:37-43, 130-164, 172-201, 256-277` は手動抽出の期間、永続化、開始・到着・破砕・キャンセルの状態遷移を実装済みだった。durable scheduler とイベントトランザクションも既存の仕組みを使う。
- `SpaceMoonFields.cs:55-82` は抽出終了イベントから採掘可能な鉱石フィールドを作る。`MoonOwnerNotifications.cs:40-67` は開始・到着・破砕・終了イベントの通知経路を持つ。
- `.NET` には Metenox の構造・サービス識別や配置制約の基盤があるが、この調査で `autoMoonMining` の RPC/cycle 処理は見つからなかった。これは今回追加していない。

## 今回追加した動作

- `SpaceMoonChunks.cs:65-108, 110-134` が開始、チャンク到着、ドリル発射、破砕、終了イベントを購読し、システム進入時に DB の抽出状態から再構成する。再構成は抽出行をロックし、永続 inventory / `space_items` を再利用するため、同時復元や再起動でチャンクが重複しない。
- `SpaceMoonChunks.cs:65-134, 136-217, 286-315` は SDE の moon 座標、moon chunk type の描画属性、精錬所と Standup Moon Drill の実 item を使って scene entity と `effects.MultiEffect` を作る。抽出中は牽引位置を時刻から計算し、到着時に stasis、手動破砕時に fracture phase に移る。`StructureMoonMiningSlim.cs:11-40` と `SpaceStructures.cs:182-217` は V24 クライアントが turret set と moon を解決できるよう、ドリル module tuple と `closestMoonID` を structure slim に載せる。Host 組み立てでの subscriber 登録は `StructureComposition.cs:404-410`。
- `MoonExtractions.cs:172-190, 267-278, 326-343, 362-380` は手動破砕から 25 秒後にチャンクを爆発除去し、30 秒後の既存 fracture job で鉱石フィールドを作る。爆発済み時刻を `MoonExtractionStore.cs:10-31, 101-112, 190-193` と migration `dotnet/src/Eve.Structures/Migrations/0010_moon_chunk_exploded.sql` に保存するため、再起動後に爆発済みチャンクを再生成しない。
- `SimCodec.cs:26` と `SimCodec.Shared.cs:51-53` の protocol 94 に chunk upsert/remove と structure moon-mining slim 更新を追加した。破砕・自然破砕では destruction effect 3、キャンセル / オフラインでは静かな除去を送る（`SpaceMoonChunks.cs:92-105, 220-245`）。通常の `OnMoonExtractionUpdate` は既存の RPC / extraction 通知経路を維持し、視覚フェーズは V24 の `OnSpecialFX` 形に合わせた sim message から送る。
- 既存 fixture generator で wire fixture を生成し直した。実 Macho dispatcher / service registry を通す RPC テストは `MoonExtractionStartHostTests.cs:214-234`、DB を使う空間投影・到着・破砕・爆発・再起動テストは `MoonMiningChunkTests.cs:24-160` にある。

## Client notifications and phase effects

- `MoonExtractions.StartAsync` writes the extraction row, arrival and natural-fracture jobs, and the `moon.extraction_started` event in one transaction (`MoonExtractions.cs:134-163`). After a successful RPC, `StructureMoonAuthority.StartAsync` sends the extraction payload in `OnMoonExtractionUpdate` (`StructureMoonAuthority.cs:94-120`). On end, Host sends `OnMoonExtractionUpdate(None)` to corporation members and pilots in the structure. Owner inbox notices are a separate path: start 202, arrival 204, laser fired 205, cancelled 203, and automatic fracture 206 (`StructureComposition.cs:417-424`, `MoonOwnerNotifications.cs:40-77`).
- The three spatial phases are synchronized with sim `OnSpecialFX`, not `OnMoonExtractionUpdate`: start sends `tractor_phase` with the extraction start time; arrival sends `stasis_phase` with the chunk-available time; fracture start sends `fracture_phase` with the firing time (`SpaceMoonChunks.cs:65-89,286-314`; `crates/eve-space/src/payload.rs:163-217`). V24.01 `eve/client/script/environment/effects/MultiEffect.py:100-155` starts and switches the sequence from `graphicInfo.phase`, `effectStarted`, `sequence`, and `triggers`. A manual fracture removes the chunk with an explosion effect after 25 seconds, then ends the extraction and creates the ore field at 30 seconds (`MoonExtractions.cs:61-64,326-343`, `SpaceMoonChunks.cs:92-105`). Cancel and offline remove the chunk quietly.
- `OnMoonExtractionUpdate` is not sent for each visual phase. The V24 HUD calculates progress from extraction/fracture timestamps; manual laser input uses local `OnMiningLaserFiredLocal`; start/end state sync uses `OnMoonExtractionUpdate`; and 3D phase playback uses `OnSpecialFX` (`moonminingExtraHudBtns.py:127-197`, `schedulingSvc.py:43-75,112-114`).
- Metenox uses a separate automatic-mining path. V24 calls `autoMoonMining.RequestMiningDetails` and `RequestMiningCycleOutput` (`moonmining/autoMoonMining/client/auto_moon_miner_service.py:9-29`). eve.rs has Metenox type, placement, unanchor, and fuel handling, but no `autoMoonMining` RPC/output cycle at this point. EveJS `autoMoonMiningService.js` and `structureAutoMoonFuelRuntime.js` were not folded into the manual Athanor/Tatara extraction phase-effect port.

## EveJS との差分と不採用項目

- EveJS 261108.1 の `moonMiningSpaceRuntime.js:146-205, 225-345, 361-465, 668-711` は chunk path / phase effect / spawn / movement / restore を実装する。`moonExtractionsService.js:671-690, 1073-1094, 1215-1253, 1381-1419` は発射時のフェーズ更新、25 秒爆発、抽出開始、到着・自動破砕の処理を持つ。eve.rs では同じクライアント可視動作を既存の DB・scheduler・event bus・SDE 所有者へ組み替えた。
- EveJS `moonExtractionRules.js:224-235` のチャンク shell tier 境界（14 / 28 / 42 日相当）は現時点で EveJS 実装と SDE の type/group/render data 以上の独立した TQ 根拠を確認できていない。コードは既存の抽出 volume 計算からモデルを選ぶためこのルールを機能参照として使うが、境界自体を CCP 公式仕様としては扱わない。実クライアントで各境界を通る表示は未確認。
- EveJS 261108.1 にある GM 用 extraction-time advance helper はこの port の変更には含めていない。対象はプレイヤーの V24 RPC と phase effect でなく、管理用時刻操作だからである。現在のブランチには origin/main 由来の GM scheduling endpoint が別途あるが、この port はその実装を変更していない。
- EveJS に専用 moon mining test file / npm test script はなく、ローカルの SDE/scene 全体を使った runtime 比較はできなかった。合成 scene を使う read-only probe では `spawnChunkForExtraction` の chunk type、tractor phase、module / target と `effects.MultiEffect` の構成を確認したが、EveJS の DB 経由 end-to-end 検証ではない。

## Missing-drill fixture and correction

- Main CI Run 1691 failed for a separate reason: the sim-link decoder rejected owner ID `0` for an ESS. That root cause and fix are recorded in main commit `b1788250a` and `docs/project/main-ci-red-neutral-structure-owner-2026-11-10.md`. The run's live log also recorded retries for `moon.extraction_started` event 343 because a drill was missing from the fixture; that symptom was not the cause of the two red CI tests.
- The live fixture `test_structures.py:89-99` previously set service 14 online without creating an inventory item or online module cycle. It now seeds an SDE type-45009 inventory item in service slot 164 and a `structure_service_module_fuel` cycle containing service 14.
- EveJS 261108.1 `moonExtractionsService.js:191-197,270-291` requires an online fitted type-45009 module when starting an extraction. The eve.rs RPC gate `StructureMoonAuthority.GateAsync` previously checked only the service row. It now checks service state, module fuel cycle, and an item with the same module ID and type 45009 in flags 164-171 inside the extraction transaction. The registered `moonExtractions.StartNewExtraction` DB rejection test checks the wire error and unchanged state (`MoonExtractionStartHostTests.cs:254-273`).
- EveJS `moonMiningSpaceRuntime.js:173-204,244-247,288-292` still builds a chunk entity when no drill is found, with no special-FX state. eve.rs previously threw during projection and held the event subscriber in retry. It now sends a chunk upsert with both module fields zero; the Rust scene keeps placing/updating the chunk and emits no Moon Drill FX. The payload length and field order are unchanged. The C# and Rust SimLink decoders accept `(0, 0)` and reject a half-zero pair.
- DB-backed `MoonMiningChunkTests` processes a durable extraction event without a module item and verifies no event retry, a persisted chunk, and module ID/type zero. Rust scene coverage checks chunk placement and phase updates without FX; link/codec tests check the optional module pair. This proves restoration of persisted rows without a drill; it does not allow the normal RPC to start a drill-less extraction.

## 実行した検証

- 初回port時の検証: Release build は `Eve.Space`, `Eve.Ships`, `Eve.Structures`, `Eve.Services.Space`, `Eve.Host` 各 0 warning / 0 error。Rust は `cargo fmt --all -- --check`, `cargo check -p eve-space`, `cargo clippy -p eve-space -- -D warnings`, `cargo test -p eve-space moon_mining` 2 passed, `cargo test -p eve-space moon_chunk` 1 passed, `cargo test -p eve-space --test simlink_fixtures` 13 passed。DB `eve_p3` の先行focused testsは `Eve.Space.Tests` 550, `Eve.Structures.Tests` 25, `Eve.Services.Space.Tests` 73, `Eve.Ships.Tests` 9, Host選択クラス47 passed。skipなし。Host composition testsの前に `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve_p3 cargo run -p eve-sde --bin eve-sde -- ensure --missions --keep-archive` で公式 mission SDE build 3405148 を投入した。最新main merge後は `MoonMiningChunkTests` / `SiteNpcTests` / `WormholeSiteTemplatesTests` 10 passed / 0 skipped。
- Run 1691 follow-up の Release build: `Eve.Space`, `Eve.Services.Space`, `Eve.Host`, `Eve.Space.Tests`, `Eve.Services.Space.Tests`, `Eve.Host.Tests` は各 0 warning / 0 error。DB `eve_p3` では `MoonExtractionStartHostTests` 16 passed、`MoonMiningChunkTests` 2 passed、`StructureCompositionTests` 20 passed、`SimCodecTests` 488 passed。skipなし。Rust は `cargo fmt --all -- --check`, `cargo check -p eve-space`, `cargo clippy -p eve-space --all-targets -- -D warnings`, `cargo test -p eve-space moon_chunk --lib` 3 passed, `cargo test -p eve-space moon_mining --lib` 3 passed, `cargo test -p eve-space --test simlink_fixtures` 13 passed。変更した headless fixture は `python3 -m py_compile tools/headless-client/tests/test_structures.py` で構文確認した。Red-gate 修正 commit `b1788250a` の main Run 1692 は success。Moon mining changes were pushed in `8db827f6f` and `5a149c880`; main CI Run 1693 completed successfully, including the server-stack/headless-client and Windows jobs, with no failed tests or jobs (`tools/dev/ci-status.py wait`).

## 未確認

- V24.01 の graphical client での実プレイ確認、および TQ server trace は未実施。
- CCP 公開資料には shell tier の正確な容積境界、破砕アニメーション開始から chunk 消去までの 25 秒値、Metenox の cycle RPC wire shape が載っていない。これらは公式仕様として断定しない。
- eve.rs の `OnSpecialFX` を受信した実クライアントで各 phase が描画されること、Athanor と Tatara の両方での実クライアント表示は未確認。DB-backed headless tests と sim codec tests は通過した。
