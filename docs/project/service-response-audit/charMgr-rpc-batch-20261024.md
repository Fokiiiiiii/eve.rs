# charMgr and global inventory RPC follow-up (2026-10-24)

## Scope

Rows RPC-01074–RPC-01091 cover charMgr.GetPublicInfo, GetPublicInfo3, GetRecentShipKillsAndLosses, and GetSettingsInfo. Rows RPC-01093–RPC-01099 cover the global-asset inventory methods List, ListIncludingContainers, and ListStationItems. RPC-01092 is classified separately because the V24.01 source shows it is a client cache method, not a server RPC.

This batch is in progress. The new regression in this checkpoint dispatches GetSettingsInfo through the registered charMgr service. Existing PublicInfo tests check persisted projections directly, but do not cover the Host wire path; the remaining row groups still need Host wire coverage.

## V24.01 client callers and consumers

The whole /home/user/eve-logic tree was searched for each RPC and cache method.

| Rows / method | Client callsites and consumed fields |
|---|---|
| 01074–01078 charMgr.GetPublicInfo | nodegraph/client/conditions/safe_schools_client.py:12-19 and safespace/client/util.py:8-17 reject a missing object and require/read schoolID; nodegraph/client/getters/location.py:31-34 reads schoolID to select the starting station. Other charMgr callers found: homestation/client/service.py:116; eve/client/script/ui/shared/preview.py:851; eve/client/script/ui/shared/neocom/charsheet/characterOverviewPanel/characterOverviewPanel.py:46; eve/client/script/ui/shared/info/panels/panelStandings.py:120; eve/client/script/ui/services/ccSvc.py:179; eve/client/script/ui/services/crimewatchSvc.py:179 (reads warFactionID, which neither the EveJS nor eve.rs PublicInfo projection currently returns); eve/client/script/ui/station/agents/agents.py:281,519; avatardisplay/avatardisplay.py:1259,1359; evephotosvc/scenePortraitQueue.py:125; evephotosvc/scenePortraitRenderer.py:188; operations/client/UI/operationDestinationController.py:12. ccSvc.py:120 also invalidates this cached method. Corporation service calls with the same method name are excluded from the charMgr caller count.
| 01079–01085 charMgr.GetPublicInfo3 | eve/client/script/ui/shared/dockedUI/inControlCont.py:40-42 receives this result alongside corporation info; characterInfoWindow.py:63-70,193-199 and infoContentBase.py:409-423 treat the result as a list and read description, startDateTime, optional medal1GraphicID, and bloodlineID; nodegraph/client/getters/player.py:23-27 reads info[0].corporationID. bountyWindow.py:451,1011 invalidates the cached call after bounty placement.
| 01086–01089 charMgr.GetRecentShipKillsAndLosses | eve/client/script/ui/shared/info/infosvc.py:2647-2652 passes num,startIndex, then filters each row by finalCharacterID for kills and victimCharacterID for losses. eve/client/script/ui/shared/neocom/charactersheet.py:90 invalidates cache key (25,None) after a kill notification.
| 01090–01091 charMgr.GetSettingsInfo | carbonui/services/settingsSvc.py:147-153 unpacks code, verified; raises if verification fails; loads the returned bytecode; executes it; and may send collected settings to LogSettings. A separate orbital moniker GetSettingsInfo is not this service.
| 01092 InvalidateStationItemsCache | eve/client/script/ui/services/menuSvcExtras/invItemFunctions.py:296 calls invCacheSvc.GetInventory(containerGlobal).InvalidateStationItemsCache(stationID); eve/client/script/environment/invCache.py:839-857 owns the five-minute station-item cache and deletes its entry locally. It does not invoke RemoteSvc('charMgr') or the moniker, so no server registration is appropriate.
| 01093–01095 charMgr.List | lpStoreSvc.py:312, fittingSvc.py:583, and assembleModularShip.py:234 call invCache.GetInventory(containerHangar).List(flagHangar). invCache.py:877-883 forwards the first uncached request to self.moniker.List() and caches returned item records. Consumers read itemID, typeID, stacksize, and singleton in the LP store, fitting selection, and subsystem validation flows.
| 01097 charMgr.ListIncludingContainers | nodegraph/client/getters/inventory.py:216-219 filters returned items; assetsWindow.py:413-416 counts and turns the rows into an item rowset. invCache.py:877-882 forwards and caches rows by itemID.
| 01098–01099 charMgr.ListStationItems | assetsWindow.py:744-745 builds asset rows after filtering by locationID; market/quote.py:376-388 filters by typeID, locationID, and stacksize before selling. Another caller is eve/client/script/ui/shared/assets/assetSafetySvc.py:35-37, which checks for items at a structure. invCache.py:839-851 caches by station ID and returns moniker rows.

## EveJS comparison and eve.rs path

- EveJS v0.12.9 server/src/services/character/charMgrService.js:510-520 forwards ListStationItems, List, and ListIncludingContainers to _globalAssets; :526-539 returns a public-info KeyVal or a one-item list; :575-589 returns a list page for the bound/current character; :739-744 returns the settings bytecode and zero signature.
- The corresponding eve.rs handlers are in dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs: GetPublicInfo projects the character plus description, security status, and medal provider data; the C# projection now uses authoritative security status for both securityRating and securityStatus, matching EveJS :393-398 (previously securityRating was always zero); GetPublicInfo3 wraps that same record in a list; global asset methods delegate through globalAssetsInventoryCall; GetSettingsInfo returns the verifier pair.
- dotnet/src/Eve.Services.CharacterInfo/CharMgrService.cs receives the Host global-assets provider through its injected callback. dotnet/src/Eve.Services.Inventory/InvBrokerService.cs:402-435 implements station-item and container listing from item/character stores and applies ownership/access checks. Production Host callback wiring and same-scenario List wire results have not yet been checked in this checkpoint.
- Existing CharacterInfo tests include direct service scenarios for PublicInfo and killmail paging. The public-info projection test now checks securityRating and securityStatus both equal the stored -2.5 value. These direct service tests do not replace the requested Host wire coverage. The whole-client warFactionID read at eve/client/script/ui/services/crimewatchSvc.py:179 remains without a matching field in either reference response; do not synthesize it without an authoritative organization provider.

## Change and verification checkpoint

- Added dotnet/tests/Eve.Services.CharacterInfo.Tests/CharMgrSettingsWireTests.cs. It registers the concrete CharMgrService with ServiceRegistry, sends a marshalled CallReq through PacketDispatcher, and checks the marshalled CallRsp payload is a two-item tuple containing bytes and signature zero. No database is opened by this test.
- First build attempt used --no-restore and stopped because this worktree had no NuGet assets file.
- Restore-enabled dotnet build spent over nine minutes at Determining projects to restore and exited 1 without restore diagnostics. The focused test has not run. The restore-enabled build remained at package restore and exited 1 without diagnostic output, so compilation is not established.
- The git checkout started on branch cx21 with unrelated pre-existing worktree changes. Those changes are not part of this RPC batch and must remain untouched.

## Later verification and remaining work

- Host tests now send the real Macho CallReq through HostServer and the registered CharMgrService. The GetPublicInfo case sends transport keyword machoVersion, checks the KeyVal fields for two character fixtures, and covers a missing character; GetPublicInfo3 is checked as a one-row list. This ran successfully on commit 5ebf140b in Actions run 40086215702; the selected job reported no failed tests. The follow-up run 40086756910 failed during compilation because the Host test did not import Eve.Services for Py.Number; the import is now added and the same regression is queued again.
- A previous run exposed a compile error from two identical CallWithKwargs helpers introduced as main advanced during the merge. The duplicate was removed. The next selected Host test passed.
- The push-triggered ci.yml run for c84fe135 reported the same compile error and a separate live structure anchoring failure at tests/test_structures.py:486 (CustomNotify: You must be in space to deploy a structure). The failure is outside this charMgr change; the latest pushed ci.yml run is still pending at this checkpoint.
- The first local restore-enabled .NET build did not finish package restore, so no local build or test result is established. The agent-test workflow skipped its headless-client installation because live tests were empty; that skip is separate from the passing selected .NET test.
- Remaining rows still lack this batch's focused wire coverage: recent-kill paging, settings, and the global-assets inventory methods. The Host callback's production wiring and database-backed inventory behavior still need a DB-lane scenario; this task has not used PostgreSQL.
- PublicInfo client warFactionID read remains a shape gap: crimewatchSvc.py:179 reads it, but EveJS charMgrService.js:372-421 and eve.rs PublicInfo do not provide it. Do not add a fabricated field.

## Client evidence required for continuation

Before closing remaining rows, add Host-dispatched wire tests for the persisted PublicInfo shapes, the recent-kill row pages, and the global-assets methods using the real provider wiring. Include different valid page sizes, missing/unauthorized inventory state, and client-added transport arguments where the packet path adds them. Record Action results and separately identify skipped tests.
