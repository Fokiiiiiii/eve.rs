# Agent audit fixes (r14)

監査対象 `/home/user/reports/90-audit-eve-agents-r14.md` の4件を、eve.rs、V24.01 client 全体、EveJS v0.12.9 の実装で照合した。4件とも根本原因は実在した。修正前に追加した回帰テストで各不具合を再現し、その後 Host／PostgreSQL 経由で修正を確認した。監査に挙がった `OnMissionsUpdated` 欠落は、期限切れ処理については互換性バグではないと確認したため、期限処理には追加していない。

## P1: Research Browser に保存済み研究 project が出ない

Host の `GetMyJournalDetails` が GameStore の未書込 JSON を読んでいた。V24.01 の `browserResearch.py:36-63` は `journal.GetMyAgentJournalDetails()[1]` を取り、各行を `(agentID, skillTypeID, pointsPerDay, currentPoints, level, quality, stationID)` として展開する。全 client callsite では `agents.py:715-718` の `OnDatacoreBought` が `OnAgentMissionChange` を scatter し、Research Browser はそのイベントで行を再読込する (`browserResearch.py:76`)。EveJS `server/src/services/agent/researchRuntime.js:902-926` も project state から同じ7列を作る。

`AgentResearchJournalHostTests.Agent_mgr_journal_projects_the_persisted_research_project_through_host_registration` を先に追加した際、DB に project があっても Host 応答の研究行が空で失敗した。Host reader を `ResearchProjectStore.ReadAllAsync`、SDE agent metadata、`AccruedPoints(project, now)` 起点へ変更。修正後の登録済み RPC テストは7列の型・値を確認し、store 再生成後も同じ行を返すことを確認した。

## P1: offer／accepted mission の期限が永続化されず、期限切れ遷移もない

V24.01 `eve/client/script/ui/station/agents/agentUtil.py:153-194` と `agentDialogueUtil.py:460-466` は offer と受諾済み mission の expiry を表示する。mission journal、dialogue、mission detail window は `OnAgentMissionChange` を受けて更新する (`eve/client/script/ui/shared/neocom/journal.py:168-183`, `eve/client/script/ui/station/agents/agentDialogueWindow.py:86`, `missionDetailsWindow.py:69`)。mission tracker は別の `OnAgentMissionChanged` を受け、accepted agent list と目的を更新する (`missions/client/missionObjectivesTracker.py:158-180`)。EveJS の既定期限は7日 (`server/src/services/agent/missionRuntimeState.js:23-26`)、offer と accept の期限設定は `agentMissionRuntime.js:5156-5208, 6485-6493`、期限切れの状態遷移と `offer_expired` 通知は `:4398-4468`。

期限が `null` のままという先行テストを追加し、offer と accepted の期限・scheduler job が欠けることを確認した。修正では期限を migration 0007 で保存し、offer／accept ごとに期限を設定、DurableScheduler job を遷移と同じ DB transaction で置換・取消する。起動時は既存 active record の job を復元し、期限を backfill する。job は行ロック下で state と exact deadline を再検査し、期限切れ遷移と outbox event を同じ transaction に保存する。登録済み agentMgr RPC の Host テストで accept 後の7日 deadline、host/scheduler の再生成、再起動後の expiry 1回、state、wire notification を確認した。

監査に併記された「expiry 時に `OnMissionsUpdated` が送られない」は、期限処理の欠陥という結論には至らなかった。EveJS `expireMissionIfNeeded` (`agentMissionRuntime.js:4398-4468`) は `OnMissionsUpdated` を呼ばず、`OnAgentMissionChange("offer_expired", agentID)` のみを送る。V24 の `OnAgentMissionChanged` は modified 以外の mission event で tracker info を無効化し、mission list を再計算する (`missions/client/missionObjectivesTracker.py:158-180`)。したがって追加 notification は行っていない。

期限機能とは別に、SDE mission pool から offer を作る live path はまだ無く、`docs/project/TODO.md` で OPEN のまま残す。

## P2: 同時 locator accept で fee が二重徴収される

V24.01 `eve/client/script/ui/station/agents/agents.py:675-684` の `isExecutingAgentAction` は通常 UI の並行操作を抑止するが、サーバーは重複 RPC を拒否していなかった。locator dialogue が pending target を読み、その後それぞれ別 transaction で locator fee、located state、outbox を書くため競合時に両方通っていた。EveJS の agent service 全体では locator button の metadata (`agentMissionRuntime.js:6178-6194`, `agentMgrService.js:799,816`) だけが見つかり、locator action の実装はなく、機能的な comparator は存在しない。

実際の Host `agentMgr` bound-object `DoAction` を二本同時送る PostgreSQL テストを先に追加した。修正前は残高が `1,000,000` から `990,000` へ落ち、5,000 ISK を二回徴収した。修正では transaction 内で pending target と cooldown を比較して claim し、wallet debit、cooldown、通知 outbox を同じ transaction に置いた。修正後は二本の RPC に対し、5,000 ISK の debit、journal row、outbox event が各1件となり、勝者後に stale action を返さないことを確認。追加で insufficient-funds ケースが pending target を保持し、debit・通知を作らないことを確認した。

## P2: mission accept の post-commit callback が重複し、crash 後の再開もできない

V24.01 の agent action は `agents.py:675-684` から `DoAction` を行う。EveJS `acceptMission` は新規遷移時だけ `acceptedNow` を返し (`agentMissionRuntime.js:6427-6499`)、caller はその場合に限って `OnAgentMissionChange("accepted", agentID)` を送る (`:8152-8181`)。eve.rs は store の null 結果を捨て、post-accept callback を呼んでいた。

同じ Offered state を見た2回の accept が callback を二重に呼ぶ PostgreSQL test を先に追加し、修正前に callback count `Expected=1, Actual=2` を確認。callback は `AcceptAsync` の非 null transition に限定した。さらに accept transition と同 transaction に `agents.mission.accepted` outbox event を書き、受け側は event の再配信に備えて storyline counter を acceptance reference receipt で冪等化した。Host wire test は受諾を登録済み RPC で行い、effect subscriber が処理する前に host/event bus を再生成、未処理 event の replay で effect が1回だけ永続化することを確認した。

## 実行した確認と未確認

確認済み: `eve_p4` 上の PostgreSQL-backed test。Host focused run は6件 pass / 0 failed / 0 skipped。その他、`AgentMissionDispatchTests` 3件、`AgentMissionStoreTests` 15件、core agent / research / mission 73件、agents store/storyline/research 20件を実行し、全て pass・skip 0。修正前の locator concurrency と callback concurrency のテストはそれぞれ二重処理を検出して失敗した後、修正後に通過した。

未確認: V24.01 graphical client での実動作、wire capture の取得、EveJS server の end-to-end 実行比較。コード上の callsite／通知形状比較と Host の wire assertions は行った。locator action はEveJSに executable implementation が見つからず、比較対象がない。mission expiry に伴う EveJS 固有の bookmark cleanup／dungeon purge と、同じ永続副作用を持つHost integration はこの監査修正テストでは確認していない。
