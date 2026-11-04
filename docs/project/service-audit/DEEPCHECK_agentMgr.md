# agentMgr 全クライアント呼び出し・Host深掘り監査

対象はeve-logic V24.01 / build 3396210の全ツリーにある呼び出しと、その返信を読む箇所。クライアント呼び出しはSerenaの全ツリー検索で照合し、EveJS v0.12.9系の実装参照として `/home/user/evejs-261018.1/src` を確認した。

## Host登録と検証範囲

`AgentMissionService.Name` は `agentMgr`。Hostの `AgentRpc.cs:79` がサービスを登録し、`MissionComposition.cs:310` が永続GameStore、BookmarkStore、Research、AgentDialogue、SDE、SpaceSessionsを実サービスへ接続する。バインドIDは接続中のHostサービスで保持し、解放時に削除する。

`AgentMgrReadWireTests.cs` はHostServerのMachoソケット経路を通し、通常Call、bound call、nested `MachoBindObject` を検証する。テストでは外部依存境界の値をコールバックで与えるため、SDE/PostgreSQLを使う本番Compositionの起動やSpaceシミュレーションの通知はこのワイヤテストからは実行していない。

## メソッド別結果

| メソッド | クライアント呼び出し・返信の利用 | Hostの引数・返信、失敗時、状態・通知 | EveJS照合 |
|---|---|---|---|
| `MachoResolveObject` | `carbon/common/script/net/moniker.py:234`。agent monikerのノード解決。 | 引数を使わずProxy node IDを整数で返す。状態変更なし。 | monikerの標準transport処理。 |
| `MachoBindObject` | `moniker.py:142`; `eve/common/script/net/eveMoniker.py:151-157` が `Moniker('agentMgr', agentID)` を作成。 | scalar/tuple/listのagent IDを受ける。未知IDは `CustomInfo` UserError。bound OIDとagent IDを保持し、nested method/args/kwargsもdispatchする。解放時にbound状態を削除。wireテストは通常bound、nested bind、machoVersion kwargsを通過。 | transport bind結果をHostのbound object形で返す。 |
| `GetStaticBlob` | `eve/common/script/sys/strangeStrings.py:13`。AgentString復元時にmessageIDを渡す。 | messageIDを無視し空文字を返す。marshal後は空のPyBytes。状態・通知なし。 | `agentMgrService.js:857-859` の空文字と一致。 |
| `GetAgents` | `eve/client/script/ui/station/agents/agents.py:91,131`; `infosvc.py:2593` はrowsetを `Index('agentID')` にする。 | 引数なし。15列のrowsetをSDEのAgentCatalogから返す。rowsetなしの時も空rowset。状態・通知なし。 | `agentMgrService.js:776-788`。 |
| `GetCompletedCareerAgentIDs` | `agents.py:218-226`。career agent ID群を渡し、結果で完了状態を読む。 | PyList/PyTupleのID群。int/longを受理しID→bool辞書を返す。文字列IDやNoneは対象外。character mission stateを読むだけ。 | `agentMgrService.js:834-842`。 |
| `GetDisabledMissions` | `agents.py:358-363`。無効mission ID一覧をキャッシュ。 | 引数なし、整数IDのlist。静的mission catalog由来。状態・通知なし。 | `agentMgrService.js:844-846`。 |
| `GetMessagesForEpicArcMissions` | `epicArcController.py:67-99`。message categoryとmission IDで参照。 | 引数なし、category→missionID→文字列の辞書。静的mission message map由来。状態・通知なし。 | `agentMgrService.js:887-897`。 |
| `GetMyEpicArcStatus` | `epicArcSvc.py:31`。arc IDとmission IDで状態を見る。 | 引数なし、nested ID辞書。日付はfiletime値またはNone、nameIDはIDまたはNone。永続mission stateを読む。 | `agentMgrService.js:881-885`。 |
| `GetMyJournalDetails` | `journal.py:311,330-348` がmission/research二列を読む。`journal.py:324` はagent bound版を呼ぶ。 | 引数なし。tuple `(mission rows, research rows)`。bound版は当該agentに絞る。MissionStore、GameStore bookmark、research rowsを読む。読み取りのみ。 | `agentMgrService.js:861-871`。 |
| `GetPlausibleMissionIDs` | `evemissions/client/qa_tools.py:95-111`。agent IDを渡して候補IDを読む。 | 先頭引数のint/long agent ID、整数ID listを返す。静的catalog照会。 | `agentMgrService.js:873-879`。 |
| `GetSolarSystemOfAgent` | `agents.py:798-800`; `infosvc.py:2020-2026`。 | 先頭のint/long agent ID。known systemはPyInt、unknownはNone。AgentCatalog/SDE参照。 | `agentMgrService.js:829-832`。 |
| `DoAction` | `agentDialogueWindow.py:427`; `missioncontroller.py:256,261`。actionIDを渡し、会話結果と選択肢を読む。 | positional actionIDまたはNone。AgentDialogueMachineがstandings/access、mission状態を判定し、即時返信またはclient callback後のdeferred返信。conversation/mission書き込みは既存storeとevent announcer経由。無効contextはserver errorで返す。 | `agentMgrService.js:899-1120`。 |
| `GetAgentLocationWrap` | `agentDialogueWindow.py:275`; `agentTransmissionWindow.py:54`; `evemissions/client/ui/agentview.py:95`。 | bound agent、引数なし。station/system/agent-in-spaceのlocation wrap辞書。未知bound agentは `CustomInfo` UserError。読み取りのみ。 | `agentMgrService.js:1166-1172`。 |
| `GetMissionBriefingInfo` | `agents.py:748-750`; `missioncontroller.py:284`。briefing fieldsをmission UIに渡す。 | bound agent、引数なし。HeldMissionが無ければNone。briefing辞書はtitle/content/deadline等を永続missionから構築。読み取りのみ。 | `agentMgrService.js:1122-1129`。 |
| `GetMissionObjectiveInfo` | `agentDialogueWindow.py:221,430`; `missioncontroller.py:251`; `jobboard/client/features/agent_missions/job.py:412,472`。後者は `charID` / `ignoreLocateCheck` kwargsも渡す。 | bound agent。HostとEveJSはpositional/keyword付加値を使わずsession characterとbound agentで解決。HeldMissionなしはNone。objective辞書はmission static/runtime由来。読み取りのみ。 | `agentMgrService.js:1140-1147`。 |
| `GetMissionJournalInfo` | `agents.py:746,755`。一方はcharID/contentID、一方は引数なし。 | 追加引数を無視しsession characterとbound agentで解決。HeldMissionなしはNone。永続mission stateの読み取り。 | `agentMgrService.js:1131-1138`。 |
| `GetMissionKeywords` | `agents.py:632`。contentIDをpositionalで渡しmission text variablesを読む。 | bound agent、文字列または数値contentID。held mission時はagentName/missionName/typeとobjective/reward fields。missionなしでもEveJS同様agentName/missionName/typeを返す。未知agentは空辞書。SDE AgentCatalogから名前を読む。読み取りのみ。追加したwire回帰assertでcontentIDを確認。 | handler `agentMgrService.js:1149-1157`; fallbackはruntime `agentMissionRuntime.js:7388-7406,4647-4723`。 |
| `GetStandingGainsForMission` | `evemissions/client/missioncontroller.py:223`。mission IDを渡しowner IDごとのgainを読む。 | 先頭のint/long mission ID。ownerID→PyReal辞書。静的gainとcharacter standingから算出。欠損時は空辞書。読み取りのみ。 | `agentMgrService.js:1183-1192`。 |
| `RemoveOfferFromJournal` | `agents.py:781-782`; `missionentry.py:74`。返信値は使わずjournalからofferを外す。 | bound agent、引数なし。MissionStoreでcommit後None。refusalはUserError化。store変更をAgentMissionAnnouncerが `OnAgentMissionChange` で通知する。 | `agentMgrService.js:1159-1164`。 |
| `GetDungeonShipRestrictions` | `activeMissionController.py:118-119`; `evedungeons/client/util.py:26-59`; `explorationscanner/signalTracker.py:210`。利用側はallowed/restricted type ID群を使う。 | dungeonID int/long、optional gateID int/long/None。dungeons connections、typeLists、types/groupsのpinned SDEを使ってKeyValのallowedShipTypes/restrictedShipTypes/nonDefaultShipRestrictionsを構築。connectionなし/unknown dungeonはNone。状態・通知なし。wire testはoptional None gate shapeを確認。 | `agentMgrService.js:1194-1201`; resolver `dungeonShipRestrictionResolver.js:89-143`。 |
| `GetEntryPoint` | `epicArcAgentContentPiece.py:128`; `menucheckers/bookmarkChecker.py:132`。Epic Arc UIはfalseyを許容し、bookmark checkerは座標として使う。 | bound agent、引数なし。永続missionPositionが揃えば3要素PyReal list、無ければNone。wire testが座標listを確認。読み取りのみ。 | `agentMgrService.js:1203-1211`。 |
| `GetInfoServiceDetails` | `infosvc.py:1998-2053`。stationID/level/services/incompatibleを読み、UserError/RuntimeErrorを捕捉。 | bound agent、引数なし。KeyValにagentID/stationID/level/services/incompatible。mission availabilityはAgentAccessRuleとmission state、locatorはagent data、researchはResearchPort/ProjectStoreから組み立てる。未知agentはNone。読み取りのみ。 | `agentMgrService.js:1174-1181`; runtime `agentMissionRuntime.js:7315-7342`。 |
| `GetReplayTimestamp` | `agents.py:305-312`; `dockedUI/agents.py:92-98`。呼び出し側はcharacterIDを渡す。 | bound agent。追加characterIDはsession値を優先し無視。persisted declineTimersByAgentIDのfiletimeをPyLong、未設定ならNone。読み取りのみ。wire testはPyLong値を確認。 | handler `agentMgrService.js:1401-1406`; runtime `agentMissionRuntime.js:7429-7432`。 |
| `GotoLocation` | `movementFunctions.py:401`。実引数はlocationType, locationNumber, referringAgentID。 | roleはarg0、referringAgentIDはarg2。valid mission bookmark/locationのitemはFollowBall、pointはGotoDirection。返信None。SpaceSessionsが実sim commandを送り、移動関連通知はsim更新経由。無効targetはNone。 | `agentMgrService.js:1213-1237`。 |
| `WarpToLocation` | `movementFunctions.py:415,429`。実引数はlocationType, locationNumber, warpRange, fleet, referringAgentID, formation settings。 | role arg0/range arg2/referringAgentID arg4を読む。locationNumber/fleet/formationはEveJS同様dispatch対象にしない。item/pointをSpaceSessions WarpAsync/WarpToPointAsyncへ渡す。返信None、SpaceSessionsの既存UserError/通知順を使う。 | `agentMgrService.js:1239-1306`。 |
| `WarpToAgentInSpace` | `agencyNew/contentPieces/agentContentPiece.py:305`。bound agentへのwarp action。 | bound agent IDを使い、agentがspaceにいる場合だけSDE solar system centerとInSpaceAgentPlacementから座標を作りSpaceSessions WarpToPointAsyncへ渡す。station agent/unknown agentはNone。返信None。wire testはbound ID dispatchを確認。 | `agentMgrService.js:1351-1399` と異なる箇所。EveJSのlocation wrapに座標がない時はagentinspace targetをNoneで終えるが、Hostは同じAgentCatalogのSDE in-space placementから座標を計算する。クライアントがこのAPIを呼ぶ根拠は上記line 305。実sim通知とGUI warpは今回動かしていない。 |
| `IsCheatingWithAgent` | `agents.py:548-549`; QA表示は `qa_tools.py:35`。 | bound agent、引数なし。PyBool.False。状態・通知なし。 | `agentMgrService.js:1408-1410`。 |
| `ShouldAlwaysAllowReplay` | `agents.py:551-552`; QA表示は `qa_tools.py:36`。 | bound agent、引数なし。PyBool.False。状態・通知なし。 | `agentMgrService.js:1412-1414`。 |

`GetAgentByID` (`agents.py:113-131`) は `GetAgents()` rowsetを使うクライアント内indexでRPCではない。`GetAgentStaticInfo` (`notificationUtil.py:84-98`) はnon-client branchだけで呼ばれるためwireメソッドに含めない。

## 実行した確認と範囲

- Host projectのbuildは成功。Service、Host Compositionの型検査を含む。
- focused Host wire test `Agent_mgr_read_rpcs_are_registered_on_the_host_and_return_client_consumed_shapes` は成功（1 passed、0 skipped）。bound/unbound、machoVersion kwargs、nested bind、int/long、None gate、浮動小数warp range、各追加RPCの返信とdispatchを通した。
- PostgreSQLなしでHost.Tests全体を実行した結果は820件中492 passed、227 skipped、101 failed。DB lane未設定時にDBを要求するテストが失敗し、skipも発生するため、全体は成功していない。個別に確認した `Real_host_registry_methods_are_enumerable` は1424件を期待するが、DBなしCompositionで列挙された241件との差で失敗した。
- 指定に従いPostgreSQLを使っていない。実GUI clientと実Space simulationも起動していない。
