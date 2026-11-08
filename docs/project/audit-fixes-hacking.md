# Hacking audit R14 修正記録

監査報告 `/home/user/reports/90-audit-eve-hacking-r14.md` の6件を、現行実装、V24.01 クライアント `/home/user/eve-logic`、EveJS 261028.1 `/home/user/evejs-261028.1` と照合しました。6件すべて実在し、誤検知はありませんでした。

## 確認済みの所見と修正

### P1 — Hacking イベントの wire mapping

V24.01 は `hackingUISvc.py:129-136` で `TileData(**eventData)`、`:210-213` で `UtilityElementData(index=i, **element)` を使います。EveJS `server/src/services/exploration/hackingMgrService.js:93-117` も外側イベントのみ KeyVal にし、内側の辞書を mapping のまま送ります。eve.rs は `HackingMgrService.ToPy(HackingEvent)` の外側を KeyVal に保ち、`eventData`、入れ子レコード、リスト内のレコードを再帰的に `PyDict` に変換するよう修正しました。

回帰テスト `ContainerSiteEndToEndTests.Hacking_turn_records_are_mappings_for_the_clients_kwargs_expansion` は、開始、タイル更新、ウイルス inventory、utility 拾得、cache、攻撃ペイロードを DB-backed サイト経由で確認します。勝利時の core contents も同じ mapping 形を確認します。

### P1 — 解析器開始時の対象認可と予約競合

V24.01 UI の実操作は `hackingUISvc.py:262-280` から hackingMgr の tile/utility RPC を呼び、practice 開始は `:48-52` から呼びます。EveJS `moduleOperation.js:1531-1588` は現在 scene 内の対象、lock、hackable 状態、busy 状態、module range を確認してから開始します。

eve.rs は DB 予約前に `SpaceHackingSite.AnalyzerStartRefusalAsync` を通じて、session pilot/character、solar system scene、target lock、range、現在位置を照合します。simulation が activation を受理した後も secure container を引き直し、reservation character/item と一致することと開始条件を再確認します。不一致なら pending DB 予約だけを解除し、failure count を増やしません。

DB-backed の `HackingMgrServiceTests.The_registered_space_module_path_refuses_an_unlocked_site_container_before_reserving_it` は未ロック、別 system、範囲外、境界内の activation を検証します。`Losing_the_target_lock_while_simulation_accepts_activation_releases_the_pending_reservation` は activation 受理との競合で予約だけが解放されることを確認します。

### P2 — 表面距離に対象半径を含める

EveJS `hackingMgrService.js:264-287` は ship と target 両方の radius を中心間距離から引き、`maxRange + 1` までを許容します。eve.rs は simulation の `PositionOf` radius を優先し、null の自船 radius は SDE/dogma 値へフォールバックして、両方の radius を引くよう修正しました。

`Analyzer_range_uses_both_simulated_radii_and_keeps_the_clients_one_meter_tolerance` は両 radius、ゼロ下限、1 m 許容境界を確認します。DB-backed registered module path のテストも site container 半径 50 m を使い、有効射程境界で受理され、warp 後に失敗が一度だけ永続化されることを検証します。

### P2 — 移動後の idle attempt 終了

EveJS `server/src/space/runtime/scene/tick.js:507-543` は各 scene tick で active attempt の対象、lock と reach を再確認します。eve.rs は `DoDestinyUpdate` と `OnTarget` を転送した後、最大1秒に1回 `HackingMgrService.RecheckReachAsync` を呼びます。範囲/lock/scene が失われると terminal loss を settle し、通知を送り、active DB reservation を解放します。一時的な settlement failure は既存 timer retry に回します。

`Moving_out_of_analyzerrange_ends_an_idle_attempt_and_releases_its_database_reservation` は移動通知の後に hacking RPC を送らずに `Space.QuiesceAsync` し、window、DB reservation、failure row、lost turn を確認します。元のsim通知を先に転送する順序も維持します。

### P2 — 難易度別の board profile と生成

EveJS `server/src/services/exploration/hackingBoardEngine.js:47-74` の tier と hole-fit profile、`:152-329` の seeded generation を参照しました。tier 0–2 は8×8、tier 3 は9×9、tier 4–5 は10×9です。eve.rs は各 profile の core/defense stats、subtype、natural/cache pools に加えて、seeded holes、rim start、full-grid Rule of Eight とその小盤 fallback、Rule of Six、eligible-cell density、core guard、cache contents を実装しました。距離表示も既存セルだけを通る BFS にしました。

EveJS の実行コードを直接 seed 2026 で実行して得た6 tierの盤面と、eve.rs の回帰テストを比較しました。

| tier | tiles | start | core | segment / defense / utility / cache |
|---:|---:|---|---|---|
| 0–2 | 50 | (7,1) | (1,4), tile 34 | 39 / 4 / 3 / 3 |
| 3 | 62 | (0,2) | (8,2), tile 27 | 48 / 5 / 4 / 4 |
| 4–5 | 65 | (0,5) | (8,6), tile 69 | 54 / 4 / 3 / 3 |

`HackingBoardTests.Seed_2026_matches_the_EveJS_reference_board_for_each_tier`, `Tier_profiles_keep_the_reference_defence_stats_and_spawn_pools`, `Generated_populations_follow_the_reference_placement_rules`, and `HackingGameTests.Distance_indicators_follow_the_generated_board_around_holes` cover the six snapshots, profile values/pools, placement rules, and graph distance. The EveJS source explicitly calls the tier and board distributions an authored reconstruction (`hackingBoardEngine.js:3-5`); parity here does not establish CCP server values.

### P3 — QA practice RPC

V24.01 `hackingUISvc.py:48-52` は `StartNewGameInstance(None, gameType, moduleTypeID, difficulty, coherence, strength, slots)` を呼び、developer slash command は `svc_slash.py:623-624` の `/openhacking` です。EveJS `hackingMgrService.js:941-965` は `session.role ?? session.accountRole` の QA bit と null target を必須にします。初版では `accountRole` だけを参照していたため、`role` だけで QA bit を持つ EveJS-compatible session の拒否を先に Host test に加え、両属性の fallback 順を実装しました。

eve.rs の登録済み `hackingMgr` に同名 RPC を追加し、7個の位置引数、null target、QA role を検証します。role 判定は EveJS と同じく `role` があれば優先し、なければ `accountRole` を使います。practice window は site reservation/settlement を作らず、GameStart は `targetID: None` を含みます。

Host composition の `HackingCompositionTests.Qa_practice_board_is_QA_role_gated_and_starts_over_the_registered_service` は両 role 属性の優先順位と開始通知を確認します。`ClientVerifiedServiceWireTests.Hacking_practice_rpc_uses_the_client_null_target_and_starts_a_mapping_shaped_board_over_host_wire` は実 Host packet path、None return、mapping 形の eventData、null target、tier 2 の board envelope と各座標を確認します。

## 誤検知

ありません。全6件で記載された不整合を実装とソース照合で確認しました。

## 実行検証

- Release `-warnaserror` build は `Eve.Hacking`、`Eve.Services.Space`、`Eve.Host`、`Eve.Hacking.Tests`、`Eve.Services.Space.Tests`、`Eve.Host.Tests` で成功し、警告0でした。Host.Tests build 中だけ、作業開始前から未追跡の `InventoryAuditHostWireTests.cs` にある古い `CreateInvBroker` 呼び出しを一時的に現行 signature へ合わせました。テスト後に `/tmp/InventoryAuditHostWireTests.cx6.backup` と一致することを確認し、元のファイルは変更・stage していません。
- `HackingBoardTests` / `HackingGameTests`: 46件成功、skip 0。
- `HackingMgrServiceTests` / `ContainerSiteEndToEndTests` / `AnalyzerSkillTests`: `eve_p6` で30件成功、skip 0。
- `HackingCompositionTests` と hacking practice packet-wire: `eve_p6` を指定して3件成功、skip 0。
- Host の `HostServerTests` 全体: 422/423成功、skip 0。唯一の失敗は未追跡 `InventoryAuditHostWireTests.Registered_invbroker_binds_capsuleer_deliveries_and_the_client_can_take_the_item` line 253 の `Assert.Single()` が空になる既知の inventory wire 失敗で、hacking の変更ではありません。
- テスト先行の red: board profile test は旧 10x9 の64 tileを返し EveJS tier2 seed 2026 の50と不一致、穴越し距離は期待3で旧計算2、QA `role` のみの practice 呼び出しは `accountRole` 限定で拒否しました。各修正後の focused tests はすべて成功しました。

## 未確認

- グラフィカルな V24.01 クライアントでの操作、実クライアント packet capture、tick timing は確認していません。
- EveJS の難易度 profile と seed 2026 の board values は機能参照で確認しましたが、CCP 内部実装や公式 SDE の値としては確認できません。
- グラフィカル V24 client 上での盤面生成分布、実 wire capture、tick の実時間挙動は未確認です。
- Host の広いテスト実行では上記の unrelated inventory wire failure が残っています。`InventoryAuditHostWireTests.cs` は未追跡のまま復元してあるため、CI の tracked branch には含まれません。
