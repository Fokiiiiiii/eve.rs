# standing / standingMgr 全クライアント呼び出し監査

対象はV24.01 build 3396210の `/home/user/eve-logic` 全体。全ツリーの `standingMgr`、`standing2`、`GetService('standing')`、`standingSvc` 呼び出しを検索し、返信を読む箇所まで追った。

## 名前、登録、変種

クライアントの `standing` は `eve/client/script/ui/services/standingsvc.py:14-21` のローカルUIサービスで、Host RPC名ではない。同サービスが `RemoteSvc('standingMgr')` を作るのは同ファイル113行。実際に呼ぶRPCは下表の5つ。クライアント全体で `RemoteSvc('standing2')`、`Moniker('standingMgr')`、standing用の `MachoBindObject` は見つからない。`standing2` の一致は `eve/client/script/ui/shared/standings/standingData.py:16-33` の `standing2to1` フィールド。法人メンバー画面は `eve/client/script/ui/shared/neocom/corporation/corp_dlg_edit_member.py:138` で `standingMgr` proxyを保持するが、同proxyへのメソッド呼び出しはない。サーバーコードの `evestations/standingsrestriction.py:155` は `sm.GetService('standingMgr').GetStandingMatrix` を呼ぶが、クライアントRPC経路ではない。

eve.rsは `StandingMgrService` を `standingMgr` として登録する（`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:7`、`dotnet/src/Eve.Host/Rpc/CorporationRpc.cs:72`、`dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:59`）。Host compositionは同じサービスへの `standing2` aliasも登録する（`dotnet/src/Eve.Host/Hosting/HostComposition.cs:283`、character serviceがある場合）。EveJS v261018.1には `Standing2Service extends StandingMgrService` がある（`server/src/services/character/standingMgrService.js:244-248`）。V24.01クライアントはどちらの `standing2` variantも呼ばない。

## ローカル `standing` service API

全ツリーの `GetService('standing')` と `standingSvc` 呼び出しは下記のローカルhelperに集約される。これらはHost RPCではない。

| ローカルmethod | 呼び出し側の証拠 |
|---|---|
| `GetStanding` | `eve/client/script/ui/shared/standings/standingsBar.py:315`、`eve/client/script/ui/shared/info/panels/panelStandings.py:93` |
| `GetStandingWithSkillBonus` | `eve/client/script/ui/station/agents/agentUtil.py:122-137`、`eve/client/script/ui/station/agents/agentDialogueUtil.py:483-485` |
| `GetStandingTransactions` | `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:107`、`resourcewars/client/firstcompletiontracker.py:35` |
| `GetStandingCompositions` | `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:110` |
| `CanUseAgent` | `eve/client/script/ui/shared/maps/systemMapSvc.py:976`、`eve/client/script/ui/shared/info/infosvc.py:2918,2933` |
| `GetEffectiveStandingWithAgent` | `evemissions/client/ui/agentview.py:69`、`jobboard/client/features/agent_missions/page.py:101` |
| `AddToEffectiveStandingWithAgent` | `evemissions/client/missioncontroller.py:218` |
| `GetStandingsDataNPCsToMyCharacter` / `GetStandingsDataNPCsToMyCorp` | `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:137,139` |
| `AppendZeroStandingIfNeeded` | `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:157` |
| `GetUnreadEntries` / `RemoveFromUnreadEntries` | `eve/client/script/ui/shared/standings/standingEntry.py:123,108` |

このローカルAPIがリモートで呼ぶのは表中5 RPCだけ。例えば `GetStanding` や agentのskill bonus計算はクライアント内cache/UIロジックであり、Host methodとして登録する対象ではない。

## クライアントが呼ぶRPC

| メソッド・クライアント呼び出し | 引数と返信の利用 | eve.rs / EveJS照合 |
|---|---|---|
| `GetNPCNPCStandings` — `eve/client/script/ui/services/standingsvc.py:114`。返信rowsetを `Filter('toID')` と `Filter('fromID')` に分ける（115-117）。引数なし。 | Hostは `Rowset` の `fromID:int, toID:int, standing:real` を返す。NPC/factionの関係はpinned SDEから導き、静的クエリだけプロセス内cacheする（`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:156-160`; query登録25-76）。失敗用UserErrorや後続通知はない。 | EveJS `Handle_GetNPCNPCStandings` は同じrowset builderへ渡す（`server/src/services/character/standingMgrService.js:194-197`）。 |
| `GetCharStandings` — `eve/client/script/ui/services/standingsvc.py:119,122-125`。返り値を `Index('fromID')` する（131-133）。引数なし。 | Hostはsessionの `CharacterId`、必要なら `characterID` を使い、`fromID:int, standing:real` の `Rowset` を返す（`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:163-173`）。読み取りのみ。 | EveJSはsessionの `characterID || charid` を使う（`server/src/services/character/standingMgrService.js:199-205`）。 |
| `GetCorpStandings` — プレイヤー法人分岐の `uthread.parallel` から `eve/client/script/ui/services/standingsvc.py:122-127`。返り値を `Index('fromID')` する（131-133）。引数なし。NPC法人では呼ばない。 | Hostは `corporationID`、次に `corpid` を使う。明示的な法人standingと在籍メンバー平均を集め、同じ2列rowsetを返す（`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:82-122,175-181`）。クライアントはUserErrorを扱わない。 | EveJSは同じsession属性から法人standing rowsetを構築する（`server/src/services/character/standingMgrService.js:207-213`）。 |
| `GetStandingTransactions` — ローカルhelperから `eve/client/script/ui/services/standingsvc.py:177-183`、UIから `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:107`、Resource Warsから `resourcewars/client/firstcompletiontracker.py:35-36`。引数は `fromID,toID` の位置引数2つ。UIとResource Warsは全リストを消費し、history UIは `eventTypeID,eventDateTime,modification` をsort/表示に使う（`eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsHistoryEntry.py:57-63`、`eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsHistoryScroll.py:24-33`）。helperは `modification * 10`（178-180）。 | Hostは `PyInt` と `PyLong` の正IDを受ける。返信はKeyValのlistで、`eventTypeID:int,eventDateTime:long(FILETIME),modification:real,fromID:int,toID:int,msg:wstr,int_1/int_2/int_3:int|None`。無効ID、`None`、別character宛ては空list。読み取りは `relation_history` を新しい順に返し、クライアントにページングがないため512件上限を除去した（`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:124-136,183-205`）。 | EveJSも該当characterの履歴を全件降順で返す（`server/src/services/character/standingRuntime.js:1035-1061`、reply builder `server/src/services/character/standingMgrService.js:55-111,137-160`）。 |
| `GetStandingCompositions` — local helper `eve/client/script/ui/services/standingsvc.py:282-283`、UI `eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingsPanel.py:110-111`。引数は `fromID,corporationID` の位置引数2つ。scrollは各行の `ownerID` を見てNPCを除外し、表示/sortで `standing` を読む（`eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingCompositionScroll.py:21-31`、`eve/client/script/ui/shared/neocom/charsheet/standingsPanel/standingCompositionEntry.py:43-47`）。 | Hostは `standing:R5, ownerID:I4` のCRowsetを `CachedMethodCallResult` で返す。session characterが指定のplayer corporationのメンバーでなければ空rowset。cacheは5分、session keyは `corpid`（`dotnet/src/Eve.Services.Corporations/StandingMgrService.cs:207-222`）。不正ID/Noneも同じ空rowsetで、UserErrorではない。 | EveJSも同じ2列CRowsetと5分 `corpid` cache metadataを返す（`server/src/services/character/standingMgrService.js:224-241`、`server/src/services/character/standingRuntime.js:487-520`）。 |

## 引数、失敗、状態、通知

クライアントの全RPC呼び出しは位置引数のみ。上表の3件は引数なし、履歴と構成は2位置引数。アプリケーションkwargsを送るclient callsiteはない。`machoVersion` はtransport metadataで、HostのCallWithKwargs経路がhandlerへ渡す前に除去する。追加した法人wire testは実Host socket経路に `machoVersion=1` を載せてこの動作を確認する。ID引数については混在する `PyInt/PyLong`、不正な `None` の両方をwire testで通す。`GetStandingTransactions` と `GetStandingCompositions` にmethod-specificなUserErrorはなく、入力条件ではクライアントが消費できる空collectionを返す。read backend exceptionは共有Host RPC error経路に委ねる。

この5 RPCは全てread-onlyで、呼び出し後のnotificationは期待されない。クライアントのlocal serviceは別のstanding変更通知 `OnStandingSet` と `OnStandingsModified` を受け取る（`eve/client/script/ui/services/standingsvc.py:14-16,41-75`）。Hostの既知のstanding write pathはcommit後に `OnStandingSet(fromID,toID,standing)` を送る（`dotnet/src/Eve.Host/Rpc/AgentRpc.cs:86-88`、NPC kill `dotnet/src/Eve.Host/Composition/SpaceComposition.cs:417-425`）。これらは上表のread RPCによる副作用ではない。

NPC/NPCはSDE由来。それ以外のstanding rowとtransaction historyは `Eve.Relations` のPostgreSQL `relations` / `relation_history` を読む（`dotnet/src/Eve.Relations/RelationStore.cs:77-157`、`docs/design/FOUNDATIONS.md` のRelations項）。character/corporation/historyのread queryは動的状態を読む。compositionは5分のメソッドcacheがある。サービス自身は状態を変更しない。

## Host wire確認

`dotnet/tests/Eve.Host.Tests/StandingMgrHostWireTests.cs` はHostServerのTCP/Macho dispatchを使う。既存テストでNPC rowset、character rowset、履歴KeyVal、空のcomposition CRowsetを確認し、追加したテストでcorporation rowset、`machoVersion` kwargs、`standing2` alias、混合int/long、None入力を確認する。513件の履歴を通す回帰テストで、上限除去後に全件返ることを確認する。wire testはfake ReadSideを使いPostgreSQLへ接続しない。

## 実行結果

- `Eve.Services.Corporations` build成功（警告0、error 0）。
- `Eve.Host.Tests` build成功（警告0、error 0）。
- focused `HostServerTests.Standing_` は4 passed、0 skipped。NPC/character/corporation rowset、履歴、composition、alias、transport kwarg、None、513件の履歴を含む。
- 最初の広い `FullyQualifiedName~Standing_` filterは `Outstanding_limit...` も拾い、DB laneを要求する1テストが失敗、2件skip、4件pass。その後、class-scoped filterでstanding wire testsを単独確認した。
- `Eve.Host.Tests` 全体を `EVE_PG_URL` なしで実行: 855件中528 passed、227 skipped、100 failed。失敗にはassigned PostgreSQL laneを要求する例（`ContractReadModelTests` の lane error、`MoonOwnerNotificationTests` の `EVE_PG_URL is not set`）が含まれる。このジョブはPostgreSQLを使わない条件なので、DB lane依存の全体結果はgreenではない。

本番Compositionでの `standingMgr` / `standing2` 登録は `dotnet/tests/Eve.Host.Tests/NewEdenStoreHostTests.cs:138-150` に `PgFact` があるが、このジョブはPostgreSQLを使わないため実行対象にしていない。正当な法人メンバーのcompositionと、database再起動後の永続性はこの環境では確認していない。GUI clientも起動していない。
