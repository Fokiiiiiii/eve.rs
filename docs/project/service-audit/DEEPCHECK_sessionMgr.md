# sessionMgr 深掘り監査

## 結論

V24.01 クライアントの `sessionMgr` はローカルサービスであり、Host に送信する RPC service ではない。全クライアントソースの検索で直接呼ばれるのは `PerformSessionChange`、`PerformSelectiveSessionChange`、`RegisterNotifyOfSessionChange`、`UnregisterNotifyOfSessionChange` の4メソッド。前二者は渡された別サービス／bound object／moniker の callable を実行するラッパーで、応答shapeは対象callableごとに異なる。

EveJS にある Host 向けの `sessionMgr.GetSessionStatistics` と `sessionMgr.RemoveSessionsFromServer` はクライアント呼出しではない。Carbon 内部の統計・proxy cleanup APIである。Hostに同名routeを追加すると実クライアントに存在しないRPC契約を作るため、今回の調査ではroute・wire testを追加していない。

## V24.01 クライアントの全呼出し

`eve/common/script/sys/eveSessions.py:90` は `__replaceservice__ = 'sessionMgr'` とし、Carbonの `SessionMgr` を置換する。`__exportedcalls__` は `ROLE_SERVICE` / `ROLE_ADMIN` に限定され、client role は公開していない (`eve/common/script/sys/eveSessions.py:93-115`)。クライアント内で同名サービスを使う場所はローカルの `GetService/StartService` または依存注入であり、`RemoteSvc('sessionMgr')`、sessionMgr向けのbound service/moniker RPCは見つからなかった。

全ソースを対象にした呼出し検索結果:

| ローカルメソッド | クライアント呼出しと利用 |
| --- | --- |
| `PerformSessionChange` | 60箇所。全callsite: `voidspace/client/void_space_activation_controller.py:145`; `homestation/client/service.py:106`; `eve/client/script/util/publicQaToolsClient.py:37,42`; `eve/client/script/util/webtools.py:120`; `eve/client/script/ui/shared/neocom/Alliances/all_ui_home.py:326`; `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_view_role_mgt.py:517`; `eve/client/script/ui/shared/neocom/corporation/corp_dlg_edit_member.py:513`; `eve/client/script/ui/shared/neocom/corporation/corp_ui_home.py:322`; `eve/client/script/ui/shared/neocom/corporation/corp_ui_member_newroles.py:551`; `eve/client/script/ui/shared/abyss/AbyssJumpWindow.py:34`; `eve/client/script/ui/shared/abyss/keyActivationWindow.py:50`; `eve/client/script/ui/services/clonejumpsvc.py:216,225`; `eve/client/script/ui/services/facWarSvc.py:195`; `eve/client/script/ui/services/ccSvc.py:185`; `eve/client/script/ui/services/menusvc.py:2105,2883`; `eve/client/script/ui/services/structure/structureControl.py:51,86`; `eve/client/script/ui/services/structure/structureDocking.py:40,45`; `eve/client/script/ui/services/corporation/base_corporation.py:165,409,803,822`; `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:514,516,552,568,578,583,588,597`; `eve/client/script/ui/services/menuSvcExtras/menuFunctions.py:185,208,217,551,574`; `eve/client/script/ui/station/base.py:497`; `eve/client/script/ui/login/charSelection/characterSelection.py:712`; `eve/client/script/parklife/cynoTravelSvc.py:35,44,53,70,93,102,111,122`; `eve/client/script/parklife/gameui.py:498,965`; `eve/client/script/parklife/shipcasterSvc.py:19,36`; `eve/client/script/parklife/autopilot.py:348,357`; `eve/devtools/script/svc_slash.py:1312,1336`; `eve/devtools/script/insider.py:630`; `randomJump/client/randomJumpController.py:54`; `stargate/client/gateJumpSvc.py:123`. |
| `PerformSelectiveSessionChange` | `eve/client/script/dogma/clientDogmaLocation.py:242`; `eve/client/script/parklife/fleetSvc.py:1193,1815`. |
| `RegisterNotifyOfSessionChange` | `eve/client/script/ui/shared/inventory/neocomInventoryBadging.py:147`. |
| `UnregisterNotifyOfSessionChange` | `eve/client/script/ui/shared/inventory/neocomInventoryBadging.py:153`. |

bound/moniker callableの例は `clientDogmaLocation.py:242` の `remoteShipMgr.Board`、`fleetSvc.py:1193,1815` のfleet callable、`menusvc.py:2105` の `RemoteSvc('structureJumpBridgeMgr')`。これらのservice名は `sessionMgr` ではない。

## メソッドごとの契約

| メソッド | 引数・型 | 応答と利用 | state・通知・エラー |
| --- | --- | --- | --- |
| `PerformSessionChange` | `sessionChangeReason, func, *args, **keywords`。required 2引数は位置／keywordどちらでも渡せる。通常callは位置引数、`ccSvc.py:185` は `sessionChangeReason=` / `func=` と `secondChoiceID=None` を使う。`hostileMutation`、`violateSafetyTimer`、`wait` はwrapper専用で消費し、その他の `args/keywords` を対象callableへそのまま渡す (`carbon/common/script/sys/sessions.py:418-438`)。この層はint/long変換をせず、その型制約は対象callableが定義する。 | 通常は `apply(func, args, kw2)` の返値をそのまま返す。short-circuit理由で遷移を無視した場合、`hostileMutation`系のlocal-only経路、または委譲先が `CancelSessionChange` を投げた場合はNone (`carbon/common/script/sys/sessions.py:449-457, 469-500`)。通常のtarget結果はtargetによりshapeが異なる。ship activation は4要素をunpack (`clientDogmaLocation.py:242-243`)、jumpは真偽値でwaypoint/autopilot継続を決める (`menusvc.py:2105-2107`, `autopilot.py:348-351`)、logoffも真偽値を読む (`gameui.py:498-501`)、CEO resignはNoneまたはcharacter ID列を読む (`base_corporation.py:803-818`)。 | Host永続化なし。ローカル `session.mutating` と `nextSessionChange` を管理し、遷移失敗時はtimerを条件付きで戻す (`carbon/common/script/sys/sessions.py:418-500`)。subscriber callbackをfunc実行前に呼び、`ProcessSessionMutating` / `OnSessionMutated` を処理する。同じ遷移中・timer中は `UserError('PerformSessionChangeInProgress', {'reason': ...})`。target例外は通常再送出し、`CancelSessionChange` は抑止される。server modeでの呼出しは `RuntimeError('Not Yet Implemented')`。 |
| `PerformSelectiveSessionChange` | `reasonAllowedToViolate, reason, allowWait, func, *args, **keywords`。3箇所は全て位置引数 (`clientDogmaLocation.py:242`, `fleetSvc.py:1193,1815`)。 | `PerformSessionChange` の返値を返す。fleet monikerの結果を局所で使用する場合もあり、sessionMgr固有shapeはない。 | `session.nextSessionChange` / 前回理由のprefixを見て `violateSafetyTimer` と任意の `wait` を設定して委譲する (`carbon/common/script/sys/sessions.py:502-511`)。追加の永続化／通知は持たない。上記と同じ委譲先・遷移エラー。 |
| `RegisterNotifyOfSessionChange` | subscriber objectとcallback。呼出しは `(self, self.stop_tracking)` (`neocomInventoryBadging.py:147`)。 | 戻値はNone。 | in-memory subscriber mapに登録する。session change前にcallbackを呼ぶ (`sessions.py:399-413`)。DB保存やwire notificationはない。 |
| `UnregisterNotifyOfSessionChange` | subscriber object。呼出しは `(self)` (`neocomInventoryBadging.py:153`)。 | 戻値はNone。 | in-memory mapからあれば削除。neocom inventoryの停止処理で登録解除する (`neocomInventoryBadging.py:151-158`)。DB保存やwire notificationはない。 |

`machoVersion` はsessionMgrのローカル引数ではない。対象のservice call層が `kwargs` に値がなければ既定値1を加え、wire payloadを `(1, method, args, kwargs)` とする (`carbon/common/script/net/ServiceCallGPCS.py:217-228`)。bound object callも同様に値を付ける (`ObjectCallGPCS.py:399-410`)。serverのobject call dispatchはmethod実行前に `machoVersion` をkwargsから除く (`ObjectCallGPCS.py:129-134`)。明示callerがsessionMgrへmachoVersionを渡す例はなく、int/long/Noneの意味付けは各target serviceの責務である。sessionMgr自体は値の変換やwire validationをしない。

## Host routeとerror

`dotnet/src` 全体の検索に `sessionMgr` というroute/service名はなく、HostのRPC構成は実サービスを `CoreRpc.cs:93,105-107` などで登録している。登録名は `ServiceRegistry.Register`、名前解決は `ServiceRegistry.Lookup` が行い、bound objectも別mapから検索する (`ServiceRegistry.cs:19-33,151-165`)。sessionMgrのservice/bound objectを登録する箇所はない。

仮にクライアントが同名serviceを直接送ると、通常dispatcherは `ServiceNotFound` を `ErrorResponse` に包んで返す (`PacketDispatcher.cs:128,161-162`)。sessionMgrのHost routeに対するUserError、永続state、後続notificationは存在しない。clientが送る正規の4ローカルメソッドをHost wire testする根拠はなく、現行の別target serviceをsessionMgr名に束ねるのも誤ったrouteとなるため、コード・wire test追加は行わない。

## EveJSとCarbon server側の同名API

EveJS v0.12.9 と 261018.1 の `server/src/services/machoNet/sessionMgrService.js` はどちらも `super("sessionMgr")` とし、2つのhandlerを持つ (`46-73`):

| handler | V24 client caller | 実際のCarbon内部経路 | EveJS 261018.1の処理 |
| --- | --- | --- | --- |
| `GetSessionStatistics` | なし。 | `MachoNet.GetSessionCounts` がlocal serviceに呼び、返却dictから `stats.get(attr, default)` を読む (`carbon/common/script/net/machoNet.py:1957-1971`)。Carbon側dictは属性ごとに `[value種類数, value別件数dict]` を返す (`carbon/common/script/sys/sessions.py:696-713`)。client UI応答ではない。 | `sessionRegistry` 件数から `{sessions, clientSessions, contextSessions}` を返す (`sessionMgrService.js:62-72`)。Carbonの属性dictとはshapeが異なり、永続化・通知なし。 |
| `RemoveSessionsFromServer` | なし。 | `SessionKillah` が `ConnectToRemoteService('sessionMgr', nodeID=...)` でproxyに `(myNodeID, sids)` を送る (`carbon/common/script/sys/sessions.py:129-130`)。proxy側はserver transportを特定し、対象sessionごとに `RemoveSessionFromServer` をworkerへ渡す。未知nodeはwarning (`sessions.py:856-870`)。これはnode間のin-memory session cleanupで、client RPCではない。 | node IDとsession IDを平坦化してaudit eventに記録するが `removedCount: 0` のまま `null` を返す (`sessionMgrService.js:51-60`)。sessionは削除されず、DB mutationやclient notificationもない。 |

Carbonには他にもproxy service接続がある。たとえば `GetProxySessionManager` は `ConnectToProxyServerService('sessionMgr', nodeID)` をcacheし (`sessions.py:373-376`)、`EndAllGameSessions(remote=1)` は全proxyへ `EndAllGameSessions` を送る (`sessions.py:843-855`)。これらもclient UIのsessionMgr RPCではない。旧版EveJS v0.12.9と261018.1の該当handler実装は同じno-op/統計shapeで、V24 client互換契約として採用しない。

## 実行確認

- Serenaで `/home/user/eve-logic` 全体の `sessionMgr` direct method referencesを検索し、callsiteと返値消費箇所を照合した。
- CBMで client symbols/callers、EveJS handler定義、Host route dispatcherを追い、Serenaで各該当範囲を読んだ。
- PostgreSQLとGUI clientは使用していない。EveJS handlerを起動する実行試験も行っていない。コード変更はなく、focused test / buildは実行していない。
