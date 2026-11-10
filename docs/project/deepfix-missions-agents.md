# Missions and agents deep correctness fix (cx4)

## 調査基準と範囲

V24.01 client build 3396210 の全ツリーを `GetAgents`, `GetReplayTimestamp`, `GetMissionObjectiveInfo`, `GetMissionJournalInfo`, `GetMyJournalDetails`, `OnAgentMissionChange`, `OnStandingsModified` で検索し、UI callsite と通知 consumer を追った。EveJS 261028.1 は agent mission runtime、standing runtime、agentMgr handler を参照し、`buildMissionRewards` を Node から実行した。CCP の [Standings help](https://support.eveonline.com/hc/en-us/articles/203217152-Standings) は、同じ agent から4時間以内に2件 decline すると agent / corporation standing を失うことを明記している。

## 修正した不一致

| 経路 | 確認した差異 | 修正と回帰確認 |
|---|---|---|
| Agent finder | Client `eve/client/script/ui/station/agents/agents.py:91-100` が `agentMgr.GetAgents()` rowset を station/system/faction/corporation index に使う。Host は登録済み `agentMgr` から SDE agent rowset を返し、`AgentMgrReadWireTests` が15列と Host socket の shape を検査する。今回この境界の追加差異は見つからなかった。 | finder の実装変更なし。`AgentMgrReadWireTests` と `AgentMissionReadWireTests` を実行。グラフィカルな Agency / finder 画面は未起動。 |
| Decline後の offer | EveJS `agentMissionRuntime.js:7728-7781,8201-8234` は idle agent に Request を出し、timer 中でも次の offer を許す。eve.rs は `DeclineUntil` 中に Request を隠し、store も再offerを拒否していた。 | 再offerを許し、期限中のdeadlineをoffer/accept後も保存する。expired deadline は新しいofferへ持ち越さない。Host E2E は拒否直後の再offer、timer保持、期限ちょうどの再offerを確認。 |
| Replay timer | Client `agents.py:306-314` が各 bound agent の `GetReplayTimestamp` を呼び、`agentDialogueWindow.py:327-338` は brief の `Decline Time` を残り時間として表示する。EveJS `missionRuntimeState.js:24,1350-1385`, `agentMissionRuntime.js:6281-6289,7455-7458` は agent 別の4時間timerを読む。 | `AgentMissionStoreRecords.Overlay` が永続mission storeのtimerをGameStore viewに重ね、`GetReplayTimestamp` は未来のFILETIMEのみ返す。Wire test は未来値と期限切れ値を検査。decline timer はPostgreSQLにあり、store再生成後の保持も確認。 |
| Decline action / notification | V24 `missionDetailsWindow.py:13-20,69-76` は action `'declined'` で mission details を閉じる。`appConst.py:1603,1608` では `'declined'` と `'offer_declined'` は別action。EveJS `agentMissionRuntime.js:8229-8232` は `'declined'` を送る。 | store の通知actionを `'declined'` に統一。bound `DoAction` を通すHost E2Eで `OnAgentMissionChange` actionと並びを確認。全 client のmission通知consumer一覧は `docs/project/service-audit/sweep-comments-eve-agents.md:12-18` に記録済み。 |
| Objective表示 | Client `agentDialogueWindow.py:221-225,430-438` は `GetMissionObjectiveInfo` を描画し、`agentDialogueUtil.py:100-120,282-315` は root `completionStatus` と dungeon `completed` / `objectiveCompleted` を別々に読む。EveJS `agentMissionRuntime.js:5804-5834,5958-5978` は objective 完了をrootとdungeonに出す。 | `ObjectiveCompleted` をroot completionに使い、dungeon `completed=1` は完了時だけ、`objectiveCompleted` は常に `1` または `None` で返す。Core wire-shape test と登録済みHost E2Eで、acceptedのまま目的だけ完了したケースを検査。 |
| 2回目の拒否とstanding | EveJS `agentMissionRuntime.js:204-218,1134-1145,1319-1342,8201-8223` はtimerがまだ未来ならdecline event 75を適用し、levelごとのagent raw値をagent loss、1/10をcorporation lossに使う。初回は無損失。公式CCP資料は損失対象と4時間ルールを確認するが、正確なper-mission値は示さない。EveJSのdecline comment自体も実際の値がmissionごとに変わり、基準より高くなることを記す。 | storeのrow lock内で初回/2回目を判定し、decline遷移と直接agent/corporation standing履歴を同一transactionでcommitする。eventTypeID 75、raw notification row、二重処理の冪等性を確認。EveJSのsecurity / distribution raw agent値は同じlevelで一致するため、`mining` も含め、important mission・non-basic agent typeでもlevel curveを適用する。 |
| Mission journal | Client `neocom/journal.py:168-184` は `OnAgentMissionChange` でagent journal cacheを無効化する。`GetMyJournalDetails` と bound `GetMissionJournalInfo` は `agents.py:746-756` などから呼ばれる。 | Store record overlay は offered / accepted / completed のみをjournal viewに載せ、declined / quit / failed は外す。既存の journal wire/Host tests を実行。今回journal row shape自体の差異は見つからなかった。 |
| Mission completion rewards / LP | 既存の Host completion transaction が payout、LP、standing、mission completionをひとつのtransactionで処理する。 | 今回LP awardロジックは変更なし。`MissionDungeonRuntimeTests` と `MissionCompletionTransactionTests` で既存のreward、standing、LP settlementの関連経路を再確認。 |

### Standingの正確性境界

EveJSで実行した `buildMissionRewards` はL1 courierのdecline eventとして corporation `-0.001`, agent `-0.01` rawを返した。実装はその参照curveをlevel 1–5に適用する。現行CCP helpは「agent/corporation standing loss」というルールを裏付けるが、loss magnitudeは与えない。Pinned `.cache/sde` と `MissionStaticReward` modelにはdecline専用raw magnitudeがなく、EveJS側もmissionごとの差を指摘しているため、この値はEveJS fallbackであり、CCPの正確な値としては扱わない。

EveJS `standingRuntime.js:1208-1289,1317-1371` は直接変更に加えてNPC standing relationから派生変化も計算する。Hostのこの修正はtransaction内の直接agent/corporation変更までで、派閥へのderived rippleは追加していない。参照側authority table `/home/user/evejs-261028.1/tools/DatabaseCreator/staticTables/npcStandingsAuthority/data.json` は旧SDE由来で、pinned build 3396210との一致を検証できていない。別buildの係数から派閥損失を捏造しないため、派生分は未実装として残す。

## テスト先行で確認した失敗

- Objective Host RPC test は修正前に `completionStatus` の期待値1に対して0を返した。
- Decline直後のreoffer test はRequest actionが空で失敗した。
- DB付きGetReplayTimestamp test はdecline後にFILETIMEが必要なところ `None` を返した。
- 2回目のdirect decline Host E2Eはagent standing `-0.1` を期待し、修正前は `0` だった。
- important/storyline agentの同じHost E2Eも `-0.1` を期待し、旧basic/nonimportant条件では `0` だった。
- mining curve test は旧ruleの空配列で失敗し、EveJSのdistribution reward scaleに合わせた。
- second-decline Host E2E はrelation historyの `msg` にミッション名 `Encounter` を期待し、旧実装の `Mission declined` で失敗した。decline遷移からoffer contentを返し、履歴と通知に表示名を保存するよう修正。

## 検証状況

- Database-backed tests はユーザー指定の `eve_p4` を使用。Agents store classには同一参照retry、同時retry、store restart後の期限保持を含む。
- 修正後に Agents store 18件、Core dialogue/store/service/objective/rules 69件、Host finder/mission dispatch/read/accept/expiry/journal/completion 21件を実行し、108件成功・0失敗・0 skip。
- 変更した `Eve.Agents`, `Eve.Services.Core`, `Eve.Host` と対応する3 test projectsを Release で個別buildし、各build 0 warning / 0 error。
- EveJSのfull serverを起動したend-to-endシナリオは実行していない。Nodeから実行可能な `buildMissionRewards` helperは実行し、DB付きHost `DoAction` end-to-endとはソース経路・wire stateで比較した。
- V24.01 graphical client、実client trace、実運用のSDE/agent populationによるmission選出は未確認。
- 正確なdecline loss magnitudeとfaction derived standingsは未確認。SDE mission pool由来の実offer選出もこの変更のテスト対象外。
- GitHub Actions main CI run `42720089223` for commit `1f9143d8f814` completed successfully: 0 failed jobs and no failed tests.
