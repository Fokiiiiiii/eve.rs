# 同一シナリオのMITM比較

更新日: 2026-10-12。全サービスの実行比較ではなく、既存の既定シナリオ `tools/headless-client/tests/test_live.py` の実測。

## 再現条件

- `tools/mitm/README.md` の既存方式を使用: MITM capture → 同じpytest選択をEveJS/eve.rsへ実行 → `mitm diff --ignore-volatile`。
- EveJSはread-only clone `828823a` のserver/index.jsを起動。依存packageのみインストール済runtimeからNODE_PATHで解決。SDE由来の生成データとアカウントはCodex作業領域の一時データrootへ分離した。
- 最初に使ったインストール済runtimeは参照cloneと195ファイル異なっていたため、正しいclone本体で再実行した。下記集計はclone本体の結果。
- eve.rsは既存のdevelopment/strict runtimeへ接続。両方4 passed・1 skipped。skipはstrict-passwordシナリオであり、全ての認証経路が検証済みという意味ではない。

## 観測結果

| 項目 | 結果 |
|---|---|
| Client CallReq | 8対8。引数と順序が一致 |
| Server events | 12対12。応答全体は不一致 |
| GetTime | volatile値を除いた形が一致 |
| SessionInitialStateNotification | role値とuserid値が異なる。useridは別DBの生成ID。roleは権限設定を含めた追加比較が必要 |
| GetServiceInfo | EveJS 143名、eve.rs 179名。13名がeve.rs側にない。共有名のlocation routing値に差はなかった |
| GetInitVals | 上記service一覧差と、store flag4項目の文字列/整数差 |
| unknown service/method | EveJSはNone、eve.rs strictはwrapped UserError。strict動作は依頼されたdevelopment設定による差であり、Noneへ戻して不具合を隠さない |

capture時にeve.rsで不足していた名前: `LSC`, `bookmarkMgr`, `certificateMgr`, `contractMgr`, `loadService`, `objectCaching`, `ownerGroupManager`, `planetOrbitalRegistryBroker`, `publicGatewaySvc`, `standing2`, `structure`, `tradeMgr`, `tutorialSvc`。

`standing2` はEveJSのStandingMgrService派生、`tradeMgr` は既存trademgrと同じrouting名。現行mainに実装のある二つはHost aliasの修正対象。残りは各methodの実装/Host配線を確認してTODOへ集約する。名前だけをadvertiseして対応済みにしない。

store flag4項目はEveJS `storeState.js:503-528` が文字列を返す。eve.rs GlobalConfig.Buildの初期値を同じ文字列に修正した。client `globalConfig/getFunctions.py:14-21` はbool(int(value))であり、この差だけがログイン障害の原因だとは判定していない。

## ローカル証拠

Codex作業領域 `service-audit-runtime` に次を保存している。生captureはGitへ含めない。

- `evejs-reference-default-live.jsonl`
- `eve-rs-default-live.jsonl`
- `default-live-comparison-summary.json` — 12イベントすべてを既存比較関数で照合した結果
- `default-live-field-differences.json` — service名とglobalConfigの全差分

未検証: 全RPCの複数状態、実クライアントの出航・再入港、public gateway/XMPPのend-to-end、修正後runtimeでの再capture。上記のテスト成功はこれらを証明しない。
