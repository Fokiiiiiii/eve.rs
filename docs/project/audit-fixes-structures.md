# Structure audit fixes (r14)

対象は `/home/user/reports/90-audit-eve-structures-r14.md` の 5 指摘。V24.01 client build 3396210 (`sde/lock.json`) を基準に、`/home/user/eve-logic` の呼び出し元とルール、EveJS `/home/user/evejs-261028.1` の状態遷移を照合した。5 件とも実際の仕様差だった。誤検出と判定した指摘はない。

## 修正内容と根拠

### P1: Metenox の解除時間

- V24.01 `structures/types.py:30-35` は Auto Moon Miner を Flex 判定より先に分岐し、`structures/const.py:181-184` は通常 7 日、Flex 45 分、Metenox 3 日を定義する。実際の UI 経路は `eve/client/script/ui/services/structure/structureDeployment.py:55-65` で確認した。
- EveJS `server/src/services/structure/structureState.js:2779-2792` は Metenox group/type を Flex family より先に判定する。SDE 3396210 の DB 行 `types.key=81826` は group 4744「Metenox Moon Drill」だった。
- Host の `StructureRulesSource` は type 81826 に 259,200,000 ms を渡し、一般 Flex の 2,700,000 ms は維持する。実際の `structureDeployment.Unanchor` 登録を通る PostgreSQL テストで、snapshot の終了時刻、durable scheduler job、`structure.state_changed` outbox を検証した。

### P2: Insurgency FOB の armor reinforcement

- V24.01 `structures/const.py:151,159` は armor reinforcement を 86,400 秒と定め、`structures/structure.py:95-115` は同じ duration に通常の reinforcement timing とサイズ別 jitter を適用する。`structure.py:225-234` は FOB の hull reinforcement を禁止する箇所で、armor timer の例外ではない。
- EveJS `structureState.js:2665-2695,2698-2752` も armor の標準 24 時間、FOB を特別扱いしない jitter/schedule を使う。`4214-4218` は FOB の例外を hull 側に記録している。
- FOB 専用の 3 時間 ±1 時間定数と分岐を削除し、標準 24 時間 base と 3 時間 jitter / reinforcement schedule を使う。DB-backed regression は早い側・遅い側の時刻、scheduler、通知 payload を検証した。
- CCP の後続 patch note は FOB timer を再変更しているが、今回の対象は pin 3396210 / 2026-06-16 checkpoint のため適用しない。[後続 patch note](https://www.eveonline.com/news/view/patch-notes-version-24-01)

### P2: Flex hull reinforcement

- V24.01 `structures/const.py:168-171` と `structure.py:117-124` は base 3,600 秒に Flex の ±1,800 秒 jitter を適用する。EveJS `structureConstants.js:214,219` と `structureState.js:2665-2675,2687-2695` も同じ値。
- Flex hull base を 1 時間に変更し、通常の 30 分 jitter は維持した。DB-backed regression は reinforce 終了時刻、保存 snapshot、scheduler job、timer notification を照合した。

### P2: queued reinforcement time の適用条件

- V24.01 `structures/structure.py:331-338,652-653` は Shield Vulnerable かつ未損傷のときだけ pending 値を適用する。EveJS `structureState.js:2835-2844,3065-3095` は完全修復を確認し、未修復なら pending を保持する。
- eve.rs は Shield Vulnerable、修理 deadline なし、pause なしの状態でのみ deadline を適用する。条件がないあいだ pending deadline を scheduler から外し、期限切れ job のループを防ぐ。修理完了後に同じ transition 内で適用する。
- 回帰テストでは適用期限直前に損傷を入れ、pending 値が維持されること、scheduler が repair deadline を保つこと、repair 完了後に pending が一度だけ反映されることを DB-backed で検証した。

### P2: Low Power abandonment deadline

- V24.01 `structures/const.py:192-194` は 7–8 日を定義し、`structures/structure.py:604-618` は Low Power 中に期限を一度サンプルして保存する。EveJS `structureState.js:481-488,3365-3393` も同じ範囲の絶対期限を保存する。
- Snapshot に `AbandonAtMs` を追加し、Low Power へ入る時に 7–8 日から一度サンプルする。DB column `abandon_at_ms` と legacy `abandonAt` に保存し、再起動後は再抽選しない。`0009_abandon_at_ms.sql` は既存 eve.rs Low Power 行を従来の 7 日 deadline で backfill し、`StructureStoreMigrationTests` が既存 Low Power 行と対象外行を検証する。
- regression tests は最小/最大 endpoint、Low Power → Abandoned、legacy row projection、process restart 後も期限が変わらないことを確認する。[CCP の abandonment 説明](https://support.eveonline.com/hc/en-us/articles/360014282739-Upwell-Structures-Abandoned-State)

## 実行経路と検証

- Host は `Composition/StructureComposition.cs:227-233,381,490-495` でルール、durable timer、`structureDeployment` RPC callback を登録する。`StructureDeploymentAuthority.cs:81-107` は実際の Unanchor RPC を権限確認後に `StructureTimers.ApplyAsync` へ渡す。timer は状態更新、outbox event、scheduler deadline を caller transaction 内で保存する。
- 5 件の回帰テストを実装前に旧挙動へ実行し、それぞれの誤った時間/遷移で失敗することを確認した。修正後は Structures project 113 件、Host の構造物関連クラス 120 件、Store を参照する追加 Host クラス 112 件、Host rules/composition/import replay 3 件、migration upgrade 1 件が通過。全て DB-backed test は `eve_p5` を使用し、これらの最終実行では skip 0 件。
- EveJS helper を read-only で Node 実行し、Metenox 259,200 秒、一般 Flex 2,700 秒、abandonment 7 日/8 日端点を確認した。Require 時に static `structureTypes` fallback のログが出たが、対象 helper の入力は明示した type/group/family、DB write はない。
- 以前の幅広すぎる Host filter の試行は unrelated な skip と期待値調整前の失敗を含んだため中断し、通過扱いにしていない。修正後に対象を絞った Host テストを実行した。
- Push 後の main CI Run 1604 で `InsurgencyDirectorTests.A_campaign_starts_with_its_fob_invulnerable_and_the_empire_side_at_its_count_exposes_it_and_destroying_it_ends_the_campaign` が失敗した。統合テストが旧 FOB 3 時間前提で固定時間を進めており、24 時間 armor reinforcement と次の UTC reinforce hour / jitter を超えられず、最後の FOB destruction assertion に到達しなかった。V24.01 `structures/const.py` の `REINFORCE_DURATION_ARMOR_INSURGENCY_FOB = 86400` と標準 jitter、EveJS `structureState.js:2679-2684,2687-2752` を再確認し、テストを persisted `StateEndsMs` まで進める形に直した。破壊 assertion は維持し、`InsurgencyDirectorTests` の DB-backed 4 件が `eve_p5` で成功した。
- 実 GUI client、実サーバー間の packet capture、長時間稼働中の multi-process race は未確認。
