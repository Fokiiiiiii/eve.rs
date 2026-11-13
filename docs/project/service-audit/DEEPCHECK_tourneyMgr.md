# tourneyMgr 全クライアント呼び出し・Host深掘り監査

> Historical snapshot. The current call inventory, verified provider wiring, and test results are in [implement-tourneymgr.md](../implement-tourneymgr.md). In particular, the final section below predates the persisted handlers and later registered-Host tests; do not treat its “未実装” statement as current status.

対象は eve-logic V24.01 / build 3396210 全ツリー。CSVのtourneyMgr行を確認し、5つのremote service RPCと開発者ツールのbound-object呼び出しを分けて追った。

## 登録と共通wire

CoreServices.cs:79 が TourneyMgrService を構成リストへ追加する。サービスは UnportedFeatureRpcServices.cs:551-700 で登録され、任意の TourneyQueryCallback がある場合だけ実データを読み、未接続またはnullの場合はスキーマに沿った空データを返す。V24の通常のservice callは positional args とtransport用 machoVersion=1 kwargs。5つの読み取りRPCにアプリkwargsはない。bound-object呼び出しはeve.devtoolsのTournament referee / camera機能に限られる。

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

これらのbound routesはHostでは実装されている。bound objectは `UnportedFeatureRpcServices.cs:577-617` の `MachoBindObject` が作成し、各methodは `dotnet/src/Eve.Services.Core/TourneyMgrService.Actions.cs:8-28` と `TourneyMgrService.Camera.cs:11-59` に定義される（EveJS v0.12.9側の対応有無は本監査では未確認）。clientが呼ぶ操作はmatch作成、pilot/ship検査、bans、teleport、match state transition、referee membershipを変えるため、空replyや常時成功を追加すると誤った成功になる。match状態の永続化は `dotnet/src/Eve.Host/Persistence/Tournaments/PersistenceTournamentMatches.Actions.cs:123-267` と `PersistenceTournamentMatches.SpaceActions.cs:14-292` が担う。

## EveJS照合・wire検証

EveJS v0.12.9 server/src/services/tourneyMgrService.js は5つのread RPCと別のlegacy BanShip / ExecuteMemberChangeを持つが、上記bound methodsの業務実装は見つからない。HostのGetAllCurrentSeriesMatchDetailsをdictに、QueryTournamentDetailsをEveJSと同じ5 field mappingにした。TourneyMgrHostWireTests.cs はGetTourneySetup, GetPotentialMatches, GetAllCurrentSeriesMatchDetails, QueryTournamentDetails, QueryShipListをHost dispatch経由で検査する。

## 確認した範囲

- Tournament_setup_and_matches_use_the_mappings_consumed_by_the_client_through_host_dispatch と Tournament_ship_list_uses_the_dictionary_field_consumed_by_the_client_through_host_dispatch: 2 passed、0 skipped。
- EveJSの実service runtime、実GUI tournament tool、production query callbackによる試合data・notifications・再起動後の状態は確認していない。bound-object機能は未実装として残る。


## 引数型
Setupは引数なし、他のread RPCはtournament IDを1 positional argで受ける。Camera/bound methodsも全てpositionalで、CreateMatchはmatch parametersを位置順に送りexternalRulesetIDはNoneになり得る。唯一のアプリkwargs callはStartAutobanningで、redCharID, blueCharID, totalNumBans。Python integer IDはwire上PyInt/PyLongになり得るためHostのPositive parserは両方を受ける。clientからmachoVersion=1もtransport kwargsに乗るがmethod引数ではない。

## 現在のHost実装・wire検証（2026-11-02）

以下はこの監査の初期スナップショットを更新する。CSVではtourneyMgr 41 callsite rows / 36 distinct methods がすべて IMPLEMENTED-TESTED になった。実証は登録Host serviceのmarshal/encoded dispatch、PostgreSQL、およびpinned Rust simulatorを通したfocused testsによるもので、GUI実機やproduction tournament runtimeを通した確認ではない。read/query defaults の空データ契約も含むため、「tested」は全production tournament stateや全遷移が網羅済みという意味ではない。

`GetFancyDetails` の `Tournament_fancy_registered_wire_reads_live_two_team_metrics` は、登録encoded RPCで初回Noneの後に両チームの6 numeric metricsを返し、live online laser/crystal ships、positive EHP/potential DPS、実hit後のred actual > 0 / blue=0 / idle=0 を確認した。locked roster と state 3 はseedした fixture で、death と active-match repair は未検証。別の stale-running 競合については deterministic fail-before の後に ordering/token 修正を行い同testを通したほか、actual fancy/RemoteACK と旧A cleanupがBを消さない回帰をそれぞれ通した。これらの後続テストと以前の intermittent 結果との因果関係は立証されていない。
