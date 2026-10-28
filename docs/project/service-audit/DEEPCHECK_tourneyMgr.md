# tourneyMgr 全クライアント呼び出し・Host深掘り監査

対象は eve-logic V24.01 / build 3396210 全ツリー。CSVのtourneyMgr行を確認し、5つのremote service RPCと開発者ツールのbound-object呼び出しを分けて追った。

## 登録と共通wire

CoreServices.cs:72 が TourneyMgrService を構成リストへ追加する。サービスは UnportedFeatureRpcServices.cs:524-550 で登録され、任意の TourneyQueryCallback がある場合だけ実データを読み、未接続またはnullの場合はスキーマに沿った空データを返す。V24の通常のservice callは positional args とtransport用 machoVersion=1 kwargs。5つの読み取りRPCにアプリkwargsはない。bound-object呼び出しはeve.devtoolsのTournament referee / camera機能に限られる。

## 読み取りRPC

| メソッド | 全client call / 消費フィールド | Host / EveJS / 結果 |
|---|---|---|
| GetTourneySetup | tournamentRefereeTools.py:537-540。結果を3値に分解し、tournamentsを.iteritems、tourneySystemsをキーと値に、activeSystemsを含むかで使う (:540-563)。 | Hostは(dict, dict, list)。EveJS v0.12.9 tourneyMgrService.jsの3要素replyと合う。callbackなしでは各コンテナを空で返す。 |
| GetPotentialMatches | tournamentRefereeTools.py:551, 574-575。seriesIDをキーにし、redTeam, blueTeam, systemを読む。 | Hostは空dict fallback。EveJSは同じ形。 |
| GetAllCurrentSeriesMatchDetails | tournamentRefereeTools.py:576-580。seriesIDで引き、[0]を現在match index、[1]をシリーズmatch数として表示。 | Hostは以前listを返していたが、callerのseriesID参照が動かない。Hostをdictに変更し、EveJSの空objectと同じwire typeにした。 |
| QueryTournamentDetails | tournamentRefereeTools.py:589-598。allowedShipListをsort、maximumPointsMatchをpoint selectorへ、matchLength/overtimeLengthを時計設定へ、externalRulesetIDをmatch作成へ渡す。 | Hostは全5 fieldを含むmappingを返す。callbackなしは allowedShipList=[], maximumPointsMatch=0, matchLength=600, overtimeLength=300, externalRulesetID=None。EveJS v0.12.9の既定値と一致。 |
| QueryShipList | tourneyUISvc.py:30-31。reply['allowedShipList']をban UIへ渡す。 | HostはallowedShipList=[]を持つdict、EveJSもdict fieldを返す。 |

これらは読み取りだけで、永続変更や通知はない。Hostにquery callbackを接続していない実行構成では、空値は空のトーナメントデータを表す。入力値が非正のIDならPositiveはnullへ正規化する。これらのread RPCにアプリUserErrorは定義されていない。

## bound-object RPC: 全呼び出しとreply消費

Camera client form_tournament.py:130-141 はGetActiveMatchForCameraのmappingからmatchID, teamOneName, teamTwoName, nebulaIDを読む。FetchTeams (:243-263) はGetMatchSystemとGetTeamMembersを呼び、最初はsystem IDとの比較、後者は2チーム分のpilot tuple [characterID, shipTypeID, shipID, points] を読む。StartFancyUIClient/UpdateFancyBars form_tournament.py:333,615-620 はGetFancyDetailsのtuple index 0 (6項目のfleet集計値) とindex 3のred/blue ban listを使う。

tournamentRefereeTools.pyのbound callsは次の通り。実データを返す必要がある呼び出し以外は、clientはreplyを読み取らない。

| メソッド | client callsite | reply消費 |
|---|---|---|
| AbortCountdown | :1140 | bool stopped。Trueならcountdown表示を戻す。 |
| AddPlayer | :1268 | なし |
| BanShipType | :1039 | 返値をcurBansへ受けるが以降で使わない |
| CleanupGrid | :1163 | なし |
| CompletePregameChecks | :1080 | なし |
| CreateMatch | :820 | 3要素 (matchID, beaconIDs, captainIDs)。:824-833で全要素を読む |
| FinalizeBans | :1034 | なし |
| IsOtherMatchInProgress | :1055 | bool。TrueならTournamentOtherMatchInSystemInProgressをclient側で送出 |
| LockMemberList / UnlockMemberList | :1255 / :1259 | なし |
| MatchCompleted | :1150 | なし |
| OverrideError | :1186, :1199 | なし |
| OverrideFleetBossID | :1178 | なし |
| OverrideLockRestrict | :1043, :1119 | なし |
| OverrideMoveRestrict | :1051 | なし |
| OverrideWarpRestrict | :1047, :1120, :1125 | なし |
| RemovePlayer | :1208 | なし |
| RemoveSelfFromReferees | :1307 | なし |
| ResetMatch | :1300 | なし |
| ReturnAllPlayers / ReturnTeamPlayers | :1157 / :1282 | なし |
| StartAutobanning | :1030 | なし。redCharID, blueCharID, totalNumBansはkwargs |
| StartCountdown | :1130 | なし |
| TeleportPlayers | :1278 | なし |
| TeleportRefHome | :1168 | なし |
| TeleportToArena / TeleportToStation | :1287 / :1292 | なし |
| UnBanShipType | :915 | なし |
| UpdateFleetDetails | :1250 | なし |

これらのbound routesはHostにもEveJS v0.12.9にも実装がない。HostでRPC呼び出しはmethod未登録エラーとなる。clientが呼ぶ操作はmatch作成、pilot/ship検査、bans、teleport、match state transition、referee membershipを変えるため、空replyや常時成功を追加すると誤った成功になる。DB状態・権限モデル・通知を持つ実装が必要で、今回の既存データでは安全に再現できない。特にCreateMatchの3要素やcamera UIのmappingに適合する永続providerもない。

## EveJS照合・wire検証

EveJS v0.12.9 server/src/services/tourneyMgrService.js は5つのread RPCと別のlegacy BanShip / ExecuteMemberChangeを持つが、上記bound methodsの業務実装は見つからない。HostのGetAllCurrentSeriesMatchDetailsをdictに、QueryTournamentDetailsをEveJSと同じ5 field mappingにした。TourneyMgrHostWireTests.cs はGetTourneySetup, GetPotentialMatches, GetAllCurrentSeriesMatchDetails, QueryTournamentDetails, QueryShipListをHost dispatch経由で検査する。

## 確認した範囲

- Tournament_setup_and_matches_use_the_mappings_consumed_by_the_client_through_host_dispatch と Tournament_ship_list_uses_the_dictionary_field_consumed_by_the_client_through_host_dispatch: 2 passed、0 skipped。
- EveJSの実service runtime、実GUI tournament tool、production query callbackによる試合data・notifications・再起動後の状態は確認していない。bound-object機能は未実装として残る。


## 引数型
Setupは引数なし、他のread RPCはtournament IDを1 positional argで受ける。Camera/bound methodsも全てpositionalで、CreateMatchはmatch parametersを位置順に送りexternalRulesetIDはNoneになり得る。唯一のアプリkwargs callはStartAutobanningで、redCharID, blueCharID, totalNumBans。Python integer IDはwire上PyInt/PyLongになり得るためHostのPositive parserは両方を受ける。clientからmachoVersion=1もtransport kwargsに乗るがmethod引数ではない。
