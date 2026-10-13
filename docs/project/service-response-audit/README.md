# 全サービス応答監査

更新日: 2026-10-12。監査と修正は進行中。全サービス一致・全不具合修正済みとは判定していない。

## 対象と根拠

- 作業対象は `C:\Users\yutai\eve.rs` の `main`。初期化修正 commit は `b636110e`、上流取り込み commit は `1556ee81`。
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
