# Host coverage handoff: RPC-01379–01430

## Resolution

The response RPCs use active Host routes. The regression crosses HostServer, PacketDispatcher, ServiceRegistry, and actual service implementations with test-owned authoritative read data; it does not use PostgreSQL.

GetDBSMapData previously returned an empty dictionary despite an available ESS system reader. It now returns solar-system IDs mapped to positive CurrentOutput values, matching EveJS v0.12.9 dynamicResourceState.buildDbsMapDataPayload (server/src/services/dynamic/dynamicResourceState.js:343-349).

## Whole-client contract evidence

- RPC-01379/01380: eve/client/script/ui/inflight/dungeoneditor.py:198-199 calls local Python list sort/insert on the PyList returned by DEGetArchetypes. They are not service methods.
- RPC-01381/01382: journal.py:249-252 caches the returned rowset and scatters OnEscalationsDataUpdated; journal.py:186-200 indexes header/lines by instanceID; contentProviderEscalations.py:53-58 filters destDungeon and expiry. EveJS returns the six-column empty Rowset (dungeonExplorationMgrService.js:86-105).
- RPC-01389/01390: fwWarzoneSvc.py:161-163 returns the battlefield-instance map. EveJS groups battlefield entries by system (dungeonInstanceCacheMgrService.js:679-682).
- RPC-01405/01406: insurgenceDashboardSvc.py:292-294 memoizes and returns the ice-heist map. EveJS groups entries by system (dungeonInstanceCacheMgrService.js:694-697).
- RPC-01409/01410: contentProviderSignatures.py:19-30 iterates the result with iteritems, accepts counts or site lists, and replaces the current-system row with scanner signatures. RPC-01411/01412 return the same map (mapFilterSignatures.py:10-12); baseMapFilter.py:134,137-142,158-159 consumes local keys/get.
- RPC-01415/01416/01417/01418: get, iteritems, and keys are Python mapping operations on the returned PyDict (baseMapFilter.py:134,137,158; contentProviderSignatures.py:22). EveJS buildSignatureCountDict returns a dictionary (dungeonInstanceCacheMgrService.js:313-321).
- RPC-01419: dynamicResourceSvc.py:58-64 unpacks (output, dbsIsActive); infoPanelLocationInfo.py:319-326 shows output * 100 only when active. EveJS returns disabled [0, 0] (dynamicBountyMgrService.js:11-20).
- RPC-01423/01424 are consumed by mapFilterDynamicBounty.py:18-35; RPC-01425/01426 by mapViewColorHandler.py:210-224. They need a system-ID-to-output map and minOutput/maxOutput/equilibriumValue.
- RPC-01427/01428/01429/01430: essSystemContentPiece.py:22-24 and essSystemInfoContainer.py:51,103,111-113 request settings. Other whole-tree callers: essFiltersCont.py:22-25; essBountiesOutputTooltip.py:18-19; essGameplayTooltip.py:18; essMainBankTooltip.py:18-20; essReserveBankTooltip.py:18-19; infoPanels/essUI/controller.py:25; dynamicresources/client/ess/settings.py:24,38-39 reads reservePulseIntervalSeconds. EveJS settings are in dynamicResourceState.js:285-287.

These caller and consumer locations were searched across /home/user/eve-logic with Serena and cross-checked with CBM graph searches.

## Host and test

DungeonRpc.cs registers DungeonService and DungeonInstanceCacheMgrService against PersistenceDungeonReadModel, which reads SDE, active content, and persisted dungeon state. CoreRpc.cs wires DynamicResourceCacheMgrService to ESS readers. CoreServices.Create registers DungeonExplorationMgrService and DynamicBountyMgrService.

DungeonDynamicResourceHostWireTests.cs covers the PyList, escalating-path rowset, grouped battlefield/ice-heist maps, signature count map, settings, DBS output map and zero-output filtering, and disabled bounty tuple through the real Host socket. Its EntryValue(PyDict, string) helper is shared by DynamicResourceCacheMgrHostTests.cs.

## Checks and limits

PASS: Dungeon_and_dynamic_resource_cache_calls_round_trip_through_host_registration (1 passed, 0 failed, 0 skipped), including the latest merged Host test source.
PASS: dotnet build dotnet/src/Eve.Services.Space/Eve.Services.Space.csproj -p:UseSharedCompilation=false (0 warnings, 0 errors). Focused test invocations compiled Eve.Host.Tests successfully.
Actions 40196410619 on SHA 21f001c failed because both Host test files defined Value(PyDict, string). The new test's helper was renamed to EntryValue. On SHA 75f32014, Actions 40198363063 and CI run 40198356757 then exposed nine unresolved Value references in the neighboring test. The latest main merge changed those callers to the shared EntryValue helper; the focused test passes on the merged tree. Actions 40200111554 and CI run 40200078948 for SHA 8c9911df are checking that tree.
Not exercised: graphical client, PostgreSQL-backed Host composition, or nonzero dynamic-bounty activation. The disabled pair matches EveJS v0.12.9.
