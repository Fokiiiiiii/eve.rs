# Journal service deep check

## 結論と境界

V24.01 に Host の RPC service `journal` はない。`eve/client/script/ui/shared/neocom/journal.py` の `JournalSvc` は UI 側のキャッシュ兼 facade で、実際の呼び出し先は `agentMgr`、`dungeonExplorationMgr`、incursion UI の `map` RPC である。agent moniker も `agentMgr` の bound object である。対象メソッドの全 client 呼び出しを検索し、呼び出し先、引数、消費される形、状態、通知を以下に記録する。

## Client が呼ぶ JournalSvc の全メソッド

| JournalSvc メソッド / client caller | 実際の経路、結果と状態 |
|---|---|
| `GetMyAgentJournalDetails` — `neocom/journal.py:675`, `industry/browserResearch.py:36-37`, `dockedUI/agents.py:74`, `jobboard/client/features/agent_missions/provider.py:67`。journal 内でもキャッシュ更新に使用（`neocom/journal.py:269-329`）。 | 初回は `agentMgr.GetMyJournalDetails()`、以後 `OnAgentMissionChange` で無効化された agent の journal を moniker `GetMyJournalDetails()` で取り直す。戻りは `(missionRows, researchRows)`。呼出側は mission row の10要素と research row を読む（`neocom/journal.py:299-329`, `industry/browserResearch.py:36-37`）。永続ミッション状態、bookmark、research read model から読み、RPC 自体は書き込まない。character session がなければ Host は `([], [])`。 |
| `GetMyAgentJournalBookmarks` — `addressBook/bookmarksPanel.py:132`, `bookmarks/bookmarkSvc.py:122`, `infoPanels/listSurroundingsBtn.py:316`。 | 上記 mission rows の bookmark を `[(missionName, bookmarks, agentID), ...]` に射影。独立 RPC や別の状態所有者はない。 |
| `GetActiveMissions` — `agencyNew/contentProviders/contentProviderStorylineAgents.py:36-37`。 | mission rows から `ActiveMissionController` のリストを構築する UI 側 helper（`neocom/journal.py:329-351`）。agent ごとの状態は同じ永続 journal read。 |
| `GetActiveMissionForAgent(agentID)` — `agencyNew/contentPieces/agentContentPiece.py:51-52`, `userentry.py:777-781`, `epicArcs/epicArcController.py:123-130`。 | `GetActiveMissions` から一致 agent を返し、該当なしなら Python `None`。追加 RPC なし。 |
| `GetAgentMissions` — `evemissions/client/mission_finder.py:23-25`。 | `GetMyAgentJournalDetails()[0]` の missionRows。 |
| `IsMissionActiveWithAgent(agentID)` — `station/agents/agentUtil.py:20-21,46-47`。 | mission row の agent ID を照合し bool を返す。 |
| `CheckUndock(stationID)` — `station/undockQuestions.py:107-113`。 | Accepted mission の source bookmark と station を照合し、`agents.CheckCourierCargo` が false の場合のみ確認 dialog を表示（`neocom/journal.py:394-406`）。この確認は cached mission row で source を調べるが、cargo は moniker の新しい mission-info read で調べる。 |
| `GetEscalations(force=0)` — `agencyNew/contentProviders/contentProviderEscalations.py:56-59`, `jobboard/client/features/dungeons/escalation_sites/provider.py:76-77`。 | cache miss または force 時に `dungeonExplorationMgr.GetMyEscalatingPathDetails()` を呼び、rowset を返す。成功時、client 自身が `OnEscalationsDataUpdated` を scatter（`neocom/journal.py:207-215`）。 |
| `ShowIncursionTab(...)` — `incursionJournal.py:86`。 | JournalWindow の tab を開く UI helper。tab の map RPC 呼び出しと shape は [DEEPCHECK_map.md](DEEPCHECK_map.md) を参照。Journal service RPC ではない。 |

`GetActiveStorylineMissions` や `Refresh` は JournalSvc にあるが、whole-client direct-call 検索で呼び出し元は見つからなかったため、上表の呼び出し API には含めていない。journal.py の隣の `planetMgr.DeleteLaunch`（`neocom/journal.py:463`）は惑星 UI 操作で、Journal RPC ではない。

## Host RPC ごとのチェック

### agentMgr.GetMyJournalDetails と bound/moniker variant

- **登録 / route:** `CharacterServiceList.cs:67` が `AgentRpc()` をサービス一覧へ含め、`AgentRpc.cs:77-82` が `agentMissionService` を返す。構成時に `CharacterServicesComposition.cs:104` から mission composition が初期化される。service 名は `agentMgr`（`AgentMissionService.cs:66`）。`MachoBindObject(agentID)` が bound ID を保持し、同じ `ReadAsync` が unbound と bound の `GetMyJournalDetails` を処理する。bound 時は mission と research row を agent ID で絞る（同ファイル `ReadAsync`）。
- **引数:** client は通常 no positional args。全 AgentMgr socket テストは `machoVersion` kwargs を通す。moniker は `Agents.GetAgentMoniker` から `GetMyJournalDetails()` を呼ぶ（`eve/common/script/net/eveMoniker.py:151-157`）。bound agent ID の `PyInt` / `PyLong` は Host wire coverage に含む。
- **reply:** 2-tuple of lists。mission row は10要素で client が index 0, 4, 6 などを使う（`neocom/journal.py:299-329`）。research row は `browserResearch.py:36-37` がそのまま受け取る。host wire test は unbound / bound の mission、bookmark、research を検査。
- **error / state / notification:** character ID が無い場合は空 tuple-of-lists。callback / storage が失敗した際、`ReadAsync` はその例外を握りつぶさず、client 側の初回読取にも fallback はない（`neocom/journal.py:299-306`）。ミッションは persisted game-store read、bookmark と research は各 read model から読み取り、成功した call で mutation はしない。mission transition の `OnAgentMissionChange` は `AgentMissionAnnouncer` が送信し、JournalSvc が cache を agent 単位で失効する（`AgentMissionStoreRecords.cs:76-91`, `neocom/journal.py:168-184`）。純粋な read の後に server notification は期待されていない。

### agentMgr.GetMissionJournalInfo (bound agent moniker)

- **全 client 呼び出し / args:** `Agents.GetMissionInfo(agentID, charID=None, contentID=None)` は `GetMissionJournalInfo(charID, contentID)` を呼ぶ（`station/agents/agents.py:745-747`）。mission window は contentID 付き（`missionDetailsWindow.py:40-51`）、active mission は contentID 付きまたは agent のみ（`activeMissionController.py:75-76,92-100`）。配送確認は moniker を直接、引数なしで呼ぶ（`station/agents/agents.py:754-763`）。Host wire test は no args、`(None,None)`、`(PyInt(charID),PyLong(contentID))`、`machoVersion` kwargs を通す。handler はメソッド引数を参照しない。EveJS も session charID と bound agentID を使って同じく引数を無視する（`agentMgrService.js:1131-1138`）。
- **reply:** `None` または dict。UI が `missionNameID`, `contentID`, `briefingTextID`, `missionImage`, `expirationTime`, `missionState`, `objectives`, `bookmarks`, `iconID` を読む（`station/agents/agentDialogueUtil.py:450-475`）。agent moniker 詳細 UI は `objectives.dungeons` を参照（`activeMissionController.py:92-100`）。配送確認は `objectives.objectives` 内の `transport` tuple の cargo dict の `hasCargo` を読む（`station/agents/agents.py:754-763`）。Host socket では `transport` discriminator は `PyBytes` であり、wire assertion もこの型を固定する。jobboard も `contentID` と `missionState` を読む（`jobboard/client/features/agent_missions/provider.py:35-49`）。
- **欠陥と修正:** Host が top-level と nested objective の `contentID` を常に `None` にしていた。client は `contentID` を `ProcessMessage` に渡し mission text を組み立てる（`agentDialogueUtil.py:450-451`）。Host はこれを persisted `AgentMissionContent.MissionId` から出す。EveJS は mission record の ID が static mission ID と一致する場合に使い、そうでなければ resolved client mission ID を使う（`agentMissionRuntime.js:1945-1963`）。Host の typed record は static mission ID が正規 ID であるというモデル対応のため、top-level と nested 両方に同値を返すよう修正した。wire test は両フィールドを検査。
- **cargo と永続状態:** Host は `HeldMissionView` を persisted AgentMissionStore + matching SDE から作り（`MissionComposition.cs:423-443`）、cargo は persisted owned-item rows を読む（同ファイル `CourierObjectiveAsync`）。`hasCargo` は active ship の `flagCargo=5` stack 合計、または目的地 station の `flagHangar=4` stack 合計が必要量に達した場合に限り true。pickup Hangar や別 ship の cargo は満たさない。active `shipid` は login session に persisted character.ShipId から設定される（`CharUnboundMgrService.cs:350`）。これは EVE University の [Distribution missions](https://wiki.eveuniversity.org/Distribution_missions) 配送条件と EveJS の `getCourierProgress`（`agentMissionRuntime.js:3031-3082`）に整合する。wire test に加え helper test は source hangar、別 ship、数量不足、active ship stack 合計、drop-off hangar を区別する。read 後 cargo 移動通知は不要で、次回 bound read は inventory を再読する。mission state transition は上記 `OnAgentMissionChange` で通知される。
- **error:** session charID / bound agent ID が無い、reader が設定されていない、または held mission が無い場合は EveJS と同じ `None`（`AgentMissionService.cs:153-163`）。有効な mission call に対して catch-all UserError は返さず、storage/reader の予期しない例外は Host dispatcher へ伝播する。wire test は mission 無しの `None` を確認。

### agentMgr.GetMissionObjectiveInfo (shared objective serializer)

`MissionViews.Objective` is also the reply used by this adjacent bound RPC, so its client surface was checked after adding `contentID`. Client calls include no args from `agentDialogueWindow.py:221` and `evemissions/client/missioncontroller.py:251`, positional `(session.charid, contentID)` from `agentDialogueWindow.py:430`, and keyword `ignoreLocateCheck=True` / `charID=..., ignoreLocateCheck=True` from `jobboard/client/features/agent_missions/job.py:412,472`. Host's bound reader ignores those optional selectors and uses session character plus bound agent, matching the existing EveJS pattern. It returns the same objective dict consumed by `agentDialogueUtil.GetMissionObjectiveHTML`; no held mission returns `None`. The new Host wire assertion verifies the objective's top-level `contentID` and list shape for no-arg and keyword forms including `machoVersion`. State and transition notifications are the same persisted held-mission read and `OnAgentMissionChange` path above.

### dungeonExplorationMgr.GetMyEscalatingPathDetails

- **登録 / shape / args:** `CoreServices.Create` が service を登録（`CoreServices.cs:63`）。V24.01 journal は args なし（`neocom/journal.py:210-213`）。Host wire test は Host socket を通し `machoVersion` kwargs、Rowset object、6列 `instanceID,dungeon,destDungeon,srcDungeon,expiryTime,journalEntryTemplateID` を検査。余分な args / kwargs は `DUNGEON_EXPLORATION_ARGUMENTS_INVALID` UserError（`UnportedFeatureRpcServices.cs:59-69`）。有効な no-arg 呼び出しでは UserError なし。
- **状態と未接続 provider:** service は optional `readDetails` callback の値を ObjectCaching 経由で返すが、production wiring に callback は渡されていない。callback がないと常に空 rowset fallback となる（`UnportedFeatureRpcServices.cs:50-78`, `CoreServices.cs:10-26,63`）。TODO も real dungeon provider の接続を残件としている（`docs/project/TODO.md:173`）。よって route/shape は検証済みだが、escalation records の persistence/read-through、状態変化に続く `OnEscalatingPathChange` は未実装で、空 fallback を実データ対応とは判定しない。client は読み取り後にローカル `OnEscalationsDataUpdated` を scatter する（`journal.py:210-215`）。EveJS も現在 empty rowset を返すため wire shape は一致するが、実データ可視性は一致判定できない。

## テストと残件

追加・更新した Host wire checks は agentMgr の unbound/bound journal reply、mission-info valid call shapes / no-mission reply / contentID / courier hasCargo、escalation rowset と machoVersion をカバーする。cargo 判定の focused test は状態別入力を検査する。focused Host.Tests は 3 passed / 0 skipped。wire fixture は service dispatch と payload を検査するが、production の PostgreSQL-backed mission/inventory readers はこの job の DB 禁止条件により通していない。全 Host.Tests shard も同じ理由で実行していない。これらは headless Host tests であり graphical V24.01 client 検証ではない。

エスカレーションの production provider と永続状態はこの変更で追加していない。該当 gameplay の生成イベント・state owner が既存 Host にないため、empty rowset だけを有効状態として扱わない。この残件を解消するには expedition/escalation の authoritative lifecycle を実装し、同じ owner からこの callback を読む必要がある。
