# 全サービス応答監査

更新日: 2026-10-13。監査と修正は進行中。全サービス一致・全不具合修正済みとは判定していない。

## 読む順番と現在の集計

1. [日本語summary](C:/Users/yutai/eve.rs/docs/project/service-response-audit/evejs-only-summary.md) — 利用者向けの影響、確定・候補・未確認の区別。
2. [詳細ledger](C:/Users/yutai/eve.rs/docs/project/service-response-audit/evejs-only-responses.md) — EJS-0001〜EJS-0672の672行、両側sourceとcallsite、動的検証の限界。
3. [TODO入口](C:/Users/yutai/eve.rs/docs/project/TODO.md#evejs-response-audit-backlog-2026-10-13) — 448 named出口を63 service group、182候補を59 namespaceで参照。既存owner TODOへ統合し重複加算しない。
4. [Core A-F新handoff](C:/Users/yutai/eve.rs/docs/project/service-response-audit/core-a-f.md:345) と [Host/protobuf監査](C:/Users/yutai/eve.rs/docs/project/service-response-audit/host-protobuf.md) — 新分類と登録・共通通信の根拠。

現在の476 named名差は **未実装448／client-local対応3／legacy未確認25**。綴りalias 2はclient-local 3の内数です。Core A-F旧44件は **未実装33／client-local対応3／legacy6／現行same-service実装済2**、現在の未登録名は42件。実装済2件は既存ledger・TODOから既に除外されており、二重減算しません。

全体は **672＝476＋protobuf候補182＋返値・通知・session・provider14群**。service広告差11／候補29、bound 39（未実装37＋legacy2）、provider6群は重複する別の切り口で、448へ加算しません。182は全nested DTO比較済みではなく、完全比較0／未完了182。Host coverage 181名／1280経路は応答一致率ではありません。

この更新は文書のhandoff照合です。動的な値、world population、通知order、再接続・再起動、実client受入は未確認のまま保留します。source/test/build・Git mutationは行っていません。CBM MCPのTransport closedは指定CLI index_statusでcanonical rootを確認し、Serenaはcanonical projectを使用しました。

### snapshot後の取り込みと再照合待ち

main `c114dc0d` にはupstream `1e3e97f8`（Industry／Market／Cynosural）が取り込まれています。監査snapshot `dda03ca0` の判定・672行ledgerを維持し、これら後続差分の再照合は未完了として保留します。後続mainへの取り込みを、snapshot判定の更新や新しい実動互換の確認とは扱いません。

snapshot後に親＋Zenoが確認したSkills `DiminishedInjectorSp` の既存実装差は、[既存Skills TODOへの修正待ち補足](C:/Users/yutai/eve.rs/docs/project/TODO.md#evejs-response-audit-backlog-2026-10-13)で別管理します。Zenoが修正担当であり、修正済み・検証済みとは記録していません。ledgerへ新しいIDを追加せず、集計を変更しません。今回の文書補足はREADME／TODOのみで、全数再監査は実施していません。

## 対象と根拠

- 作業対象は `C:\Users\yutai\eve.rs` の `main`。source監査snapshot HEADは `dda03ca0464043822e68c8348604ff34eadedcac`（親のdirty宣言を含むsource監査。実運転snapshotではない）。文書読戻し時のmain HEADは `c114dc0d8e292c056e8d71b37f496084f484676f`。この後続commitのsource受入を再検証したとは扱わない。
- EveJS の基準は `C:\Users\yutai\evejs-v0.12.9`、commit `828823a75f211fdecb945b087a26c225f111491f`。
- V24.01 client callsites は `C:\Users\yutai\eve-logic`、commit `1dbb5711a072a146e1d228365668ac05655eefe1`。
- 別checkoutを読んだサブエージェント結果は現行mainの監査結果として受け入れない。絶対パス・branch・HEADを確認して再監査する。

## 手順と判定

1. 全サービス・alias・bound RPC・protobuf RPCを列挙する。
2. 各methodの引数、kwargs、戻り値の型と全フィールド、エラー、通知順序を対応するEveJS/client callsiteまで追う。
3. Hostの実配線、状態の所有者、権限、mutation、commit後通知、次のclient動作まで確認する。
4. 未実装は `docs/project/TODO.md` に追記する。既存実装の破損は回帰テストを伴って修正する。
5. 同じシナリオのMITM比較と関連テストで検証し、確認済み修正からcommit・pushする。

一致にはsourceで確認した範囲と実行で確認した範囲を明記する。動的な値・別状態・未実行の経路は一致と数えない。routeの存在は互換性の証拠にならない。

## 担当範囲

| 文書 | 対象 |
|---|---|
| corporations.md | Corporation、Alliance、War、Office |
| character-skills.md | CharacterSelect、CharacterInfo、Skills |
| inventory-dogma-ships.md | Inventory、Dogma、Ships、Station |
| space.md | Space、Destiny、simulation |
| market-wallet-slash.md | Market、Wallet、Crimewatch、Slash |
| core-a-f.md | Coreファイル名 A-F |
| core-g-m.md | Coreファイル名 G-M |
| core-n-z.md | Coreファイル名 N-Z |
| host-protobuf.md | Host配線・登録、protobuf、共通通信層 |
| host-native-services.md | Host内のmanual IService実装 |
| runtime-comparison.md | 同一シナリオのMITM比較 |

担当文書は検証後に集約する。未提出・別checkoutの報告は集計から除外する。
