# Beyonce deep check

更新日: 2026-10-27

## 判定

監査は未完了。Bound route と既存 Host wire tests、クライアント呼び出しを照合し、Bookmark の読み取り経路、CmdAlignTo の bookmarkID、CmdDock の返却値を修正した。一方、Warp subject と component RPC に実装欠落が残るため、完了扱いにはしない。

## 追加確認 (2026-10-29)

- `MachoBindObject` の拒否時も `(boundObject, None)` を返す。V24.01 `carbon/common/script/net/moniker.py:140-144` は返値を `self.boundObject, retval` に展開する。追加した Host TCP wire test は同一 system でない bind が bound object と None の2要素tupleを返すことを検証する。
- `CmdWarpToStuff` の有効subjectには moon mining point、operation dungeon、external dungeon、epinstance、mercenary den もある。呼び出し元は `movementFunctions.py:461-489`、`michelle.py:739-741`、`transmissionMgr.py:141-143`、`mercDenWnd.py:323` から `statefulButtonLocationController.py:147-151` を通る。EveJS 261028.1 は moonIDを現systemのmoon mining beaconへ対応させ (`beyonceService.js:3527-3550`)、operation siteをcharacter spawnpointとinstance visibilityで確認し (`:3566-3582`)、dungeon instanceをmaterializeして確認する (`:3583-3601`)。`mercenaryDen` はEveJSでもunsupported-subject fallbackへ入る (`:3819-3823`)。現Beyonce Host pathには前4種を authoritative runtime state から解決する配線がなく、通常のball IDとして扱ってwarp成功を返すと別のentityへ飛ばすため拒否を維持し、互換差として記録した。
- `CmdTurboDock` の唯一の提示経路は `movementFunctions.py:506-518` のControl+Shift+Altかつ `session.role & ROLE_GML` 条件内であり、通常のdock pathは `CmdDock`。EveJS 261028.1 は `CmdDock` handler (`server/src/services/ship/beyonceService.js:4209-4217`) のみで、turbo handlerはない。GM専用shortcutを通常dockへ化けさせたりvalidationを迂回したりする根拠がないため、このcallはサポート外として `DEVIATIONS.md` に記録。
- `GetMultiLocationsEx` の `locationNameID` は `int?` に狭め、値があるときは `PyInt`、それ以外は `None` を返す。クライアントdescriptorは `eveCfg.py:604-620` の `DBTYPE_I4`; consumers `eveui/dragdata.py:75-78` と `eveui/autocomplete/location/suggestion.py:55-60` は値があれば localization lookup、なければ `locationName` を表示する。追加した Host TCP wire test は保存済み構造物名が `locationName` にあり、列7がNoneであることを確認する。

## Route と共通契約

- AgentRpc.cs:108-110 は spaceSessions がある時 BeyonceService を実サービス列へ登録する。CharacterServiceList.cs:73-90 は同じ instance に bookmark/scan provider を配線する。
- Eve-logic の bound 呼び出しは carbon/common/script/net/moniker.py:140-144,234-235 から MachoBindObject / MachoResolveObject を送る。beyonce moniker は eve/common/script/net/eveMoniker.py:141-143。
- Binding.PlaceOf は PyInt/PyLong と tuple/list の先頭を受け、Binding.NestedAsync は nested method、args、kwargs を展開する。Number は PyInt/PyLong/PyReal を扱う。machoVersion は transport kwargs、subfolderID などは nested kwargs。ServiceAuditP0HostWireTests.cs の BookmarkScanResult test は実 TCP route でこの分離と8要素 reply tupleを確認する。
- C# の decorated methods は BeyonceService.cs:92-767 と BeyonceService.Components.cs:15-16 に25個ある。

## クライアントが呼ぶ全 method

| Method | eve-logic 呼び出し / 消費箇所 | 実装の照合 |
|---|---|---|
| MachoResolveObject | carbon/common/script/net/moniker.py:234-235 | BeyonceService.cs:92-93。Proxy node id を返す。状態変更・通知なし。 |
| MachoBindObject | eve/common/script/net/eveMoniker.py:141-143; carbon/common/script/net/moniker.py:140-144 | BeyonceService.cs:99-111。同一system/in-spaceを検査、bound OID作成、nested call dispatch、応答後にballpark bind。bound registryはプロセス内状態。EveJS beyonceService.js:4410-4445 と順序を照合。 |
| BookmarkScanResult | eve/client/script/ui/shared/bookmarks/bookmarkSvc.py:622-633 | BeyonceService.cs:114-187。位置・名前・コメント・result・folder・expiryを検証してから保存callbackへ進む。Host composition は AccessGroupBookmarkMgrService.CreateScanBookmarkAsync / BookmarkStore に接続。EveJS reply は8要素tuple。Host wire test の保存callbackはfakeで、DBまでの実行確認ではない。 |
| AdminGetBubbleID | eve/client/script/remote/michelle.py:762-763; eve/client/script/ui/services/menuSvcExtras/modelDebugFunctions.py:87-88 | BeyonceService.cs:189-210。staff roleを確認し、int/long item IDからPyLongまたはNone。read-only。Host route testでreplyを確認。 |
| GetCynoJammerState | eve/client/script/ui/shared/sovDashboard/cynoJammerIcon.py:45-56 | BeyonceService.cs:212-224。現systemのsimulation timeまたはNone。read-only。Host route testあり。 |
| GetFormations | eve/client/script/remote/michelle.py:323-325 | BeyonceService.cs:226-227。ObjectCaching MethodResultでDiamond/Arrowを返す。Host route testがResultClassと名称を確認。 |
| UpdateStateRequest | eve/client/script/remote/michelle.py:849,855 | BeyonceService.cs:239-247。bound ownerならSpaceSessionsへ状態再送を依頼しNone。更新通知はsimulation/ballpark側。 |
| CmdSetSpeedFraction | eve/client/script/ui/eveCommands.py:1070; eve/client/script/ui/inflight/shipHud/activeShipController.py:226,247; eve/client/script/parklife/autopilot.py:433,450 | BeyonceService.cs:250-258。positional numericをsimulationへ渡してNone。 |
| CmdGotoDirection | eve/client/script/ui/eveCommands.py:1091,1126; eve/client/script/ui/inflight/shipHud/activeShipController.py:224,245; eve/client/script/ui/camera/cameraUtil.py:45; eve/client/script/parklife/scanSvc.py:464 | BeyonceService.cs:261-269。3 positional numericをsimulationへ渡してNone。 |
| CmdStop | eve/client/script/ui/eveCommands.py:1103; eve/client/script/ui/services/menusvc.py:2431 | BeyonceService.cs:272-280。引数なし、simulation command、None。 |
| CmdFollowBall | eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:228,301; eve/client/script/parklife/autopilot.py:436,453; eve/client/script/ui/shared/neocom/journal.py:448 | BeyonceService.cs:283-291。target positionalと任意range。range None/missingは0。 |
| CmdOrbit | eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:259 | BeyonceService.cs:294-302。target positionalと任意distance。None/missingは0。 |
| CmdAlignTo | eve/client/script/ui/services/menusvc.py:2777-2791。Bookmark時はdstID=None, bookmarkID=<id> kwargs | BeyonceService.cs:305-352を修正。アクセス可能なactive BookmarkをBookmarkStoreから解決し、ItemIdがあればentityへalign、座標のみなら現shipとの差分方向を計算。新TCP Host wire testはnested kwargsからbookmarkIDをresolverへ渡すところまで確認。実simulation commandのwire testはない。 |
| CmdWarpToStuff | eve/client/script/util/profiling.py:317; eve/client/script/ui/shared/addressBookEntries.py:155; eve/client/script/ui/shared/neocom/journal.py:452; eve/client/script/ui/shared/infoPanels/listSurroundingsBtn.py:381; eve/client/script/ui/shared/agencyNew/contentPieces/escalationContentPiece.py:74; eve/client/script/ui/services/menusvc.py:1427,2737,2747,2757; eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:418,432,451,462,488; eve/client/script/parklife/transmissionMgr.py:142; eve/client/script/remote/michelle.py:732-740; objectives/client/objective_task/dungeon.py:341,369; jobboard/client/features/dungeons/escalation_sites/job.py:57 | BeyonceService.cs:355-660 はitem/bookmark/char/position/scan/launchのみ実装。Bookmarkをactive/access-checked storeからItemIdまたは座標で解決するよう修正。ballparkCommon/parkhelpers/warpSubjects.py:11-25 はepinstance, operationDungeon, moonMiningPoint, externalDungeon, mercenaryDenも定義し、上記呼び出しに実例がある。C#はこれらを一般的なwarp refusalで拒否する。dungeon/site/denのstate ownerが未接続で、EveJS同等ではない。 |
| CmdWarpToStuffAutopilot | eve/client/script/parklife/autopilot.py:464 | BeyonceService.cs:665-676。bound targetへ10km warp。 |
| CmdStargateJump | eve/client/script/parklife/autopilot.py:357; eve/client/script/ui/stargate/client/gateJumpSvc.py:123; eve/devtools/script/insider.py:630 | BeyonceService.cs:679-689。from/to/ship positionalをSpaceSessionsへ渡してNone。session transitionは後続通知。 |
| CmdJumpToFleetModuleBeacon | eve/client/script/parklife/cynoTravelSvc.py:35,102 | BeyonceService.cs:692-699。char/beacon/systemをSpaceJumpDriveへ渡す。成功replyはbound OIDと利用可能時刻。 |
| CmdJumpToFleetDeployableBeacon | eve/client/script/parklife/cynoTravelSvc.py:44,111 | BeyonceService.cs:701-708。clientはdeployableID/systemID/beaconIDを渡すがC#は3番目を読み捨て、arg0をfield IDとしてSpaceJumpDriveへ渡す。PgDeployedStore.cs:5-18 は単一 deployable_id のみで、別beaconIDとの対応を持たず、FleetStoreにもactiveDeployableBeacons相当がない。EveJS beyonceService.js:1492-1562,2779-2902 はfleetのactiveDeployableBeaconsから両IDを照合する。現C#の2 ID同値通知だけでは実client入力の対応を検証できず、deployable variantはend-to-end未接続。 |
| CmdBridgeToFleetModuleBeacon | eve/client/script/parklife/cynoTravelSvc.py:61 | BeyonceService.cs:711-722。beacon/systemをbridge runtimeへ渡してNone。 |
| CmdBridgeToFleetDeployableBeacon | eve/client/script/parklife/cynoTravelSvc.py:70 | BeyonceService.cs:724-735。deployable/systemを使うが3番目のbeaconIDを検証しない。 |
| CmdGroupJumpToFleetModuleBeacon | eve/client/script/parklife/cynoTravelSvc.py:102 | BeyonceService.cs:738-745。beacon/systemをconduit runtimeへ渡す。 |
| CmdGroupJumpToFleetDeployableBeacon | eve/client/script/parklife/cynoTravelSvc.py:111 | BeyonceService.cs:747-754。deployable/systemを使うがbeaconIDを検証しない。 |
| CmdJumpThroughFleet | eve/client/script/parklife/cynoTravelSvc.py:93 | BeyonceService.cs:757-763。other ship/beacon/systemをbridge runtimeへ渡す。 |
| CmdDock | eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:516 | BeyonceService.cs:766-775を修正。SpaceSessionsのdocking side effectsとUserErrorは維持し、accepted timestampを返さずNone。clientはsession transitionを待つ。accepted bound-wire testはない。 |
| CallComponentFromClient | caller一覧は下記 | BeyonceService.Components.cs:15-35 はlinkWithShipのみ実行。EveJSの他componentはNoneで返り、副作用とbool/quantity replyが欠ける。現C#コメントの「他はEveJSと同じ」は誤り。 |

## CallComponentFromClient subcalls

全callerはCallComponentFromClient(itemID, componentName, methodName, ...)をbound callで送信する。C# TextOfはPyStr/PyBytes/PyWStringを扱う。

| component.method | eve-logic caller | EveJS / C# |
|---|---|---|
| linkWithShip.InitiateLink / InitiateLinkQuick | spacecomponents/client/components/linkWithShip.py:238,244 | EveJSとC#の双方がcallbackへ接続。 |
| towGameObjective.Start / Stop | spacecomponents/client/components/towGameObjective.py:125,131 | EveJS beyonceService.js:3149-3162 はstateful requestとTOW_GAME_OBJECTIVE_INVALID_REQUESTを実装。C#にowner/mutationなし。 |
| itemTrader.ProcessTrade | spacecomponents/client/components/itemTraderNew.py:31; itemtrader_legacy.py:36 | EveJS beyonceService.js:3164-3180 はrecipeID/multiplier/inputISK kwargsを処理しtrade resultを返す。C#はNone。 |
| orbitalSkyhook.IsCharacterAuthorised / ExtractReagentsToShip | spacecomponents/client/components/orbitalSkyhook.py:251,257 | EveJS beyonceService.js:3182-3210 はbool/quantityを返しreagent mutationを行う。C#はNone。 |
| phaseStabilizer.ConnectShip | spacecomponents/client/components/phaseStabilizer.py:148 | EveJS dispatcherにあり、C#はNone。 |
| mercenaryDen.ExtractInfomorphsToShip | spacecomponents/client/components/mercenaryDen.py:109 | client-visible item mutation。C#はNone。 |
| underConstruction.DepositItemsFromShip | spacecomponents/client/components/underConstruction.py:171 | clientはtypeAndQty keywordを渡す。C#はkwargs/state ownerを処理せずNone。 |
| microJumpDriver.StartMicroJumpDrive | spacecomponents/client/components/microJumpDriver.py:91 | EveJS beyonceService.js:3212-3243 はruntime actionを起動。C#はNone。 |

EveJSはmovementFunctions.py:523-524のFILAMENT_SPOOLUP / JumpThroughRandomFilamentGateもcomponent dispatcherで処理する（beyonceService.js:3149-3243）。C#に対応actionなし。これらはstate ownerが見つからず、この監査ではfake successを足していないためend-to-end gapのまま。

## 呼び出しが見つからないclient wrapper

- GetFleetState: wrapperはeve/client/script/remote/michelle.py:286-294。全client treeでwrapperのcallerが見つからなかった。通知消費は同ファイル:259-261,1428-1431。C# route / EveJS handlerなし。
- GetFleetLocationAndInfo: wrapperはeve/client/script/parklife/fleetSvc.py:883-888。全client treeでcallerが見つからず、C# route / EveJS handlerなし。
- GetRelativity: wrapperはeve/client/script/remote/michelle.py:535-545。全client treeでcallerが見つからず、C# route / EveJS handlerなし。
- 呼び出し可能性の確認できないdormant pathとして記録し、active RPCとは扱わない。

## 実行した確認

- Host test project build: 成功、0 warning / 0 error。変更したCore / Space / Hostとtest projectを含む。
- 新規 Beyonce_align_to_resolves_the_client_bookmark_keyword_through_bound_host_wire: 成功、1 passed。TCP Host routeで外側machoVersion、nested dstID=None / bookmarkID=longを送り、resolverへ値が届くことを確認。
- 全 Eve.Host.Tests shardをEVE_PG_URLを除外して実行: 824件中496 passed、101 failed、227 skipped。失敗例はGameRpcPersistenceTests.cs:19とShipCosmeticsMgrRpcServiceTests.cs:105のDB URL必須assertion。PostgreSQLには接続していない。failure/skipを通過扱いしていない。
- 実graphical client testは行っていない。CmdDock accepted/session-change wire、BookmarkStore経由のwarp/align、deployable beaconの異なるID、unsupported warp subjects、component subcallsのHost testsと実装が残っている。
- PostgreSQL未使用のためDB-backed persistence / restart / notificationの実行確認はしていない。
