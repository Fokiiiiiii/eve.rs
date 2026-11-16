# EVE.js 261108.1 と eve.rs の Space / World 差分監査

監査日: 2026-11-16

対象: EVE Online V24.01 build 3396210 (`/home/user/eve-logic`)、EVE.js 0.12.9 (`/home/user/evejs-261108.1`)、この worktree の eve.rs。

## 判定と範囲

この文書は今回確認した差分・未解決の候補を記録する。Space / World 全機能の完了監査ではない。特に EveJS と eve.rs の同一シナリオの完全な比較、全 Destiny call / packet の記録、全サービス・遷移のクライアント全体の読み合わせは完了していない。未確認を「差分なし」とは判定しない。

判定は `CONFIRMED FIXED`（V24 client が使う挙動差を確認し、回帰を追加して修正）、`POSSIBLE BREAK`（依存経路は確認したが、実クライアント破損は再現していない）、`NOT REAUDITED`（この作業で新たな判定をしていない）を使う。

明示された対象外: incursions payout、skyhook raids、void space、nodegraph、season content、security mission offers。security mission offers は economy job の対象。

## ライブ比較と decoder

`tools/dev/run-evejs-live.sh /home/user/evejs-261108.1 .cache/sde/eve-online-static-data-3396210-jsonl` で EveJS は起動し、headless client の login、character 作成、選択まで応答した。しかし同じ undock walk は、新規キャラクターの `GetInventory(10004).List()` が空の `ObjectEx('__builtin__.set', [[]])` を返した時点で停止した（MITM trace `/tmp/cx6-evejs-undock.jsonl`, events 101–104）。サーバーログにはそのキャラクターへの船の inventory 同期もある（`/tmp/cx6-evejs-logs/evejs-server.out`）。したがって EveJS 側の同 walk は undock に到達せず、eve.rs で別に成功した headless run と同一条件の差分には使えない。EveJS が起動できなかったという意味ではなく、比較用キャラクターの格納庫状態が成立しなかった。

MITM は現在、marshal action と Destiny wire buffer の両方を保持する。`SetState` (packet type 0) と `AddBalls2` (type 1) の header/stamp と、連結された free/rigid ball の ID、mode、radius、position、flags、velocity、mode tail 等を field view に展開する。rigid collision tail は型依存なので推測せず raw hex で残す。未対応・切断データは offset 付き `decodeError` として記録し、元 buffer も保持する。`GotoDirection` / `WarpTo` は action 引数を tuple の要素ごとに比較する。これは比較を助ける decoder 追加であり、Destiny 全形式の native client decoder ではない。

## 差分・リスク登録

| ID / verdict | eve.rs / EveJS の構成 | V24.01 client が依存する箇所 | 根拠と結果 |
| --- | --- | --- | --- |
| SW-01 — `CONFIRMED FIXED` Abyss gate activation range | eve.rs は以前、authoritative pose の表面距離が 2,500 m 以下の場合だけ受け付けた。現在は [`AbyssalTraceRange.cs:27-46`](../../dotnet/src/Eve.Host/AbyssalTraceRange.cs)、[`CharacterServicesComposition.AbyssalFlows.cs:271-293`](../../dotnet/src/Eve.Host/CharacterServicesComposition.AbyssalFlows.cs)、[`SpaceSessions.Abyssal.cs:243-279`](../../dotnet/src/Eve.Services.Space/SpaceSessions.Abyssal.cs)、[`spawn.rs:735-786`](../../crates/eve-space/src/sim/spawn.rs) により、committed FOLLOW の状態を Rust sim query から Host へ返す。EveJS は exact target への committed `FOLLOW` と range <= 2,500 m がある時、`min(2,500, max(500, maxVelocity * 2))` を加算する (`/home/user/evejs-261108.1/server/src/abyssal/runtime/abyssalGateActivationRange.js:52-105`)。 | `/home/user/eve-logic/eve/client/script/ui/services/menusvc.py:3042-3047,3697-3700`; `/home/user/eve-logic/eve/client/script/parklife/autopilot.py:488-491,503-522`; `/home/user/eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:568-579` | client は 50 ms の自動接近を始め、距離内で RPC を実行する。許容内で gate に FOLLOW 中でもサーバーの厳格判定が先に拒否し得た。Host/DB の失敗先行回帰 `Abyssal_gate_accepts_a_committed_follow_approach_to_the_exact_gate` を追加し、exact gate の bounded FOLLOW は受理、別 target / range 超過は状態を変えず拒否、成功時は room notification 順まで確認。SimLink protocol 99。 |
| SW-02 — `POSSIBLE BREAK` POS force field の生成・解除 | EveJS はオンライン tower に force-field item を生成し scene に spawn/reconcile する (`/home/user/evejs-261108.1/server/src/services/ship/controlTowerRuntime.js:935-1032,1488-1554`)。fuel failure では offline 化後に force field を削除する (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseFuelRuntime.js:410-507`)。eve.rs の tower anchor/online/fuel 経路は [`SpaceContainers.ControlTowers.cs`](../../dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:193) にあるが、今回のコード調査では tower に紐づく force-field ball の作成・harmonic 更新・fuel shutdown cleanup 経路を確認できなかった。既存記録も未実装としている (`docs/project/deepfix-structures-pos.md:53`)。 | `/home/user/eve-logic/eve/client/script/parklife/playerOwned.py:91-115` は tower の `forceFieldID` と ball harmonic を使う。`/home/user/eve-logic/eve/client/script/ui/inflight/moonmining.py:1242-1248,1402-1412` は同 owner の force-field slim を検索し、追加時に POS UI を更新する。 | force-field ball / harmonic の欠落または stale state は force-field 内判定と moon-mining UI を壊し得る。実 client 再現は未実施のため `POSSIBLE`。force-field 作成・更新・燃料切れ後の除去の PostgreSQL + real sim Host E2E は未追加。 |
| SW-03 — `POSSIBLE BREAK` POS assembly-array industry facility | EveJS は POS assembly array の group/type profile、anchored tower association、online 条件、facility payload と location を実装する (`/home/user/evejs-261108.1/server/src/services/industry/starbaseIndustryRuntime.js:19-104`)。eve.rs の facility resolver は NPC station と永続 Upwell structure を処理するが POS array の deployed state / tower relation は解決しない (`dotnet/src/Eve.Host/Composition/IndustryComposition.cs:69-131`; 既存範囲記録 `docs/project/deepfix-industry.md:37`)。 | `/home/user/eve-logic/eve/client/script/industry/facilitySvc.py:42-72,114-120`; `/home/user/eve-logic/eve/client/script/industry/industrySvc.py:92-110,139-160`; `/home/user/eve-logic/eve/common/script/util/industryCommon.py:378-418` | facility list/detail を使う client industry flow で EveJS が返す array が eve.rs では出ない可能性がある。クライアントでの facility discovery / job install は今回未実施。POS array の実在状態、access、job escrow、tower shutdown 後の pause/resume は未実装・未確認。 |
| SW-04 — `POSSIBLE BREAK` deployment placement の dynamic blockers | EveJS は scene があれば live scene balls と warp-in points を配置検証に使う (`/home/user/evejs-261108.1/server/src/services/structure/structureDeploymentPlacement.js:250-300,417-423`)。eve.rs の Host anchoring check は SDE static balls と persisted structures を見るが、simulation live balls と warp-in points を列挙しない (`dotnet/src/Eve.Host/Persistence/Structures/StructureAnchorAuthority.cs:39-42,247-294`; 既存記録 `docs/project/deepfix-structures-pos.md:54`)。 | `/home/user/eve-logic/eve/client/script/ui/structure/deployment/deploymentEntity.py:50-64,66-69` | client preview は実 ballpark の balls/items で blocker を調べる。stale/並行 scene state や RPC 直呼びで Host と client 判定が異なり得る。Host-side equivalence は未確認で `POSSIBLE`。 |
| SW-05 — `POSSIBLE BREAK` FW player kill / hub-destruction progression | eve.rs には NPC kill producer と `CaptureAsync` があるが、player-versus-player kill の VP/stat producer と FW hub destruction の capture trigger は未構築 (`docs/project/FW_DESIGN.md:110-115,139-146`; `dotnet/src/Eve.FactionWarfare/FwService.cs:139-152,186-204`)。EveJS の確認できた FW service は VictoryPointState の read/publish を持つ (`/home/user/evejs-261108.1/server/src/services/faction/fwVictoryPointMgrService.js:92-143`, `fwQueryPayloads.js:13-51`); この source pass では kill / hub producer を確認できなかった。 | `/home/user/eve-logic/eve/client/script/ui/services/fwVictoryPointSvc.py:31-60` は local/all VP state と update notification を読む。 | V24 UI は状態更新に依存する。eve.rs に gameplay producer がないのは機能上の不足だが、EveJS の producer 同等性も確認できず、EveJS に対する実行時差とは判定しない。kill-to-VP と hub-destruction capture は `POSSIBLE` / unresolved。 |

## 既存監査からの関連記録（今回は再実行していない）

以下は過去の source / test 記録への索引であり、今回の side-by-side 実測ではない。既存記録の verdict を今回の pass として数えない。

| 分野 | 既存記録 | 今回の扱い |
| --- | --- | --- |
| Combat / damage / NPC / drones | [`audit-space-combat.md`](audit-space-combat.md), [`deepfix-dogma-fitting-combat.md`](deepfix-dogma-fitting-combat.md), [`COMBAT_STATUS.md`](COMBAT_STATUS.md) | EveJS / Host / sim の既存差分・修正・未検証項目がある。今回全シナリオは再実行していない。 |
| Targeting / modules / charges / wrecks / loot | [`audit-fixes-space.md`](audit-fixes-space.md), [`audit-fixes-destiny.md`](audit-fixes-destiny.md), [`TODO.md`](TODO.md) | source/fixture evidence が分散している。今回全呼出し・通知順は列挙し直していない。 |
| Warp / jump / gates / movement | [`handoff-warp-arrival-cx6.md`](handoff-warp-arrival-cx6.md), [`live-fix-ballpark-undock.md`](live-fix-ballpark-undock.md), [`MITM_COMPARE.md`](MITM_COMPARE.md) | EveJS headless undock は hangar List 前に止まり、paired capture はない。 |
| Probes / scanning / anomalies / dungeons / wormholes | [`service-audit/deepfix-probes-scanning.md`](service-audit/deepfix-probes-scanning.md), [`service-audit/deepfix-wormholes.md`](service-audit/deepfix-wormholes.md), [`audit-fixes-dungeons.md`](audit-fixes-dungeons.md) | 個別の過去監査はある。今回 live side-by-side は未実施。 |
| Structures / POS / sovereignty | [`deepfix-structures-pos.md`](deepfix-structures-pos.md), [`deepfix-planets-sov-warfare.md`](deepfix-planets-sov-warfare.md), [`FW_DESIGN.md`](FW_DESIGN.md) | POS force-field / array / placement と FW progression の上記候補以外は再監査していない。 |
| Mining / planets / industry | [`deepfix-industry.md`](deepfix-industry.md), [`audit-fixes-planets.md`](audit-fixes-planets.md), [`INDUSTRY_STATUS.md`](INDUSTRY_STATUS.md), [`PI_DESIGN.md`](PI_DESIGN.md) | この作業では差分 walk を実行していない。 |
| Incursion / abyss / event / GM / skyhook | [`TODO.md`](TODO.md), [`ABYSSAL_DEADSPACE.md`](ABYSSAL_DEADSPACE.md), [`audit-fixes-events.md`](audit-fixes-events.md), [`evejs-261108-port-gm-commands.md`](evejs-261108-port-gm-commands.md) | Abyss gate のみ新規修正。対象外機能は上記から除外。その他は未再監査。 |

## 今回の検証

- 変更した Host、Services.Space、Space と対応するテストプロジェクトを Release でビルド（`-warnaserror`）し、警告 0・エラー 0。
- PostgreSQL `eve_p6` を使った Abyss Host flow / range クラス選択は 44 passed、0 failed、0 skipped。さらに `FullyQualifiedName~Abyssal` の選択は 103 passed、0 failed、0 skipped。失敗先行の gate 回帰、誤 target / 距離超過の拒否、状態維持、notification 順を含む。
- `tools/headless-client/tests/test_destiny_decode.py` と `tests/test_mitm.py`: 28 passed、0 failed、0 skipped。ball buffer decoder は free/rigid field の連結と入力切断時の error を確認。
- 現在の Rust tree で `cargo fmt -p eve-space -- --check`、`cargo check -p eve-space`、`cargo clippy -p eve-space --all-targets -- -D warnings` が通過。pose-query と SimLink fixture の focused test は各 1 passed、0 skipped。
- Eve.Space codec / Abyssal scene / SimClient の選択は 553 passed、0 failed、0 skipped。Abyssal space-session の選択は 20 passed、0 failed、0 skipped。
- EveJS と eve.rs の完全な同一 run 比較、graphical client、full live headless suite、全 feature の restart / retry / concurrency matrix は未実施。

## 残る作業

上記候補を含む全 Space / World route の call / error / notification / Destiny buffer 差分と Host wiring を、同一 state の EveJS / eve.rs で記録できていない。EveJS の起動確認とは別に、比較 walk が成立する starter ship / hangar fixture を作り、undock から各機能へ進める必要がある。force field、POS array、dynamic placement、FW producer の候補は、V24 client / CCP source と runtime evidence に基づく failing end-to-end regression を追加してから実装判断する。現時点ではこの広域 job の Definition of Done に達していない。
