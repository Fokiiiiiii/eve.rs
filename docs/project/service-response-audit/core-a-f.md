# Eve.Services.Core A–F 応答互換監査

監査時点: 2026-10-13（中断後の追補完了）
対象checkout: C:\Users\yutai\eve.rs / main / 取得基点HEAD 52b8421f34e635900a18459da4403f26f2dfaec0 / 最終確認HEAD dda03ca0464043822e68c8348604ff34eadedcac
参照: C:\Users\yutai\evejs-v0.12.9、C:\Users\yutai\eve-logic
対象範囲: dotnet/src/Eve.Services.Core のbasename A–Fの26個のC#ファイルにある全service宣言。FittingMgrServicesを含む。

## 集計（現行main）

- サービスID: **34**。全34にEveJS同名serviceがある。EveJS該当なし **0**。
- A–F Core宣言RPC: **183**。実Host登録RPC: **186**。Core fallbackのshipCosmetics 7→Host 9、paperDoll 5→Host 6の差が3件。fallback四型は登録されない。
- EveJS export RPC: **228**。Host-only名 **0**、現行EveJS-only名 **42**。
- 累計Host body詳細比較: **186/186**（先行135 + 旧未比較49 + 現行追加2）。body未比較 **0**。比較を終えた件数であり、186件の互換一致・runtime受入を意味しない。
- 旧EveJS-only **44/44** のbody・入力・shape・client経路・現行Host所属を調査。分類: **未実装33、既存別serviceのclient経路で対応3、legacyでclient到達未確認6、現行同service実装済2**。
- 「既存別serviceで対応3」はclient local serviceが別RPCを使うことを確認したもの。旧RPC名自体はHostで未登録。よって未登録名42 = 33 + 3 + 6。新たに実装済の2件はagentMgr.MachoResolveObjectとdungeonInstanceCacheMgr.GetTriglavianSiteInstances。
- 実wire capture、EveJS対eve.rsの同一シナリオ実行、DB readback、race/retry/restart、graphical clientは **未実施**。

## 調査方法と証拠の扱い

AGENTS.md、CLAUDE.md、AGENT_OVERRIDE.md、dotnet/src/AGENTS.md、関連skill repo-recon/ponytail、ADR 0001/0003/0005/0010を確認した。比較ではEveJSのservice handler、Hostの登録・provider、eve-logicのcallsiteを別々に追った。

CBMは接続済みでTransport closedは発生しなかったため、指定のCLI fallbackは不要だった。CBMのcoreディレクトリcoverageはfreshness=not_trackedだったので、indexは探索に使い、内容と行番号はSerenaの現在sourceを基準にした。継続監査ではSerena manualを読み、canonical Serenaを絶対path C:\Users\yutai\eve.rsへactivateした。参照検索はrootを確認したCBMを使用した。mainのexact-path coverageはmetadata_changedだったため、現行C#のbody・行番号はSerenaで読み直した。CBMはC-Users-yutai-eve.rs / repository root、evejs-v0.12.9 / EveJS repository（828823a75f211fdecb945b087a26c225f111491f）、eve-logic / eve-logic repository（1dbb5711a072a146e1d228365668ac05655eefe1）を確認済み。

これはsource-level監査であり、テスト、headless differential run、実EVE V24.01 graphical client、PostgreSQLシナリオ、再起動後の応答は実行していない。PyValue/JS値から導く応答shapeは静的な判定で、実wire byte captureの確認ではない。ID、Filetime、CallId、現在時刻の値差はshape差と分けている。

## サービスとexport RPC一覧

D/Uは「累計詳細比較済み/Host RPC body未比較」。H/Eは「実Host RPC数/EveJS RPC数」。各RPC名は大文字小文字を含めて記載した。

| Service ID | H/E | D/U | Host RPC名（すべて） | EveJS-only RPC | 主な根拠 |
|---|---:|---:|---|---|---|
| accessGroupBookmarkMgr | 18/18 | 18/0 | GetMyActiveBookmarks, AddFolder, UpdateFolder, DeleteFolder, GetFolderInfo, SearchFoldersWithAdminAccess, AddToKnownFolders, RemoveFromKnownFolders, UpdateKnownFolderState, BookmarkStaticLocation, BookmarkLocation, UpdateBookmark, DeleteBookmarks, MoveBookmarksToFolderAndSubfolder, CopyBookmarksToFolderAndSubfolder, CreateSubfolder, UpdateSubfolder, DeleteSubfolder | — | Core AccessGroupBookmarkMgrService.cs:13-256; Host CharacterServiceList.cs:70; EveJS accessGroupBookmarkMgrService.js:145-644 |
| agentMgr | 14/30 | 14/0 | MachoResolveObject, MachoBindObject, DoAction, GetMissionBriefingInfo, GetMissionObjectiveInfo, GetMyJournalDetails, GetAgents, GetCompletedCareerAgentIDs, GetAgentLocationWrap, GetDisabledMissions, GetMessagesForEpicArcMissions, GetMyEpicArcStatus, GetPlausibleMissionIDs, GetSolarSystemOfAgent | GetAgentByID, GetAgentStaticInfo, GetDungeonShipRestrictions, GetEntryPoint, GetInfoServiceDetails, GetMissionJournalInfo, GetMissionKeywords, GetReplayTimestamp, GetStandingGainsForMission, GetStaticBlob, GotoLocation, IsCheatingWithAgent, RemoveOfferFromJournal, ShouldAlwaysAllowReplay, WarpToAgentInSpace, WarpToLocation | Core AgentMissionService.cs:42-783; Host MissionComposition.cs:214, AgentRpc.cs:67; EveJS agentMgrService.js:696-1424 |
| air_npe | 3/3 | 3/0 | is_air_npe_enabled, get_air_npe_state, skip_air_npe | — | Core AirNpeService.cs:12-66; Host ProgressRpc.cs:73; EveJS airNpeService.js:20-52 |
| blueprintManager | 3/3 | 3/0 | GetLimits, GetBlueprintData, GetBlueprintDataByOwner | — | Core BlueprintManagerService.cs:15-97; Host IndustryRpc.cs:47; EveJS blueprintManagerService.js:19-54 |
| browserLockdownSvc | 4/4 | 4/0 | GetDefaultHomePage, IsBrowserInLockdown, GetFlaggedSitesHash, GetFlaggedSitesList | — | Core BrowserLockdownService.cs:6-23; Host CoreServices.cs:45; EveJS browserLockdownService.js:14-42 |
| calendarMgr | 11/11 | 11/0 | GetResponsesForCharacter, GetResponsesToEvent, CreatePersonalEvent, CreateCorporationEvent, CreateAllianceEvent, EditPersonalEvent, EditCorporationEvent, EditAllianceEvent, DeleteEvent, UpdateEventParticipants, SendEventResponse | — | Core CalendarMgrService.cs:24-174; Host HostComposition.cs:114-116; EveJS calendarMgrService.js:18-163 |
| calendarProxy | 2/2 | 2/0 | GetEventList, GetEventDetails | — | Core CalendarMgrService.cs:177-215; Host HostComposition.cs:114-116; EveJS calendarProxyService.js:9-27 |
| securityMgr | 1/1 | 1/0 | get_modified_systems | — | Core ClientStartupServices.cs:8-13; Host CoreRpc.cs:84; EveJS securityMgrService.js:7-16 |
| alert | 2/5 | 2/0 | BeanCount, SendClientStackTraceAlert | BeanDelivery, GetLogModeForError, GroupBeanDelivery | Core ClientStartupServices.cs:15-44; Host CoreRpc.cs:104; EveJS alertService.js:252-332 |
| fighterMgr | 10/10 | 10/0 | GetFightersForShip, CmdAbandonFighter, CmdActivateAbilitySlots, CmdDeactivateAbilitySlots, CmdScoopAbandonedFighterFromSpace, ExecuteMovementCommandOnFighters, LaunchFightersFromTubes, LoadFightersToTube, RecallFightersToTubes, UnloadTubeToFighterBay | — | Core ClientStartupServices.cs:46-131; Host CoreRpc.cs:41-50,105; EveJS fighterMgrService.js:54-469 |
| shipKillCounter | 1/2 | 1/0 | GetItemKillCountPlayer | GetItemKillCountNPC | Core ClientStartupServices.cs:133-161; Host CoreRpc.cs:108; EveJS shipKillCounterService.js:14-51 |
| bannedwords | 3/3 | 3/0 | fetch_regexes, check_words_allowed, check_search_words_allowed | — | Core CoreCompatibilityServiceAdapters.cs:6-22; Host CoreServices.cs:73; EveJS bannedwordsService.js:129-147 |
| alphaInjectorMgr | 1/1 | 1/0 | GetNextAvailableInjection | — | Core CoreCompatibilityServiceAdapters.cs:24-32; Host CoreServices.cs:74; EveJS alphaInjectorMgrService.js:10-21 |
| cache | 1/1 | 1/0 | GetServerVersionAndBuild | — | Core CoreCompatibilityServiceAdapters.cs:34-43; Host CoreServices.cs:75; EveJS cacheService.js:19-27 |
| communityTrackMgr | 2/2 | 2/0 | get_instance_attributes_public, change_qa_mode | — | Core CoreCompatibilityServiceAdapters.cs:45-99; Host CoreServices.cs:77; EveJS communityTrackMgrService.js:70-104 |
| contextualOfferMgr | 6/6 | 6/0 | TrackOfferDelivered, TrackOfferSeen, TrackOfferBuyPress, TrackOfferClosed, TrackOfferPurchased, TrackOfferPurchasedSeen | — | Core CoreCompatibilityServiceAdapters.cs:101-143; Host CoreServices.cs:78; EveJS contextualOfferMgrService.js:66-94 |
| localizationServer | 3/3 | 3/0 | UpdateLocalizationQAWrap, GetAllTextChanges, ReloadFSDPickle | — | Core CoreCompatibilityServiceAdapters.cs:147-201; Host CoreServices.cs:84; EveJS localizationServerService.js:26-53 |
| kiringMgr | 9/9 | 9/0 | GetKiringConfiguration, PerformKiringServerSideAuthenticationFromCode, GetSAuthAid, GetAntiAddictionCode, GetEveGuardServerAddress, IsMonochromeStyleEnabled, ShouldKickCheater, PlaceKiringOrder, ActivateRedeemingCode | — | Core CoreCompatibilityServiceAdapters.cs:208-299; Host CoreServices.cs:47; EveJS kiringMgrService.js:115-171 |
| FastCheckoutService | 5/5 | 5/0 | IsFastCheckoutEnabledForUser, ClearFastCheckoutCacheForUser, GetTestingConfiguration, GetOffersForUser, BuyOffer | — | Core FastCheckoutService.cs:26-133; Host RewardRpc.cs:50; EveJS FastCheckoutService.js:134-190 |
| charFittingMgr | 7/7 | 7/0 | GetFittings, SaveFitting, UpdateFitting, SaveManyFittings, DeleteFitting, DeleteManyFittings, UpdateNameAndDescription | — | Core FittingMgrServices.cs:299-322; Host FittingRpc.cs:22-25; EveJS charFittingMgrService.js:28-142 |
| corpFittingMgr | 8/8 | 8/0 | GetFittings, SaveFitting, UpdateFitting, SaveManyFittings, DeleteFitting, DeleteManyFittings, UpdateNameAndDescription, GetCommunityFittings | — | Core FittingMgrServices.cs:325-358; Host FittingRpc.cs:22-25; EveJS corpFittingMgrService.js:32-166 |
| allianceFittingMgr | 7/7 | 7/0 | GetFittings, SaveFitting, UpdateFitting, SaveManyFittings, DeleteFitting, DeleteManyFittings, UpdateNameAndDescription | — | Core FittingMgrServices.cs:361-384; Host FittingRpc.cs:22-25; EveJS allianceFittingMgrService.js:28-144 |
| shipCosmeticsMgr | 9/11 | 9/0 | ActivateSkinLicense, ApplySkinToShip, GetEnabledCosmetics, GetFirstPartySkinData, GetLicencedSkins, GetLicencedSkinsForShipType, GiveSkin, GMExpireSkinLicense, RemoveSkin | GetLicensedSkins, GetLicensedSkinsForShipType | Core CompatibilityRpcServices.cs defines a 7-method fallback but is not registered; active Host ShipCosmeticsMgrRpcService.cs:14-303 via InventoryRpc.cs:45-46; EveJS shipCosmeticsMgrService.js:245-469 |
| characterEnergyMgr | 1/1 | 1/0 | GetMyEnergyState | — | Core CompatibilityRpcServices.cs fallback is not registered; active Host CharacterRpcServices.cs:8-53 via InventoryRpc.cs:67; EveJS characterEnergyMgrService.js:280-310 |
| paperDollServer | 6/7 | 6/0 | GetMyPaperDollData, GetPaperDollData, GetPaperDollDataBatch, GetPaperDollPortraitDataFor, UpdateExistingCharacterFull, UpdateExistingCharacterLimited | ConvertAndSavePaperDoll | Core CompatibilityRpcServices.cs fallback is not registered; active Host CharacterRpcServices.cs:55-295 via InventoryRpc.cs:68; EveJS paperDollServerService.js:181-290 |
| planetMgr | 3/22 | 3/0 | DeleteLaunch, GetMyLaunchesDetails, GetPlanetsForChar | GetCommandPinsForPlanet, GetExtractorsForPlanet, GetFullNetworkForOwner, GetPlanetInfo, GetPlanetResourceInfo, GetProgramResultInfo, GetResourceData, GMAddCommodity, GMCleanupExpiredLaunches, GMGetCompleteResource, GMGetLocalDistributionReport, GMGetPlanetDiagnostics, GMGetSynchedServerState, MachoBindObject, MachoResolveObject, UserAbandonPlanet, UserLaunchCommodities, UserTransferCommodities, UserUpdateNetwork | Core CompatibilityRpcServices.cs fallback is not registered; active Host PlanetMgrRpcService.cs:12-123 via InventoryRpc.cs:69; EveJS planetMgrService.js:988-1531 |
| crateService | 7/7 | 7/0 | GetCrateLocation, OpenCrate, ClaimLootFromCrateStack, PeekInsideFixedCrate, ClaimLoot, ClaimSkillBundle, ClaimFitting | — | Core CrateService.cs:43-498; Host InventoryRpc.cs:44; EveJS crateService.js:541-705 |
| dungeon | 5/5 | 5/0 | DEGetArchetypes, DEGetDungeons, DEGetFactions, DEGetRoomObjectPaletteData, DEGetRooms | — | Core DungeonRpcServices.cs:47-124; Host DungeonRpc.cs:48; EveJS dungeonService.js:167-214 |
| dungeonInstanceCacheMgr | 15/15 | 15/0 |GetCombatAnomalyInstances, GetCombatAnomaliesCount, GetSignatureInstances, GetIceBeltInstances, GetIceBeltsCount, GetOreAnomalyInstances, GetOreAnomaliesCount, GetOreAnomaliesCountInRange, GetBulwarkMiningSiteCounts, GetFactionWarfareInstances, GetFactionWarfareBattlefieldInstances, GetHomefrontSiteInstances, GetPirateInsurgencyInstances, GetPirateInsurgencyIceHeistInstances, GetTriglavianSiteInstances | — | Core DungeonRpcServices.cs:127-494; Host DungeonRpc.cs:49; EveJS dungeonInstanceCacheMgrService.js:624-703 |
| facilityManager | 7/7 | 7/0 | GetFacilities, GetFacility, GetFacilitiesByID, GetMaxActivityModifiers, GetFacilityTaxes, SetFacilityTaxes, GetFacilityLocations | — | Core FacilityManagerService.cs:8-46; Host IndustryRpc.cs:42, IndustryComposition.cs:153-203; EveJS facilityManagerService.js:46-134 |
| RWManager | 2/2 | 2/0 | get_closest_rw_stations, solarsystem_contains_rw_instances | — | Core FactionActivityRpcServices.cs:10-56; Host CoreRpc.cs:85 (constructed without callbacks); EveJS rwManagerService.js:150-177 |
| fwCharacterEnlistmentMgr | 6/6 | 6/0 | CreateMyDirectEnlistment, GetCorpAllowedEnlistmentFactions, GetMyDirectEnlistmentCooldownTimestamp, GetMyEnlistment, RemoveMyDirectEnlistment, SetMyCorpAllowedEnlistmentFactions | — | Core FactionActivityRpcServices.cs:68-220; Host CoreRpc.cs:86-101 with character/corporation callbacks; EveJS fwCharacterEnlistmentMgrService.js:65-144 |
| fwVictoryPointMgr | 2/2 | 2/0 | GetAllVictoryPointStates, GetLocalVictoryPointState | — | Core FactionActivityRpcServices.cs:227-274; Host CoreRpc.cs:102 (constructed without callbacks); EveJS fwVictoryPointMgrService.js:93-141 |
| insurgencySolarsystem | 2/2 | 2/0 | GetAllVisibleCampaigns, GetLocalCampaignClientSnapshot | — | Core FactionActivityRpcServices.cs:293-352; Host CoreRpc.cs:103 with ReadInsurgencyCampaignsAsync; EveJS insurgencySolarsystemService.js:61-88 |

## 詳細比較で確認した差分

### agentMgr: 現行の登録・bound経路（旧指摘を訂正）

現行Hostは14 RPC。MachoResolveObjectはProxyNodeIdを返し、BindAsyncは同じHasMethod表を使ってnested RPCへCallAsyncでdispatchする。args、kwargs、bound object ID/parametersを引き継ぐ。前回の「Resolveなし」「DoAction以外のnested call拒否」は現行不具合として撤回する。根拠: ../../../dotnet/src/Eve.Services.Core/AgentMissionService.cs:67-107,184-232。AgentRowもPyListへ修正済み（同:456-477）。aab0dd10/b636110e以後のcurrent sourceで確認した。

残る16名の未登録を一律に破損とは扱わない。client-local lookupで覆われる1、legacy未確認2、client callsite付き未実装13を追補の44件台帳で区別する。Briefing/Objectiveとbound Journalの内容差分は下記49件表と残存指摘を参照。DoActionの既比較記録を追加の11件と混同しない。

### planetMgr:一覧3件だけではbound planetを使えない

HostはDeleteLaunch/GetMyLaunchesDetails/GetPlanetsForCharだけを登録する。EveJSはさらにMachoResolveObject/MachoBindObjectとnetwork, pin, extractor, resource, launch, transferなど19 RPCをexportする。V24 clientはPlanetService.GetPlanetからClientPlanetを作り、InitでeveMoniker.GetPlanet(planetID)を設定する (planetSvc.py:55-62; clientPlanet.py:29-40)。その後remoteHandler.GetPlanetInfo()を呼び、他にもUserUpdateNetwork(serializedChanges), UserLaunchCommodities(commandPinID, commodities), UserTransferCommodities(path, commodities), GetProgramResultInfo(pinID, typeID, heads, headRadius), GetResourceData(info)を呼ぶ (clientPlanet.py:82, 179, 411, 447, 524, 733)。

EveJSのUserUpdateNetworkはidempotency keyとnetwork revisionを確認し、ISKをdebitし、失敗時refundし、networkを更新してserialized colonyを返す (planetMgrService.js:1212-1326)。Host PlanetMgrRpcService.HasMethodは3名だけ (PlanetMgrRpcService.cs:27)、残るmethodはPacketDispatcherのunknown-method経路に入る。GetPlanetsForCharのrowsetが返っても、後続のbound monikerとplanet操作は未接続。

### facilityManager:5 methodが実データなしで応答する

Host callbackはGetFacility/GetFacilitiesByIDだけを処理する (IndustryComposition.cs:153-203)。残るGetFacilitiesは空list、GetMaxActivityModifiersは空dict、GetFacilityTaxesは各tax fieldがNone、GetFacilityLocationsは空list、SetFacilityTaxesはNoneを返すだけで、税の保存もOnFacilitiesUpdatedもない (FacilityManagerService.cs:21-45)。clientはfacilitySvc.py:45,59,86,90,93,117,132でこれらを使う。EveJSはGetFacilitiesをsession用facility listで返し、GetMaxActivityModifiersは6 activityのmapを返し、GetFacilityTaxesを読んでSetFacilityTaxes後に通知し、GetFacilityLocationsを計算する (facilityManagerService.js:50-132)。

成功時GetFacilityも部分payloadで、Hostはactivities/serviceAccess/rigModifiersを空にして返す (IndustryComposition.cs:153-203)。clientが活動可能facilityを絞る時にEveJSと同じデータにはならない。

### calendarMgr/calendarProxy:書込み後のevent notificationsがない

引数と戻り値はEveJSと対応する。Create*はevent ID、Edit/Delete/UpdateEventParticipants/SendEventResponseはNone、CalendarProxy.GetEventListは3 listのtuple、GetEventDetailsはdetails payloadである (CalendarMgrService.cs:37-170,185-214; EveJS calendarMgrService.js:23-161, calendarProxyService.js:14-25)。

しかしEveJSは作成後OnNewCalendarEvent、変更後OnEditCalendarEvent、削除時OnRemoveCalendarEvent、参加者追加/削除、応答時OnExternalCalendarEventResponseを対象セッションへ送る (calendarMgrService.js:23-149)。Host handlerはCalendarStore更新とreturnのみで、通知送信を持たない。dotnet/src内にも対応するOnNew/OnEdit/OnRemoveCalendarEvent送信箇所は見つからなかった。読み直しで更新できても、他の閲覧者への変更反映はEveJS互換でない。

### accessGroupBookmarkMgr:共有folder fieldsと更新通知の不足

AddFolder/UpdateFolderのEveJS入力はArg0-6でgroup access ID (admin/manage/use/view)も受け取る。Core handlerはArg0-2だけを保存し、folder payloadのadminGroupID/manageGroupID/useGroupID/viewGroupIDは常にNone (AccessGroupBookmarkMgrService.cs:29-45, 228-243)。SearchFoldersWithAdminAccessもpersonal folderだけ返し、EveJSが含める共有admin folderを返さない (同.cs:58-64; EveJS accessGroupBookmarkMgrService.js:214-249)。

さらにEveJSは共有folder更新、bookmark追加/更新/削除/移動、subfolder追加/更新/削除をOnFolderUpdatedカテゴリの通知として、変更後・応答前に他のfolder viewersへ送る (accessGroupBookmarkMgrService.js:228-255, 394-637; bookmarkNotifications.js:135-185)。Core serviceはBookmarkStoreを書いて返すが、session notification経路を持たない。clientのACLBookmarkLocationはstatic targetをBookmarkStaticLocationへ送る (bookmarkSvc.py:607-619); このRPCの成功応答tupleは合う一方、他viewer通知は不足する。非staticのBookmarkLocationはclientがballpark bound methodへ送るため、このserviceのBookmarkLocationの現在client impactは未確定。

### fitting:通常mutationは通知順を維持、CommunityFittingsは空

char/corp/allianceの21 shared RPCは同じFittingMgrCallsに委譲される。save/update/deleteはstore write後にowner sessionsとcallerへ通知し、corp cache invalidationをOnFitting通知より先に送る。EveJSのnotifyFittingMutationもinvalidationを通知より先に送る (FittingMgrServices.cs:34-295, 242-254; fittingMgrServiceHelpers.js:118-153)。戻り値はGetFittings=dict, Save/Update=ID, SaveMany=mapping list, Delete=None, DeleteMany=ID listで対応する。

例外はGetCommunityFittings。Host FittingRpcはCorpFittingMgrService(calls)を作りreadCommunityFittingsを渡さない (FittingRpc.cs:22-25); serviceはcallback nullなら空dictを15分cacheする (FittingMgrServices.cs:325-358)。EveJSはCOMMUNITY_FITTING_CORPのfitting recordsを同じGetCommunityFittings RPCから返す (corpFittingMgrService.js:54-62; fittingStore.js:634-668)。clientはfittingSvc.py:404-405で呼ぶ。current sourceにコミュニティdataが入っているかは未確認なので、空応答の実プレイヤーへの影響はそのdata条件付き。

### crateService.ClaimFitting: 保存先の旧指摘を撤回

c7288f1c以後の現行HostはCrate backendへsavedFittingsを渡し、ClaimFittingAsyncがPgSavedFittingStore.SaveManyAsync(Character, characterId, ...)を呼ぶ。charFittingMgrと同じ保存ownerである。GameStore savedFittingsはclaim元の定義を読むために残るが、claim先はPostgreSQL。旧「別storeへ保存するので一覧に出ない」は撤回。根拠: ../../../dotnet/src/Eve.Host/Rpc/InventoryRpc.cs:44、../../../dotnet/src/Eve.Host/Persistence/Inventory/PersistenceCrateServiceBackend.cs:141-166。

現行CrateService.ClaimFittingAsyncはpending数をlock内で先に予約し、保存不成立ならfinallyで戻す（../../../dotnet/src/Eve.Services.Core/CrateService.cs:247-286）。これはsourceで確認した修正で、並列実行PASSの主張ではない。読み直し時点に親のCrate作業中差分があった。再接続後pendingや通知について、この保存先修正だけから完成とは判断しない。

### BannedWords / AlphaInjector / Core adapters

Host CoreServices.CreateはRpcHandlerを渡さずBannedWordsService/AlphaInjectorMgrService等を生成する (CoreServices.cs:73-84; Host searchでRpcHandler setter未検出)。BannedWordsはfetch_regexesを常に空list、check_words_allowed/check_search_words_allowedをNoneへfallbackする (CoreCompatibilityServiceAdapters.cs:6-22)。EveJSはconfigured regexesを読み、err_message kwargも使って禁止語をUserErrorで拒否する (bannedwordsService.js:97-145)。clientはnotepad.py:576-588、createContractWnd.py:1412等で呼ぶ。Hostにregex data/providerをつなぐ経路がなく、regexが設定されるscenarioでは登録済みでも応答が異なる。

GetNextAvailableInjectionはHostがLong(0)を返す (CoreCompatibilityServiceAdapters.cs:29-31)、EveJSはcharacter stateから次のAlpha injection Filetimeを計算する (alphaInjectorMgrService.js:15-20; skillTradingRuntime.js:665-667)。client callsiteはskillsvc.py:1004。GetKiringConfiguration等はlocal safe defaults、PlaceKiringOrderは時刻付きIDを使う。ActivateRedeemingCodeはEveJS Trueに対しHost False (CoreCompatibilityServiceAdapters.cs:263-265; kiringMgrService.js:167-169); no redeeming storeを理由とする明示的な差で、DEVIATIONS.md:1384に記載。

FastCheckoutのBuyOfferはEveJSがpurchaseTraceID/journeyID kwargsをcallbackに渡すが、Hostはoffer/offerIDを読みCallIdをidempotency keyとして使う (FastCheckoutService.cs:50-77; FastCheckoutService.js:159-188)。現行方針としてreal-money offersを無料で本当に付与し、after commitで通知する差はDEVIATIONS.md:1079-1090に記載済み。これは互換差分として記録し、テスト/実購入は実行していない。

### Faction data services

CoreRpcはRWManagerService()とFwVictoryPointMgrService()をcallbacksなしで生成し、FwCharacterEnlistmentにはcharacter/corporation read/write callbacks、InsurgencySolarsystemにはReadInsurgencyCampaignsAsyncを渡す (CoreRpc.cs:85-103)。

RWManagerはcallback未設定時にstation map={} / contains=false、VictoryPointはstates={}、local result=[session system, None]を返す (FactionActivityRpcServices.cs:17-42,235-257)。EveJSはRW近隣station/active system state、VictoryPoint state sourceを読み (rwManagerService.js:154-175; fwVictoryPointMgrService.js:111-139)。clientはlpStoreSvc.py:530とfwVictoryPointSvc.py:35-46で呼ぶ。データ提供が未接続ならEveJSと異なる。

FwCharacterEnlistmentとInsurgencyはHost callbackへ配線される。Enlistmentのcreate/removeはsave成功後にsession.Change(warfactionid)を送る (FactionActivityRpcServices.cs:80-174; CoreRpc.cs:86-101)、Insurgencyはcampaign snapshotsを読む (CoreRpc.cs:103; InsurgencyComposition.cs:80)。

### 旧未比較49 RPC（追補で詳細比較完了）

以下49件のrequest/result/state/notification bodyを現行mainで比較した。個別結果は末尾の追補に記載。未比較は0件。

- agentMgr 11: GetMissionBriefingInfo, GetMissionObjectiveInfo, GetMyJournalDetails, GetAgents, GetCompletedCareerAgentIDs, GetAgentLocationWrap, GetDisabledMissions, GetMessagesForEpicArcMissions, GetMyEpicArcStatus, GetPlausibleMissionIDs, GetSolarSystemOfAgent
- fighterMgr 10: GetFightersForShip, CmdAbandonFighter, CmdActivateAbilitySlots, CmdDeactivateAbilitySlots, CmdScoopAbandonedFighterFromSpace, ExecuteMovementCommandOnFighters, LaunchFightersFromTubes, LoadFightersToTube, RecallFightersToTubes, UnloadTubeToFighterBay
- shipCosmeticsMgr 9: ActivateSkinLicense, ApplySkinToShip, GetEnabledCosmetics, GetFirstPartySkinData, GetLicencedSkins, GetLicencedSkinsForShipType, GiveSkin, GMExpireSkinLicense, RemoveSkin
- dungeon 5: DEGetArchetypes, DEGetDungeons, DEGetFactions, DEGetRoomObjectPaletteData, DEGetRooms
- dungeonInstanceCacheMgr 14: GetCombatAnomalyInstances, GetCombatAnomaliesCount, GetSignatureInstances, GetIceBeltInstances, GetIceBeltsCount, GetOreAnomalyInstances, GetOreAnomaliesCount, GetOreAnomaliesCountInRange, GetBulwarkMiningSiteCounts, GetFactionWarfareInstances, GetFactionWarfareBattlefieldInstances, GetHomefrontSiteInstances, GetPirateInsurgencyInstances, GetPirateInsurgencyIceHeistInstances

旧EveJS-only44件は末尾の分類台帳へ移した。現行未登録名は42件。別service対応は実際のclient forwardingを確認できた3件だけである。

## 未確認と作業境界

- 継続監査開始時はmain/52b8421fでsource dirtyなし（他担当の未追跡artifactあり）。途中に親のCharacter/Skills/Crate source/testの作業中差分が増えた。旧dirty snapshotを現行不具合の根拠にせず、対象bodyはcanonical checkoutから再取得した。
- CBM graphは探索用。exact-path freshnessのmetadata_changedを確認した4ファイルはSerenaの現在bodyを読んだ。EveJSは指定828823a root、eve-logicは上記固定root/HEADを参照。
- MCPのSerena file-creation toolはこの接続の公開tool一覧に存在しなかったため、依頼されたこの文書だけPowerShellで作成した。source/test fileは編集せず、テスト・live server・graphical client・restartは実行していない。Gitはroot/branch/HEAD/statusのread-only確認だけ。build/test/Git mutationは行っていない。

## 補足: 検出した応答・error・Host差分

### blueprintManagerの無効ID

GetBlueprintData(blueprintID)でitemが見つからない、blueprint以外、またはsessionに非所有の場合、HostはIndustryBlueprintNotFound UserErrorをthrowする (BlueprintManagerService.cs:31-41)。EveJSのgetBlueprintByItemIDはその場合nullを返し (industryRuntimeState.js:1252-1261)、handlerは空objectをbuildBlueprintInstancePayloadへ渡してdefault値のKeyValを返す (blueprintManagerService.js:31-35; industryPayloads.js:92-111)。同じ入力に対する応答が異なる。通常のclientが無効IDを送る頻度は確認していない。

### alertのtrace IDと未登録internal RPC

BeanCountの(None,None)とSendClientStackTraceAlertの2要素応答shapeはHost/EveJSで対応する。EveJSはstackHash/stackKeyから既存errorIDを解決し、CLIENT_LOG_MODEを返す (alertService.js:313-330)。Hostは呼出し毎にInterlocked.Incrementで新しいIDを払い出し、modeを"D"固定で返す (ClientStartupServices.cs:23-33)。client alert.pyは返却値をstack hashにcacheする (carbon/common/script/sys/alert.py:494-520)。IDがstack固有に安定するか、log modeが設定値になるかはHostと異なる。

EveJS-onlyの3件はclient modeから実際にProxySvc('alert')へ呼ばれる。旧「server内部だけ」は誤りで訂正する。eve-logic/carbon/common/script/sys/alert.py:346-357がBeanDelivery/GroupBeanDelivery、同:797-807がGetLogModeForError。通常のゲームRPCではないが、client telemetry経路の未実装である。

### method-not-registered時のwire error

PacketDispatcherはCallAsyncより先にIService.HasMethodを検査し、falseの場合はAttributeErrorをwrapして送る (PacketDispatcher.cs:76-246)。したがって、EveJS-only methodは一般のservice callではCallAsync内部のdefault returnに到達せず、method-not-registered errorになる。agentMgrのnested callは現行HasMethodで検査し、未登録名や再帰bindだけを拒否する。既登録Briefing/Objectiveをnestedで呼ぶ経路はaab0dd10で修正済み。../../../dotnet/src/Eve.Services.Core/AgentMissionService.cs:203-231。

### cacheと揺れる値

cache.GetServerVersionAndBuildはHostが(server BootVersion, BootBuild)の2要素tuple、EveJSがruntime configの(boot_version, boot_build)配列を返す (CoreCompatibilityServiceAdapters.cs:39-43; cacheService.js:11-25)。構成値そのものの一致は未実測。JS生array→PyTupleはEveJS marshal.js:213-217でsource確認した。Cacheはmethod body比較数に含めるが、wire containerの最終形は未確認。

### cosmetics/paperDollの呼出し経路

shipCosmeticsMgrのAmerican spelling alias2件はHost未登録。ただしV24 client local cosmeticsSvc.GetLicensedSkinsがremote GetLicencedSkinsへ、_GetLicensedPersonalSkinsForTypeがremote GetLicencedSkinsForShipType(session.charid,typeID)へforwardすることを確認した。eve-logic/cosmetics/client/cosmeticsSvc.py:113-129。旧remote mapping未確認を解消し、通常の取得フローについてname gapの破損扱いをしない。American spellingを直接RPCで送ればAttributeErrorになる。

paperDollServer.ConvertAndSavePaperDollはEveJS自体がNoneを返すだけ（evejs-v0.12.9/server/src/services/character/paperDollServerService.js:214-217）。Hostにないが現行client callsiteを検出できず、legacyでclient未確認として扱う。

### 検査対象外のruntime受入

この監査ではRPC name、source handler、Host provider、client callsiteを静的に照合した。EveJS同一シナリオ実行、PostgreSQL write/readback、race/retry/restart、実V24.01 clientでの受信・画面更新は未実施である。

### 登録境界

HostCompositionは明示的に組み立てたservicesとcharacterServicesの各要素をregistryへ登録する (HostComposition.cs:123-128)。CompatibilityRpcServices.csのfallback classはその配列に含まれず、同じservice IDはInventoryRpc.cs:45-69のEve.Host実装が登録される。したがって互換比較のH/E列はCore fallbackのRPC数ではなく、registryへ実際に渡されるHost classのRPC数で数えた。

## 継続監査追補 — 取得基点52b8421f / 最終main dda03ca0

### Coverageと所属の確定

旧未比較49件を下の5表で全件比較した。旧EveJS-only44件を別の44行台帳で分類した。現行追加2件のResolve/Triglavianもbodyを比較し、未比較は0件。source比較済と実クライアント動作確認済は異なる。空payloadを一致と数えるには、同じ意味のstateが本当に空である必要がある。

RPC所属はファイル名で見積もりしていない。Service属性付きC# partial型、またはIService.Name/HasMethod、実際にHostへ渡すinstanceを追って下表の型を確定した。Host partial composition型はすべてEve.Host.CharacterServicesCompositionであり、CoreRpc/AgentRpc/DungeonRpcなどのファイル名は所属service名ではない。通常のPG-enabled Host compositionを集計対象にした。PG無設定のminimal Host branchはcharacterServices=[]で、同じ186 RPCの構成ではない。

根拠: ../../../dotnet/src/Eve.Host/Hosting/HostComposition.cs:74,114-128（factoryとregistry登録）、../../../dotnet/src/Eve.Host/Rpc/CharacterServiceList.cs:41-72（Core/Dungeon/Inventory/Fitting等を合成）、../../../dotnet/src/Eve.Host/Rpc/AgentRpc.cs:65-68、../../../dotnet/src/Eve.Host/Rpc/CoreRpc.cs:70-110、../../../dotnet/src/Eve.Host/Rpc/DungeonRpc.cs:41-49、I:41-69、../../../dotnet/src/Eve.Host/Rpc/FittingRpc.cs:23-27。Fitting三型はFittingMgrServices.cs:301/327/364のService属性で区別した。

| Service ID | 実登録CLR/C#型 | 確定経路 |
|---|---|---|
| accessGroupBookmarkMgr | Eve.Services.Core.AccessGroupBookmarkMgrService（BookmarkRefusals wrapper） | Service属性 / CharacterServiceList |
| agentMgr | Eve.Services.Core.AgentMissionService | Name+HasMethod / MissionComposition→AgentRpcServices |
| air_npe | Eve.Services.Core.AirNpeService | Name+HasMethod / ProgressRpc |
| blueprintManager | Eve.Services.Core.BlueprintManagerService | Service属性 / IndustryRpc |
| browserLockdownSvc | Eve.Services.Core.BrowserLockdownService | Service属性 / CoreServices.Create |
| calendarMgr | Eve.Services.Core.CalendarMgrService | Service属性 / CoreServices.Create |
| calendarProxy | Eve.Services.Core.CalendarProxyService | Service属性 / CoreServices.Create |
| securityMgr | Eve.Services.Core.SecurityMgrService | Service属性 / CoreRpc |
| alert | Eve.Services.Core.AlertService | Service属性 / CoreRpc |
| fighterMgr | Eve.Services.Core.FighterMgrService | Service属性 / CoreRpc→FighterProvider |
| shipKillCounter | Eve.Services.Core.ShipKillCounterService | Service属性 / CoreRpc |
| bannedwords | Eve.Services.Core.BannedWordsService | Service属性 / CoreServices.Create |
| alphaInjectorMgr | Eve.Services.Core.AlphaInjectorMgrService | Service属性 / CoreServices.Create |
| cache | Eve.Services.Core.CacheService | Service属性 / CoreServices.Create |
| communityTrackMgr | Eve.Services.Core.CommunityTrackMgrService | Service属性 / CoreServices.Create |
| contextualOfferMgr | Eve.Services.Core.ContextualOfferMgrService | Service属性 / CoreServices.Create |
| localizationServer | Eve.Services.Core.LocalizationServerService | Service属性 / CoreServices.Create |
| kiringMgr | Eve.Services.Core.KiringMgrService | Service属性 / CoreServices.Create |
| FastCheckoutService | Eve.Services.Core.FastCheckoutService | Service属性 / RewardRpc |
| charFittingMgr | Eve.Services.Core.CharFittingMgrService | Service属性 / FittingRpc |
| corpFittingMgr | Eve.Services.Core.CorpFittingMgrService | Service属性 / FittingRpc |
| allianceFittingMgr | Eve.Services.Core.AllianceFittingMgrService | Service属性 / FittingRpc |
| shipCosmeticsMgr | Eve.Host.ShipCosmeticsMgrRpcService | Name+HasMethod / InventoryRpc |
| characterEnergyMgr | Eve.Host.CharacterEnergyMgrRpcService | Name+HasMethod / InventoryRpc |
| paperDollServer | Eve.Host.PaperDollServerRpcService | Name+HasMethod / InventoryRpc |
| planetMgr | Eve.Host.PlanetMgrRpcService | Name+HasMethod / InventoryRpc |
| crateService | Eve.Services.Core.CrateService | Name+HasMethod / InventoryRpc |
| dungeon | Eve.Services.Core.DungeonService | Name+HasMethod / DungeonRpc |
| dungeonInstanceCacheMgr | Eve.Services.Core.DungeonInstanceCacheMgrService | Name+HasMethod / DungeonRpc |
| facilityManager | Eve.Services.Core.FacilityManagerService | Name+HasMethod / IndustryRpc |
| RWManager | Eve.Services.Core.RWManagerService | Name+HasMethod / CoreRpc |
| fwCharacterEnlistmentMgr | Eve.Services.Core.FwCharacterEnlistmentMgrService | Name+HasMethod / CoreRpc |
| fwVictoryPointMgr | Eve.Services.Core.FwVictoryPointMgrService | Name+HasMethod / CoreRpc |
| insurgencySolarsystem | Eve.Services.Core.InsurgencySolarsystemService | Name+HasMethod / CoreRpc |

### 追補のsource参照凡例

以下の A:120 はこの凡例の絶対pathの1-based line 120を意味する。Serenaの0-based行は1を足した。EveJS/CCP参照rootもCBM index_statusのcanonical_rootで確認済み。source本文を取得したpathがworktree1e47を指すものは本監査に使っていない。

- **A** = [../../../dotnet/src/Eve.Services.Core/AgentMissionService.cs](../../../dotnet/src/Eve.Services.Core/AgentMissionService.cs)
- **MV** = [../../../dotnet/src/Eve.Services.Core/MissionViews.cs](../../../dotnet/src/Eve.Services.Core/MissionViews.cs)
- **MC** = [../../../dotnet/src/Eve.Host/Composition/MissionComposition.cs](../../../dotnet/src/Eve.Host/Composition/MissionComposition.cs)
- **AO** = [../../../dotnet/src/Eve.Services.Core/AgentMissionStoreRecords.cs](../../../dotnet/src/Eve.Services.Core/AgentMissionStoreRecords.cs)
- **AJ** = [evejs-v0.12.9/server/src/services/agent/agentMgrService.js](evejs-v0.12.9/server/src/services/agent/agentMgrService.js)
- **MR** = [evejs-v0.12.9/server/src/services/agent/agentMissionRuntime.js](evejs-v0.12.9/server/src/services/agent/agentMissionRuntime.js)
- **F** = [../../../dotnet/src/Eve.Services.Space/SpaceFighters.cs](../../../dotnet/src/Eve.Services.Space/SpaceFighters.cs)
- **FJ** = [evejs-v0.12.9/server/src/services/fighter/fighterMgrService.js](evejs-v0.12.9/server/src/services/fighter/fighterMgrService.js)
- **FR** = [evejs-v0.12.9/server/src/services/fighter/fighterRuntime.js](evejs-v0.12.9/server/src/services/fighter/fighterRuntime.js)
- **S** = [../../../dotnet/src/Eve.Host/ShipCosmeticsMgrRpcService.cs](../../../dotnet/src/Eve.Host/ShipCosmeticsMgrRpcService.cs)
- **SJ** = [evejs-v0.12.9/server/src/services/ship/shipCosmeticsMgrService.js](evejs-v0.12.9/server/src/services/ship/shipCosmeticsMgrService.js)
- **SS** = [evejs-v0.12.9/server/src/services/ship/shipCosmeticsState.js](evejs-v0.12.9/server/src/services/ship/shipCosmeticsState.js)
- **I** = [../../../dotnet/src/Eve.Host/Rpc/InventoryRpc.cs](../../../dotnet/src/Eve.Host/Rpc/InventoryRpc.cs)
- **D** = [../../../dotnet/src/Eve.Services.Core/DungeonRpcServices.cs](../../../dotnet/src/Eve.Services.Core/DungeonRpcServices.cs)
- **DJ** = [evejs-v0.12.9/server/src/services/dungeon/dungeonService.js](evejs-v0.12.9/server/src/services/dungeon/dungeonService.js)
- **CJ** = [evejs-v0.12.9/server/src/services/dungeon/dungeonInstanceCacheMgrService.js](evejs-v0.12.9/server/src/services/dungeon/dungeonInstanceCacheMgrService.js)
- **DR** = [../../../dotnet/src/Eve.Host/Persistence/Incursions/PersistenceDungeonReadModel.cs](../../../dotnet/src/Eve.Host/Persistence/Incursions/PersistenceDungeonReadModel.cs)
- **P** = [../../../dotnet/src/Eve.Host/PlanetMgrRpcService.cs](../../../dotnet/src/Eve.Host/PlanetMgrRpcService.cs)
- **PJ** = [evejs-v0.12.9/server/src/services/planet/planetMgrService.js](evejs-v0.12.9/server/src/services/planet/planetMgrService.js)
- **AL** = [evejs-v0.12.9/server/src/services/alert/alertService.js](evejs-v0.12.9/server/src/services/alert/alertService.js)
- **NK** = [evejs-v0.12.9/server/src/services/ship/shipKillCounterService.js](evejs-v0.12.9/server/src/services/ship/shipKillCounterService.js)
- **PD** = [evejs-v0.12.9/server/src/services/character/paperDollServerService.js](evejs-v0.12.9/server/src/services/character/paperDollServerService.js)

### 49 RPC — 入力・shape・通知・state owner詳細

kwargsは以下に明記したGiveSkinとDungeon filters以外、対象bodyでは使われない。binderのnested kwargsはAgentMissionServiceがServiceCallへそのまま引き継ぐ。read RPCでも、state source未接続やcache envelopeの差があるものを「互換一致」とは扱わない。JS生arrayはPyTupleとしてencodeされる（evejs-v0.12.9/server/src/network/tcp/utils/marshal.js:213-217）。explicit buildList/PyListとの違いも区別した。

#### agentMgr（11/11）

HostはMC:194-201のstatic callbacks、MC:215-239のGameStore/Pg mission overlay/research reader、MC:413-436のheld mission/Fetch/Courier ownerへ接続。bound agentはA:86-87のOID mapで得る。いずれもこの11 read RPC自身から通知は送らない。missionのmutation通知は別のDoAction/announcer側であり、read resultの不足を通知が自動で補うとは数えない。

| RPC | 具体的入力・kwargs | 戻り値・差分判定 | 通知/Host state owner | body根拠 |
|---|---|---|---|---|
| GetMissionBriefingInfo | bound A、argsなし（任意の旧char/content引数をbodyは使わない） | dict/Noneの外形は共通。Hostはkeywords={}、AcceptTimestamp/Expiration/Decline/Image=None。accepted missionの時刻と置換語がEveJSと異なる。 | ReadHeldMission→Pg AgentMissionStore。readのみ、通知なし。 | A:120-127; MV:30-40; AJ:1121-1128; MR:5780-5838 |
| GetMissionObjectiveInfo | bound A、argsなし | dict/None。Hostはkill dungeon location=(type1531,system0,location0)、locations=[]、LP0、bonusRewards=[]。fetch/transport tupleはあるが実location/rewards投影が部分的。 | Pg held mission + Fetch/Courier callbacks。readのみ、通知なし。 | A:120-127; MV:42-117; MC:413-447; AJ:1139-1146; MR:5884-6169 |
| GetMyJournalDetails | unboundまたはbound A、argsなし | tuple(list missionRows,list researchRows)。Hostはbound Aでも全agentを返す（EveJSはfilter）。R&D readはlegacy GameStoreで、Pg research writerと不一致。 | MC:215-239のGameStore+Pg mission overlay。通知なし。 | A:293-331,622-702; AJ:868-878; MR:7529-7563 |
| GetAgents | argsなし | mutable util.Rowset、19 headerと各PyList row。AgentRow修正を確認。agent順序を整列するがID順以外のclient依存は見つからず。 | MC:194、SDE/client agent static rows。通知なし。 | A:57-63,364-372,456-477; AJ:787-833 |
| GetCompletedCareerAgentIDs | ([agentID,...],)、空/無効IDは除外 | numeric-key dict bool、指定IDだけ返す外形は共通。completedCareerAgentIDsはlegacy stateを読む。Pg completion→このmapのwriterは未検出、進行後trueは未保証。 | missionRuntimeState + mission overlay。read、通知なし。 | A:374-390; MC:215-221; AO:17-39; AJ:840-848; MR:7571-7590 |
| GetAgentLocationWrap | bound A、argsなし | dict。station/in-space/systemの枝を照合。Host in-spaceはsourceBuild fieldなし。未bound/unknown agentはHost UserError（EveJSはnormalization側のdefault）。 | ReadAgents SDE。read、通知なし。 | A:113-178; AJ:1165-1171; MR:6209-6244 |
| GetDisabledMissions | argsなし | list positive missionID。kwargs不使用。データはstatic contentから供給される。 | MC:195。read、通知なし。 | A:392-400; AJ:850-852 |
| GetMessagesForEpicArcMissions | argsなし | Host raw dict(str mapName→numeric-key dict)。EveJSは同じ論理mapをCachedMethodCallResult(versionCheck=never,proxyCache=true)で包む。cache/wire policy差、client破損は未確定。 | MC:198のEpicArcMessageMaps。read、通知なし。 | A:402-416,498-511; AJ:894-904 |
| GetMyEpicArcStatus | session character、argsなし | numeric arcID→numeric missionID→4-field tuple。legacy progress shapeは対応。Pg overlay RecordにisEpicArc/epicArcID/clientMissionRecordがなく、新規Pg epic missionからはarc statusを作れない。 | legacy epicArcProgress。Pg authoritative progressの投影不足。通知なし。 | A:418-425,513-618; AO:50-67; MC:273-279; AJ:888-892; MR:7565-7569 |
| GetPlausibleMissionIDs | (agentID,)、無効ID→empty list | positive missionID list。Host static callbackに実接続。kwargs不使用。異なるagent/content corpusの値は比較対象データを揃える必要がある。 | MC:196-197 static content。read、通知なし。 | A:427-436; AJ:880-886 |
| GetSolarSystemOfAgent | (agentID,)、unknown→None | int/None。callback→agent static dictionary、EveJS runtimeと同じ読取り目的。 | MC:199-200。read、通知なし。 | A:438-454; AJ:835-838 |

#### fighterMgr（10/10）

[Service("fighterMgr")] partial型の10 handlerは../../../dotnet/src/Eve.Services.Core/ClientStartupServices.cs:46-131からISpaceFightersへ委譲される。実Host providerはCoreRpc.cs:42-51のSpaceFightersで、sim、CharacterShips、Dogma、SolarSystem/Security sourceを渡しPilotEntered→RebuildAsyncを設定する。tube/inventory mutationはCharacterShips/PgItemStoreのtransaction、flight/effectはRust simulation、ability stateはitem customInfoと_slotsで管理する。unsupported simulationをfake successにしない拒否はEveJSとの差であり、その差だけで破損と断定しない。

CCP client contract: eve-logic/eve/client/script/parklife/fightersSvc.py:98-109,234-235、eve-logic/eve/client/script/ui/inflight/squadrons/shipFighterState.py:201-230。abilityStatesは初期状態更新の実入力である。

| RPC | 具体的入力・kwargs | 戻り値・差分判定 | 通知/Host state owner | body根拠 |
|---|---|---|---|---|
| GetFightersForShip | argsなし、session ship/character/system | tuple(list tuple4 tubes,list tuple4 inSpace,dict abilityStates)。Host第三dictが常に{}。no shipはHost UserError、EveJSはempty tuple。 | CharacterShips/Pg inventory、_launched/_slots、Rust sim。snapshot read、通知なし。 | F:85-103,1084-1095; FJ:267-307; FR:2135-2160 |
| CmdAbandonFighter | (fighterID,) | 成功bool。Host invalid fighterをUserError、EveJS false。EveJS raw listもnormalizeするがCCP callerはscalar。 | Rust Abandon→OnReceived/ReleasedTo→controller removal/EMPTY。DBとruntime処理を追跡。 | F:582-597,739-785,982-986; FJ:453-459; FR:4870-4893 |
| CmdActivateAbilitySlots | ([fighterIDs],slotID,targetIDまたはXYZ) | dict(fighterID→None/UserError payload)。slot/charge/cooldown/security/effect validationあり。正常・invalid slot・no simの枝を比較。 | _slots + type/ability catalog + Rust effects。Keepでitem customInfo保存。activation noticeを50ms遅延（EveJS queue）。順序保証はruntime未確認。 | F:201-215,234-299,389-404; FJ:420-435; FR:4416-4658 |
| CmdDeactivateAbilitySlots | ([fighterIDs],slotID) | dict(fighterID→None/error)。非activeの拒否あり。Host sim cycleとstate.Stop、EveJS cooldown再設定/即時active clearにはタイミング差。 | Rust Deactivate、_slots。OnFighterAbilitySlotDeactivatedは遅延queue。exact timing未実測。 | F:218-229,319-364; FJ:437-451; FR:4660-4752 |
| CmdScoopAbandonedFighterFromSpace | (fighterID,toFlagID=158またはtube) | bool。Hostは同じchar/shipが放棄し、_abandonedに残るものに限定。EveJSはsceneのcontroller-free fighterを距離/scopeで判定。他人の放棄/restart後を同等とは数えない。 | Pg Scoop→Rust確認→失敗時release、ItemChange→tube content→READY。 | F:600-645; FJ:461-468; FR:5028-5124; ../../../dotnet/src/Eve.Ships/CharacterShips.Fighters.cs:198-250 |
| ExecuteMovementCommandOnFighters | ([fighterIDs],STOP/ORBIT/FOLLOW/GOTO_POINT,target/range/XYZ) | 正常None。Host commandはcase-sensitive、EveJS trim+uppercase。Host無対象/invalidをUserError、EveJS skip→handler None。正規CCP入力の命令typeは共通。 | Rust order/point order。EveJS recall取消時INSPACE notice、Host exact cancellation effectsはsim側確認が必要。 | F:503-576; FJ:409-418; FR:4781-4868 |
| LaunchFightersFromTubes | ([tubeFlags],) | dictは失敗tubeだけを列挙。Host no-pilotはthrow、EveJS no-host empty errors。success shape共通。 | Pg tube→space→Rust spawn、拒否時tubeへ戻す。ItemChange→ContentEmpty→AddedToController→INSPACE。 | F:143-157,652-737; FJ:387-393; FR:3693-3858 |
| LoadFightersToTube | (fighterID,tubeFlagID) | 成功True。Host拒否はUserError、EveJS false。inventory capacity/owner/tube classの検査は実storeへ接続。 | Pg CharacterShips move commit→ItemChange→ContentUpdate→READY。 | F:105-125; FJ:309-350; ../../../dotnet/src/Eve.Ships/CharacterShips.Fighters.cs:43-83 |
| RecallFightersToTubes | ([fighterIDs],) | dictは全要求fighterID→None/error。Host invalid/no simのrefusalもper ID。 | Rust recall→RECALLING、land eventでLANDING→ItemChange→RemovedFromController→ContentUpdate→READY。EveJSはrecall時ability deactivateも行う。 | F:160-195,959-978; FJ:395-407; FR:4050-4137 |
| UnloadTubeToFighterBay | (tubeFlagID,) | Host成功None、EveJS True。失敗Host UserError/EveJS false。client wrapperは返すが上位がboolで分岐する証拠は未確認。単独で破損と断定しない。 | Pg move commit→ItemChange→ContentEmpty→EMPTY。 | F:127-140; FJ:352-385 |

#### shipCosmeticsMgr（9/9）

Core fallbackでなくSのEve.Host型をI:45-66で登録。license/logos/ship skinはGameStoreCache、consumeはPgItemStore/Npgsql transaction、catalogはShipCosmeticsCatalog。license有効性はrevoked/expiry/SDE existenceで判定する。bytesの実captureは未実施だが、notification builderまでsourceを追った。

| RPC | 具体的入力・kwargs | 戻り値・差分判定 | 通知/Host state owner | body根拠 |
|---|---|---|---|---|
| ActivateSkinLicense | ([itemIDs],)、scalar/tupleもnormalize | None。所有者/有効license/既active/同skin重複を検査。license state先書き→Pg inventory transaction→失敗rollback。通知の引数数に破損（下記）。 | GameStore shipCosmetics + PgItemStore。commit後各itemのOnItemChange→OnSkinLicenseActivated。 | S:190-260; I:54-66; SJ:391-468 |
| ApplySkinToShip | (shipID,skinIDまたはNone) | None/UserError。64-bit shipIDがHost PositiveでInt32へclampされる。valid大IDのowned shipを誤参照。 | GameStore /ships、Pg items。Host自己sessionへCosmeticsChanged(empty dict)→CurrentShipSkinChange、EveJSはgateway noticeとlive slim refresh。observer更新は未確認。 | S:160-188,294; I:45-52; SJ:308-349; SS:484-556 |
| GetEnabledCosmetics | (shipID,) | dict backendSlot→typeID。slot mapの型は共通。shipID clampによりlarge IDは空/別shipのmapとなる。 | GameStore shipLogoFittings enabledByBackendSlot。read、通知なし。 | S:63-77,294; SJ:250-259 |
| GetFirstPartySkinData | (licenseeID,skinID) | KeyVal/None。6 fields skinID/expires/isSingleUse/licenseTypeID/skinMaterialID/materialIDを比較。unknown/revoked/expired→None。 | GameStore override + catalog、通知なし。 | S:106-110,253-277; SJ:298-306,53-67; SS:260-291 |
| GetLicencedSkins | argsなし、session char | list(KeyVal6 fields)、有効overrideだけ。Host catalog SkinId順、EveJS numeric override keys。expiry Filetimeの現在時刻差は除外。 | shipCosmetics override/catalog。read、通知なし。 | S:79-104,253-277; SJ:261-268; SS:293-310 |
| GetLicencedSkinsForShipType | (typeID,)または(charID,typeID) | list(KeyVal6 fields)。二つのsignatureをHost/EveJSとも受付、invalid type→empty。 | override + catalog shipTypeIDs、read、通知なし。 | S:82-104; SJ:274-292 |
| GiveSkin | (skinID,)、kwargs duration,isSingleUse | None。kwargs不指定/0でEveJSはpermanent,false、HostはSDE license duration/isSingleUseをfallback。bad skin Host throw/EveJSログ後None。GM/default policy差として記録。 | override書込み。通知なし。 | S:112-135; SJ:351-365; SS:327-384 |
| GMExpireSkinLicense | (skinID,) | None。両方request時にexpiredとなる。EveJS past1sec、Host past1tickは本質差としない。未保有overrideはHost no write/EveJS expired override作成。 | override mutation、通知なし。 | S:146-158; SJ:381-389; SS:462-482 |
| RemoveSkin | (skinID,licenseeID) | None。別owner拒否は共通。valid skin未保有ではHost CustomInfo、EveJS revocation recordを書きNone。通常取得済owner操作と区別。 | override revoked書込み、通知なし。 | S:137-144,279-286; SJ:367-379; SS:440-460 |

#### dungeon（5/5）

DungeonRpc.cs:48がDR.ReadEditorCatalogAsyncを実providerとして渡す。D:47-124の全switchとfiltersを比較。HostはSDE、EveJSはeditor fixtureをauthorityとする。editor fixtureをHostへコピーすべきという判断はしない。部屋/palette未供給はdeveloper/editor機能の未実装であり、通常のclientゲーム操作破損とは分ける。

| RPC | 具体的入力・kwargs | 戻り値・差分判定 | 通知/Host state owner | body根拠 |
|---|---|---|---|---|
| DEGetArchetypes | args不要 | list(tuple(name,id))。HostはSDE archetypes、EveJSはeditorFixtures。same inputでもcatalog corpus差は意図したデータ源差。 | DRのcached SDE catalog、read、通知なし。 | D:47-81; DJ:190-193 |
| DEGetDungeons | positional dungeonID、kwargs dungeonID/archetypeID/factionID、None/'All' | list(KeyVal dungeonID/dungeonNameID/dungeonName/factionID/archetypeID)。filtersとnull handlingを比較。 | SDE dungeonsをread、通知なし。 | D:83-100; DJ:149-165,172-188 |
| DEGetFactions | args不要 | list(int factionIDs)。HostはSDE sorted、EveJS fixture。kwargs使わない。 | SDE factionsをread、通知なし。 | D:69-74; DJ:195-198 |
| DEGetRoomObjectPaletteData | args不要 | dict。Host providerは常にempty palette、EveJS fixture paletteあり。editor/dev data未供給。 | DR:146-152はpaletteをemptyで構築、通知なし。 | D:76; DJ:210-213; DR:114-152 |
| DEGetRooms | positionalまたはkwarg dungeonID | list(tuple(roomID,name))、unknown→empty。Host provider RoomsByDungeonIdが空で有効dungeonでもempty。EveJS fixture roomsを返す。 | DR catalogにroom sourceがない。editor/dev data未供給、通知なし。 | D:102-110; DJ:200-208; DR:114-152 |

#### dungeonInstanceCacheMgr（旧14/14、現行は追加Triglavianを含め15）

DungeonRpc.cs:49→DR.ReadCacheSnapshotAsync。DR:38-75がContentRuntime anomaly/signatureのactive rowsとGameStore dungeonRuntimeStateをinstanceIDでmergeし、DR:114-152のSDE dungeon catalogでhydrateする。EveJS側はshared active summaries、archetype buckets、resource authority、visible wormholesを投影する。全対象はreadで通知なし。C#/EveJSのKeyVal8 fieldsはD:376-399/CJ:469-504で照合した。scope/lifecycleはsharedかつseeded/active/paused、並びはsystem/instance/dungeon順。追加text検索のtimeout後、CBM symbol snippetでCJ:33-51のARCHETYPES/ARCHETYPE_SETSを回収した。combat24/ore27/ice28/invasion65/homefront70、FW33/34/35/36/68、pirate72..79、iceHeist74の定数も照合済み。static family/shape比較とactual world populationの検証は区別する。

| RPC | 具体的入力・kwargs | 戻り値・差分判定 | 通知/Host state owner | body根拠 |
|---|---|---|---|---|
| GetCombatAnomalyInstances | args/kwargsなし | dict systemID→list(KeyVal8 fields + optional position)。active/shared/anomaly/archetype24、順序とhydrate fallbackを比較。 | ContentRuntime + dungeonRuntimeState + SDE、read、通知なし。 | D:165,186-203,376-399; CJ:469-484,563-582,629-632 |
| GetCombatAnomaliesCount | args/kwargsなし | dict systemID→int count、同じeligibility/archetype24。 | 同じsnapshot、read、通知なし。 | D:166,205-210; CJ:606-622,634-637 |
| GetSignatureInstances | args/kwargsなし | dict systemID→int count。signature active countsは対応。Host visible wormhole count入力がemptyでwormhole追加分を落とす。 | DR snapshot追加3mapがempty、read、通知なし。 | D:167,212-227; DR:70-75; CJ:301-321,639-642 |
| GetIceBeltInstances | args/kwargsなし | dict systemID→list(KeyVal+optional targetID/position)。Host requireIceAuthority=trueでauthority emptyのため現行Hostはice entriesを落とす。 | DR empty IceSystemAuthority、read、通知なし。 | D:168,186-203,354-356; DR:70-75; CJ:389-402,486-504,644-647 |
| GetIceBeltsCount | args/kwargsなし | dict systemID→int、authority slotとdepletion deadlineで計算。Host authority emptyかつactive iceもauthority filterで落ちる。 | DR未配線、時計deadlineはID/時刻揺れでなくavailability state差。 | D:169,229-266; CJ:228-299,649-652 |
| GetOreAnomalyInstances | args/kwargsなし | dict systemID→list(KeyVal)、archetype27。active hydrated instancesは共通shape。 | Content + GameStore/SDE、read、通知なし。 | D:170,186-203; CJ:654-657 |
| GetOreAnomaliesCount | args/kwargsなし | dict systemID→int。EveJS universe planned spawn countにactive countをoverlay。Host planned count入力emptyで未materialized systemsが欠落。 | DR empty OreAnomalySpawnCounts、read、通知なし。 | D:171,229-266; DR:70-75; CJ:269-299,659-662 |
| GetOreAnomaliesCountInRange | 両bodyは範囲引数/kwargsを使わない | 上記と同じdict。range filteringを追加したとの見積もりをしない。planned map未配線は同じ。 | same resource count reader、通知なし。 | D:171; CJ:664-667 |
| GetBulwarkMiningSiteCounts | args/kwargsなし | dict systemID→int。8 dungeon IDs、metadata/runtimeFlags/family/originによる判定。raw active summariesを使う点を比較。 | shared anomalies snapshot、通知なし。 | D:140,172,268-290; CJ:323-360,669-672 |
| GetFactionWarfareInstances | args/kwargsなし | dict systemID→list(KeyVal)。archetypes33/34/35/36/68。 | snapshot SDE hydrate、read、通知なし。 | D:133,173,186-203; CJ:674-677 |
| GetFactionWarfareBattlefieldInstances | args/kwargsなし | dict systemID→list(KeyVal)。archetype68。 | snapshot read、通知なし。 | D:134,174; CJ:679-682 |
| GetHomefrontSiteInstances | args/kwargsなし | dict systemID→list(KeyVal)。archetype70。 | snapshot read、通知なし。 | D:135,175; CJ:684-687 |
| GetPirateInsurgencyInstances | args/kwargsなし | dict systemID→list(KeyVal)。archetypes72..79。 | snapshot read、通知なし。 | D:136,176; CJ:689-692 |
| GetPirateInsurgencyIceHeistInstances | args/kwargsなし | dict systemID→list(KeyVal)。archetype74。 | snapshot read、通知なし。 | D:137,177; CJ:694-697 |

追加1: GetTriglavianSiteInstancesもD:157,181でHasMethod/dispatchされ、archetype65→GroupedEntriesへ接続。CJ:699-702と同じdict(list KeyVal)のfamily。旧「未登録」は撤回。client/runtime到達は未実測。

### 旧EveJS-only44件 — 全件分類台帳

「未実装」はEveJSの当該RPC bodyとCCP callerがあり、現行Hostの同serviceに経路がないもの。「既存別serviceで対応」はCCP local serviceが既存Host RPCへforwardする経路をsourceで確認したものだけ。「legacyでclient未確認」はserver/internal/old utility/alias等で通常client→remoteの到達を確認していないもの。legacyは不要という判定ではない。現行実装済2件は旧44件の再分類であり、現在のEveJS-only数42には含めない。

全Nameが既存別Host serviceへ直接移動しているとは確認できなかった。対応3件はclient-local lookup/forwardingである。直に旧RPC名を送ると未登録名42件は通常のPacketDispatcher:147-160でwrapped AttributeError。planetMgrのResolve/Bindには別serviceのagent binderを転用したとみなせない。agent nested未登録名はA:203-206のrefusalになる。static検索の0件はruntime不要の証明ではない。

全行の「agents.py」はeve-logic/eve/client/script/ui/station/agents/agents.py、「clientPlanet.py」はeve-logic/eve/client/script/environment/planet/clientPlanet.py、「planetSvc.py」はeve-logic/eve/client/script/environment/planetSvc.py、「cosmeticsSvc.py」はeve-logic/cosmetics/client/cosmeticsSvc.py、「alert.py」はeve-logic/carbon/common/script/sys/alert.pyの絶対root参照である。短名の他callsiteは先行詳細監査の固定eve-logic rootと同じ。

| # | Service/RPC | 分類 | 具体的入力→EveJS shape/通知 | 現行Host・CCP経路・再利用先 | 根拠 |
|---|---|---|---|---|---|
| 1 | agentMgr.GetAgentByID | 既存別serviceで対応 | (agentID,)→dict/None、EveJSはmaterialize→OnAgentAdded | V24 local svc.agents.GetAgentByIDはGetAgents().Clone/Indexを読む（eve-logic/eve/client/script/ui/station/agents/agents.py:89-122）。Host直RPC名は未登録。再利用A:GetAgents、MC agent static rows。 | AJ:1415-1423; A:67-107,364-372 |
| 2 | agentMgr.GetAgentStaticInfo | legacyでclient未確認 | (agentID,)→dict/None、materialize→OnAgentAdded | common notificationUtil.GetAgentのboot.role!=client枝のみ（eve-logic/eve/common/script/util/notificationUtil.py:84-98）。client直接呼出し未確認。Host未登録、static agent directoryを再利用候補。 | AJ:854-862; A:67-107 |
| 3 | agentMgr.GetDungeonShipRestrictions | 未実装 | bound、(dungeonID,gateID?)→dict restrictedShipTypes | activeMissionController/agent monikerのclient呼出しあり（eve-logic/eve/client/script/ui/shared/neocom/activeMissionController.py:118）。Host未登録。keeperのSDE gate restrictionsを共通投影する候補だが代替RPC完了とは数えない。 | AJ:1193-1200; A:67-107 |
| 4 | agentMgr.GetEntryPoint | 未実装 | bound、()→XYZ tuple/None | epicArcAgentContentPiece.py:128-129のclient呼出し。現行Host未登録。mission store/space siteのentry positionを使うadapterが必要。 | AJ:1202-1210; A:67-107; MC:413-420 |
| 5 | agentMgr.GetInfoServiceDetails | 未実装 | bound、()→dict/None | eve-logic/eve/client/script/ui/shared/info/infosvc.py:1998-2000。Host未登録、agent/held missionの既存read ownerが再利用候補。 | AJ:1173-1180; A:67-107 |
| 6 | agentMgr.GetMissionJournalInfo | 未実装 | bound、(charID?,contentID?)→dict/None（briefing/objectives等） | eve-logic/eve/client/script/ui/station/agents/agents.py:746-763。Host未登録。ReadHeldMission/MissionViewsを再利用しJournal wrapperを組む必要。Objectiveのpartial fieldsをそのまま完成扱いしない。 | AJ:1130-1137; A:67-107,120-127; MV:30-117 |
| 7 | agentMgr.GetMissionKeywords | 未実装 | bound、(contentID?)→dict | agents.py:626-638がlocalization substitutions用に呼ぶ。Host未登録、clientMissionMessages/agent static contextを使う候補。現在Briefingのkeywords={}では代替にならない。 | AJ:1148-1156; A:67-107; MV:32 |
| 8 | agentMgr.GetReplayTimestamp | 未実装 | bound、(characterID?)→Filetime long/None | agents.py:306-314のagent timers。Host未登録。Pg mission completed/failed historyを用いる候補（MC:273-276）。 | AJ:1400-1405; A:67-107 |
| 9 | agentMgr.GetStandingGainsForMission | 未実装 | bound、(missionID?)→numeric-key dict gain | eve-logic/evemissions/client/missioncontroller.py:223-225。Host未登録、mission reward + existing Relations standing ownerの投影が必要。 | AJ:1182-1191; A:67-107; MC:249-264 |
| 10 | agentMgr.GetStaticBlob | legacyでclient未確認 | (messageID)をbodyは使わず空string | eve-logic/eve/common/script/sys/strangeStrings.py:13-30のold string unpickle helper。現行UI→remote agentMgr経路は未確認。Host未登録。不要と断定しない。 | AJ:864-866; A:67-107 |
| 11 | agentMgr.GotoLocation | 未実装 | bound、(locationType,locationNumber?,referringAgentID?)→None | eve-logic/eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:401（元監査の同名callsite）。EveJS follow/goto pointを行う。Host agent RPCなし。SpaceSessions移動とmission targetを接続する候補。 | AJ:1212-1236; A:67-107 |
| 12 | agentMgr.IsCheatingWithAgent | 未実装 | bound、()→False | agents.py:549-550。Host未登録。EveJSの固定Falseもclientが使うcontract、unknown methodでの失敗を同値としない。 | AJ:1407-1409; A:67-107 |
| 13 | agentMgr.MachoResolveObject | 現行同service実装済 | (bindParams)→node int | 現行A:67,91で登録/dispatch。node IDの実数値はrouting config差として扱い、shape/bodyを比較済。carbon/common/script/net/moniker.py:111-165のresolve経路。 | AJ:771-775; A:67-107 |
| 14 | agentMgr.RemoveOfferFromJournal | 未実装 | bound、()→None、offer mutation | agents.py:782-784。Host未登録、AgentMissionStoreをownerとして削除/通知を接続する必要。DoAction(decline)と同一の操作とは数えない。 | AJ:1158-1163; A:67-107 |
| 15 | agentMgr.ShouldAlwaysAllowReplay | 未実装 | bound、()→False | agents.py:552-553。Host未登録、client replay gatingのcallがAttributeErrorになる。 | AJ:1411-1413; A:67-107 |
| 16 | agentMgr.WarpToAgentInSpace | 未実装 | (agentID?)→None、warp point/entity | eve-logic/eve/client/script/ui/shared/agencyNew/contentPieces/agentContentPiece.py:301-307。Host agent methodなし。SDE agent-in-spaceとSpaceSessions warp ownerが再利用候補。 | AJ:1350-1398; A:67-107 |
| 17 | agentMgr.WarpToLocation | 未実装 | bound、(locationType,number?,range,?,referringAgentID?)→None | CCP movementFunctions.WarpToBookmarkがagent pathを選ぶ（同file:415-430）。EveJSはmission site materialize→warp。別のbeyonce bookmark RPCがあるだけではこのcallを処理できない。 | AJ:1238-1305,1308-1348; A:67-107 |
| 18 | alert.BeanDelivery | 未実装 | (beans dict)→None、counts/audit更新 | client mode branchのProxySvc('alert') call（eve-logic/carbon/common/script/sys/alert.py:346-357）。Host AlertServiceは2 exportsのみ。telemetry aggregate ownerの接続が必要。 | AL:270-276; ../../../dotnet/src/Eve.Services.Core/ClientStartupServices.cs:15-44 |
| 19 | alert.GetLogModeForError | 未実装 | (errorIDs dict)→tuple(dict changes,list flooded) | client BeanDeliveryBoy→UpdateLogModes→ProxySvc call（alert.py:346-348,797-807）。EveJS empty resultも有効shape。Host未登録。 | AL:306-312; ../../../dotnet/src/Eve.Services.Core/ClientStartupServices.cs:15-44 |
| 20 | alert.GroupBeanDelivery | 未実装 | (compressed zlib marshal bytesまたはdict,nodeID?)→None | client telemetry details-mode delivery（alert.py:353-357）。EveJS decompress/aggregate/audit、Host未登録。runtime ID揺れではなくcall path欠落。 | AL:278-304; ../../../dotnet/src/Eve.Services.Core/ClientStartupServices.cs:15-44 |
| 21 | shipKillCounter.GetItemKillCountNPC | legacyでclient未確認 | (itemID,)→numeric count | CBM whole eve-logic exact-name検索でclient call未検出。HostはPlayerのみ。existing shipKillCounters readerをNPC projectionへ再利用できるが、同RPCを実装済とは数えない。 | NK:33-38; ../../../dotnet/src/Eve.Services.Core/ClientStartupServices.cs:133-161 |
| 22 | shipCosmeticsMgr.GetLicensedSkins | 既存別serviceで対応 | argsなし→list(KeyVal)、EveJS British alias forwarding | client local cosmeticsSvc.GetLicensedSkinsはremote GetLicencedSkinsを呼ぶ（eve-logic/cosmetics/client/cosmeticsSvc.py:113-117）。既存Host bodyに到達。American直RPC名は未登録。 | SJ:270-272; S:16,79-80 |
| 23 | shipCosmeticsMgr.GetLicensedSkinsForShipType | 既存別serviceで対応 | (typeID)または(charID,typeID)→list(KeyVal) | client _GetLicensedPersonalSkinsForTypeはGetLicencedSkinsForShipType(session.charid,typeID)へforward（cosmeticsSvc.py:124-129）。同じHost bodyに到達。American直名は未登録。 | SJ:294-296; S:16,82-90 |
| 24 | paperDollServer.ConvertAndSavePaperDoll | legacyでclient未確認 | argsを使わずNone、EveJS自身もno-op | whole eve-logic exact-name検索でcallsite未検出。Hostの6-method tableにはなし。必要性を実確認していないため未実装ゲームフローとは断定しない。 | PD:214-217; ../../../dotnet/src/Eve.Host/CharacterRpcServices.cs:55-80 |
| 25 | planetMgr.GetCommandPinsForPlanet | 未実装 | (planetID)またはbound→dict(ownerID→KeyVal6 fields) | eve-logic/eve/client/script/environment/planetSvc.py:110-118。Host P:27は3 namesだけ。planetRuntimeState colony/SDE groupを使うadapterが必要。 | PJ:1177-1193; P:27-39 |
| 26 | planetMgr.GetExtractorsForPlanet | 未実装 | (planetID)またはbound→list(KeyVal6 fields) | planetSvc.py:120-128。全owner colonyのECU pin reader。Host未登録。 | PJ:1195-1210; P:27-39 |
| 27 | planetMgr.GetFullNetworkForOwner | 未実装 | (planetID,ownerID)→tuple(list pin KeyVal,list endpoint list) | planetSvc.py:98-108。Host未登録。colony owner-scoped network readerが必要。 | PJ:1160-1175; P:27-39 |
| 28 | planetMgr.GetPlanetInfo | 未実装 | boundまたは(planetID)→KeyVal planet/colony | eve-logic/eve/client/script/environment/planet/clientPlanet.py:82-94。初期PI read。Hostはbound自体未提供。 | PJ:1075-1084; P:27-39 |
| 29 | planetMgr.GetPlanetResourceInfo | 未実装 | planetID/bound→CachedMethodCallResult(resource dict) | clientPlanet.py:644、resource/GM program chooser。Host未登録。SDE resource catalog + planet runtime ownerが必要。 | PJ:1086-1098; P:27-39 |
| 30 | planetMgr.GetProgramResultInfo | 未実装 | bound、(pinID,typeID,heads,headRadius)→tuple(qty,cycleTime,numCycles) | clientPlanet.py:525のprogram estimate。Host未登録。ECU/resource distribution authoritative calculationが必要。 | PJ:1426-1460; P:27-39 |
| 31 | planetMgr.GetResourceData | 未実装 | bound、(KeyVal resourceInfo)→KeyVal(data bytes,numBands等) | clientPlanet.py:734。Host未登録。resource harmonic bytes readerなし。Empty bytesで成功する代替はない。 | PJ:1100-1114; P:27-39 |
| 32 | planetMgr.GMAddCommodity | 未実装 | bound、(pinID,typeID,quantity)→result KeyVal/UserError | clientPlanet.py:668-674のGM callあり。colony mutation→OnRefreshPins([pinID])。Host未登録。GM用と通常UIを区別。 | PJ:1508-1530; P:27-39 |
| 33 | planetMgr.GMCleanupExpiredLaunches | legacyでclient未確認 | (maxAgeDays,ownerID?)→result KeyVal | EveJS cleanup実処理、deleted>0でOnPILaunchesChange。whole client exact-name未検出。Host未登録。既存P launch read/delete ownerを再利用候補。 | PJ:1489-1506; P:41-64 |
| 34 | planetMgr.GMGetCompleteResource | 未実装 | bound、(resourceTypeID,layer)→resource KeyVal(bytes) | eve-logic/eve/client/script/ui/shared/planet/planetUISvc.py:669-675のGMShowResource。Host未登録。 | PJ:1116-1129; P:27-39 |
| 35 | planetMgr.GMGetLocalDistributionReport | 未実装 | (planetID,(theta,phi))→KeyVal(planetID,latitude,longitude,resources dict) | clientPlanet.py:385-386。Host未登録。planet resource samplingを再利用する準備が必要。 | PJ:1131-1139; P:27-39 |
| 36 | planetMgr.GMGetPlanetDiagnostics | legacyでclient未確認 | (planetID?,ownerID?)→diagnostics KeyVal | whole eve-logic exact-name未検出。EveJS diagnostics readerでstate mutationなし。Host未登録。 | PJ:1476-1487; P:27-39 |
| 37 | planetMgr.GMGetSynchedServerState | 未実装 | bound、(ownerID?)→tuple(duration Filetime,colony KeyVal) | clientPlanet.py:676-700のGMVerifySimulation。Host未登録。clockの実数値差は受入の時刻揺れ、colonyが取れないことは別。 | PJ:1141-1158; P:27-39 |
| 38 | planetMgr.MachoBindObject | 未実装 | (planetID,nestedCall?)→tuple(bound descriptor,nested result) | clientPlanet.Init:29-40→eveMoniker.GetPlanet。EveJS tracks per-OID planetID/reuse、async nestedを待つ。Host PはIServiceでbinderなし。 | PJ:1000-1036; P:12-39 |
| 39 | planetMgr.MachoResolveObject | 未実装 | (planetID)→node int | carbon/common/script/net/moniker.py:111-165のresolve。agentのResolveが実装済でもplanetMgr所属は未登録。 | PJ:994-998; P:27-39 |
| 40 | planetMgr.UserAbandonPlanet | 未実装 | bound、()→bool（invalid IDs False、valid True） | clientPlanet.py:174。EveJS colony abandon→OnMajorPlanetStateUpdate(planetID,True)。Host未登録。 | PJ:1415-1424; P:27-39 |
| 41 | planetMgr.UserLaunchCommodities | 未実装 | bound、(commandPinID,commodity dict)→Filetime long/UserError | clientPlanet.py:412。EveJS preview→export wallet tax→launch、失敗refund→physical launch container→OnRefreshPins→OnPILaunchesChange。Host未登録。Wallet/PgItemStore/GameStore launch ownerを結合する必要。 | PJ:1328-1380; P:27-64 |
| 42 | planetMgr.UserTransferCommodities | 未実装 | bound、(pin path,commodity dict)→tuple(simTime,sourceRunTime)/UserError | clientPlanet.py:448。EveJS transfer validation/mutation→OnRefreshPins(source,destination)。Host未登録。 | PJ:1382-1413; P:27-39 |
| 43 | planetMgr.UserUpdateNetwork | 未実装 | bound、(serializedChanges)→serialized colony KeyVal/UserError | clientPlanet.py:180。EveJS request-scope hash/revision/idempotency→preview→construction debit→apply→refund on error→consume new command-center items。Host未登録。既存wallet/itemsを再利用しcolony authoritative transactionが必要。 | PJ:1212-1326; P:27-39 |
| 44 | dungeonInstanceCacheMgr.GetTriglavianSiteInstances | 現行同service実装済 | argsなし→dict(systemID→list KeyVal) | 現行D:157,181はarchetype65で実dispatch、Host dungeonReadModelへ接続。旧未実装記録を撤回。runtime/actual populated instancesは未実測。 | CJ:699-702; D:138,151-183,186-203 |

PIのpayload helpersも確認した。PJ:307-346のpinはutil.KeyVal（type別fields/contents dict/Filetime等）、PJ:368-377のcolonyはownerID/pins list/links list/routes list/level/currentSimTimeを持つKeyVal、PJ:379-400のradiusは明示real、PJ:417-431のresourceはdata bytes/numBands/proximity、PJ:450-493のdistributionとpin summariesはKeyVal。UserAbandonPlanetはevejs-v0.12.9/server/src/services/planet/planetRuntimeStore.js:3977-3988でboolを返す。未登録だから戻り値を調べずに済ませた行はない。

### 具体的な残存破損・未接続と再利用先

ここでの破損はstatic sourceで同じ入力/stateに対してclient contractを満たせない経路が確定したもの。実発生をgraphical clientで観測した、という意味ではない。

1. **SKIN license activation noticeの引数数（破損）**
   入力: active character Cが所有する有効license itemをActivateSkinLicense([itemID])でactivate。S:190-247で保存・consume後、I:55-58は2 IDを含むPyListを1引数でSendNotificationする。../../../dotnet/src/Eve.Services/ClientSession.cs:220-224のparams arrayと同/SessionChanges.cs:53-79のBuildはflattenしないため、argsの長さは1。EveJS SJ:180-188とnetwork/clientSession.js:109-111,829-922は2 IDをflat argsとして送る。CCP cosmeticsSvc.py:213-216のOnSkinLicenseActivated(_skinID,licenseeID)は2引数を要求し、Host payloadでは受信callbackがTypeErrorになりcacheをresetできない。保存済license/item消費を取り消さずUI cacheだけ古く残り得る。既存ClientSession.SendNotificationを2 scalar引数で使う修正候補。ここでは修正していない。

2. **cosmeticsの64-bit ship ID clamp（破損）**
   入力例: PgItemStoreにowner=Cのship ID 2147483648が存在するstateでApplySkinToShip(2147483648,None)、または同IDのlogo stateをGetEnabledCosmetics(2147483648)で読む。S:294のPositiveは2147483647へclampするため、別ID lookup/別pathとなる。EveJS SJ:250-259,308-322はこの値をNumberで保持する。../../../dotnet/src/Eve.Inventory/Items.cs:38-40,519-525はItem.Id/GetAsyncがlongで、この入力はstore contract上有効。IDの揺れではなくIDの損失。既存long parser/InventoryWire.Idを再利用する候補。session activeShipIdにも同じclampがある。型ID/skinIDのInt32とitemIDのInt64を区別する必要がある。

3. **mission Briefing/Objectiveの実state投影不足（部分実装）**
   入力: offered/accepted/completed held missionへbound GetMissionBriefingInfo()/GetMissionObjectiveInfo()。accepted timestamp/expiryがあるstateでもMV:30-40はNone、keywordsはempty。kill missionでもMV:42-60はsystem/location 0、rewards/LP/bonus/locationsはMV:95-117で部分的な固定値。EveJS MR:5780-5838,5884-6169はheld record/rewards/locationを読む。単なるFiletime揺れではない。client mission text、destination、reward表示が欠ける経路。既存HeldMissionView、AgentMissionRecord、MC:413-447のFetch/Courier/static content/mission messagesを使って投影する候補。client/APIが要求しないmetadata field差は同列に破損扱いしない。

4. **R&D Journalのownerがwriterと異なる（未接続）**
   入力: ResearchProjectStoreにprojectをStartAsyncしたcharacter CでGetMyJournalDetails()。現行writerは../../../dotnet/src/Eve.Services.Core/AgentDialogue.cs:377-378でresearch.Projects.StartAsync、cancelも同:417でPg storeへ書く。MC:203はそのResearchProjectStoreをPersistenceAgentResearchへ渡す。一方journal research bucketはMC:235-239のGameStore researchRuntimeStateのみを読み、Pg projectsをoverlayしない。fresh legacy tableが空ならstart後もjournal research rowsは空。EveJS MR:7558-7562はresearchRuntimeから現在rowsを作る。再利用先は../../../dotnet/src/Eve.Agents/ResearchProjectStore.cs:53-54のAccruedPoints、同:70-83のReadAllAsyncとSDE agent station/field metadata。新しいparallel research storeは不要。

5. **新規Pg epic arc progressがGetMyEpicArcStatusへ投影されない（未接続）**
   入力: legacy missionRuntimeStateが空のcharacterでPg AgentMissionStoreにepic arc stepがあるstate。MC:215-221はAO:17-39のOverlayを使うが、AO:50-67のRecordはmissionID/state/timestamps等だけでclientMissionRecord/isEpicArc/epicArcIDを作らない。A:513-593のEpicArcStatusPayloadはlegacy progressかそのmissing metadataに依存するため、Pg stepをarc mapへ追加できない。MR:7565-7569はruntime epic progressを返す。再利用先はMC:273-279で既に使うReadCompletedAsync/ReadFailedAsync、agentMissionStatic.EpicArcs/mission membership。completed career mapもA:379のlegacy readしか更新接続を検出できず、進行後bool反映を一致とは数えていない。

6. **fighter初期ability stateの欠落（破損）**
   入力: launched fighterにactive ability、remaining chargeまたはcooldownがあるstateでGetFightersForShip()。F:85-103は第三dictを常にemptyにするが、FR:2149-2160は各entityのslot payloadを返す。CCP shipFighterState.py:201-230はcacheをclear後そのdictでslot statesを復元する。UI再生成/reconnectのsnapshotでactive/charge/cooldown表示が欠ける。backendに_slots、AbilitySlot、StateOf/Resumeが既存（F:406-500,793-839）。それらからsnapshotを投影する候補。activation/deactivation notificationsが後で来ることは、初期snapshotが正しいことの代用にならない。

7. **Dungeon resource/wormholeのHost projection未配線（未接続）**
   入力: authority-backed ice siteがあるstateでGetIceBeltInstances()/GetIceBeltsCount()、未materialized ore planned siteをGetOreAnomaliesCount()/InRange、visible wormholeのみあるsystemをGetSignatureInstances()で読む。DR:70-75はOreAnomalySpawnCounts/IceSystemAuthority/VisibleWormholeSignatureCountsをすべてemptyで渡す。D:186-203,212-266はその入力を本当に使用するので、ice entries/count・未materialized ore・wormhole追加countが欠落する。CJ:228-321,389-402は実authority/runtimeを使う。handler shape一致では解決しない。既存ContentRuntime/dungeonRuntimeState/SDE catalogを保ち、補助authorityを接続する候補。wormholeは../../../dotnet/src/Eve.Host/Composition/SpaceComposition.cs:195-206のspaceSessions.Wormholes、../../../dotnet/src/Eve.Wormholes/WormholeWorld.cs:80-92のEndsInSystemAsyncを再利用しvisibilityを適用する。ore/ice計画authorityのHost projection自体は未実装で、EveJSのcaptured countを固定値でコピーする案は採らない。

8. **共有folder/Calendar/Facility/Community/RW/FWの先行差分を現行sourceでも再確認**
   AccessGroup Add/UpdateはArgs0-2だけ（AccessGroupBookmarkMgrService.cs:30-44）、admin searchはPersonalFoldersだけ（同:59-63）。Calendar event通知送信はdotnet/src exact-name検索で未検出。FacilityManagerService.cs:21-45とIndustryComposition.cs:172-190は5 fallbackとempty activities/serviceAccess/rigModifiersを維持。CorpFittingMgrService.GetCommunityFittingsはFittingMgrServices.cs:351-360のcache wrapper追加を確認したがFittingRpc.cs:23-27にproviderがなく内容はempty dict（CCP fittingSvc.py:400-413が読む）。CoreRpc.cs:85,102のRW/Victory callbackなしも現行で再確認。これらは先行節のprovider未接続記録として残す。既存BookmarkStore/CalendarStore/Industry facility store/Pg fitting store/FW state ownerを接続する候補で、別ownerを作らない。先行行番号は適宜このcurrent証拠を優先する。

9. **SKIN activationの並行失敗補償が別callの成功licenseを消す可能性（source上のrace）**
   具体的なinterleaving: 所有者Cのquantity=1の同じ有効license item Xへ、ActivateSkinLicense([X])を2 call同時に送る。両callがS:190-215のplan段階で「有効licenseなし/Previous=null」を読む。Aがoverrideを書きitemをconsumeしてcommitする。Bはその後item lock/getで既に消費されたitemを見て失敗するが、S:216-251のcatch/RestoreOverridesがPrevious=nullを根拠に同じlicense pathをRemoveする。結果はA成功/B拒否なのに、Aが保存したlicenseが消える。GameStore overrideとPg item transactionが一つのcommit ownerでなく、補償が自callのwriteかを確認しないことが原因。EveJSのSJ:391-468は同期のgrant/consume sequenceで、同じasync interleavingではない。これは実際に並行実行して観測した結果ではなく、取得済bodyに存在する成立可能な実行順である。既存PgItemStore/GameStore ownerを保ち、char/skinごとの直列化またはversion/ownershipを確認する補償へ寄せる修正候補。別call成功のoverrideを無条件に戻さないことを検証する必要がある。

親への具体的な検証handoff: OnSkinLicenseActivatedのdecoded argsを2 scalarにしてCCP receiverへ渡すこと、Int32境界を越すowned shipのskin/logo read-write、同item並行activationで一方のcommit licenseが残ること、Pg research開始/取消とjournalのreadback、Pg epic step/historyとarc status、fighter active/charge/cooldownの再取得、ice/ore/wormhole authorityがあるsystemのcache readを確認する。本担当でtestsの追加・実行はしていない。

### 差分だが破損と断定しないもの

- Node ID/CallId/採番/updatedAt/現在Filetime、過去1secか1tickかというexpiry時刻差、ID順の並びとfixture/SDE corpus差は型・本質state差と区別した。
- fighter load/unload/abandon/scoopのFalseとUserError、simulation不在を拒否すること、unsupported effectsの拒否はpolicy/validation差。正規client操作での実影響はruntime未確認。Unload success True→Noneもclient wrapperが返す以外のbool依存を確認できておらず、単独で破損と断定しない。
- fighter notificationの50ms delayはsource上のqueue順であり、network reply後を保証するruntime barrierとみなさない。recall中のability停止、失われたfighter、他pilotの放棄fighter recovery、restartの完全互換は未実測。
- GiveSkinのSDE duration fallbackとbad skinの拒否差はGM/default policy差。ApplySkinToShip後の空logo noticeとgateway/slim observer通知の違いは静的差分、実描画/observer反映は未確認。
- in-space agent locationのsourceBuild field欠落、EpicArcMessagesのcache envelope、Blueprint invalid IDをnamed errorにする差、Kiring/FastCheckoutの記録済方針差を、全件破損とは扱わない。
- editor Rooms/Paletteはprovider未供給で未実装。EveJS fixtureを製品authorityにそのままコピーすべきとの判断はしていない。

### 作業保全・最終境界

- 実root repository root、branch main、取得基点HEAD 52b8421f34e635900a18459da4403f26f2dfaec0、最終確認HEAD dda03ca0464043822e68c8348604ff34eadedcac。保存/検査中の親commit dda03ca0はCrate retry修正で、変更pathはCrateService.cs、CrateServiceTests.cs、親のcrate-skill-retry-fix.mdのみ。今回49 RPC＋新規2 RPCのソースにはこのcommitの差分なし。Crate ClaimFittingは最新sourceで読み戻し、予約/rollbackを保持することを確認した。ambient worktree1e47はsource/保存に使わなかった。全exec_commandのworkdirをC:\Users\yutai\eve.rsに指定。
- 途中に親/他workerのsource/test差分14 files（保存前read-only statで661 insertions/46 deletions）と複数の未追跡audit docsを観測した。これらをrestore/stage/delete/commitしていない。自担当writeはこのcore-a-f.mdのみ。開始時source cleanと読み直し時の親Crate dirtyを混同しない。
- 既読materialを再利用。canonical Serena manual/activateとCBM三rootを確認済み。CBM source exact-path metadata_changedをSerena current bodyで補った。Transport closedはなかった。追加text searchのrequest_timeoutはnarrow query/graph/snippetで回避し、ARCHETYPE_SETS追加text検索timeoutもsymbol snippetで回収し、CJ:33-51の定数を照合した。未確認を一致件数へ加えていない。
- 本担当ではsource/test変更、build/test、Git mutation、子agent、別modelへの切替を実行していない。親のCore698/Rust835等のPASSはユーザー提示の既存検証対象であり、本監査の全差分/runtimesをPASSとする証拠にはしていない。
- **source coverage: 34 services / Host186 RPC / EveJS228 RPC / 累計Host詳細比較186 / 旧残49完了 / 旧EveJS-only44分類完了 / body未比較0**。runtime/scenario受入は未確認。正常/失敗/並行/再試行/再接続/再起動の実測は親の修正・直列検証へhandoffする。
