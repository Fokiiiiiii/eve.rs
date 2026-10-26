# RPC Host coverage handoff — CX5

## Scope and decision

This batch adds one real-socket Host regression covering the listed lookup, machoNet, and moonExtractions reads. No production endpoint was added: the 15 rows named `machoNet.get` are Python dictionary lookups on the result of the existing RPC `machoNet.GetGlobalConfig()`, not remote method calls. Every listed callsite performs its `.get(key, fallback)` locally after the remote call.

## Client evidence and consumers

- RPC-01790/01791: `eve/client/script/ui/shared/neocom/corporation/corp_ui_applications.py:890-901` calls `LookupEvePlayerCharacters(searchStr, 0)`; handles None/empty, primes `each.characterID`, and builds invitation rows from that ID and owner cache.
- RPC-01794/01795: `eve/client/script/ui/util/searchOld.py:53-56` calls `LookupKnownLocationsByGroup(groupID, searchStr)`, extracts each `itemID`, and primes `cfg.evelocations`.
- RPC-01798/01799: `eve/client/script/ui/shared/neocom/wallet/noneNPCAccountOwnerDialog.py:64-78` calls `LookupNoneNPCAccountOwners(searchStr, 0)`; handles None/empty, primes `each.ownerID`, and builds selectable owner rows.
- RPC-01811: `eve/client/script/ui/shared/maps/mapSvc.py:428-440` unpacks `(sol, sta, statDivisor)`, reads per-system counts with dictionary `.get(..., 0)`, subtracts docked pilots, divides by the divisor, and builds map values.
- RPC-01830, 01831, 01832, 01833, 01835: local config dictionary consumers at `eve/client/script/ui/eveCommands.py:3570,3583,3596,3609,3622`. They read CSMVotingURL, CSMCandidateInfoURL, AcademyLandingPageURL, and UprisingPatchNotesURL; client fallback URLs are supplied at the callsites.
- RPC-01836 and 01852: `eve/client/script/ui/services/evePhotosvc.py:430-433,714-718` reads default portrait size (with a client constant fallback) and a caller-selected image-server key (with caller fallback).
- RPC-01837: `eve/client/script/parklife/autopilot.py:74-75` reads disableAutopilotLogging to decide whether to log.
- RPC-01838: `eve/client/script/ui/services/corporation/base_corporation.py:1048-1049` reads disableCorpRecruitmentLogging.
- RPC-01841: `carbon/client/script/remote/connectionService.py:296-297` reads disableLongCallWarning.
- RPC-01846: `eve/client/script/ui/shared/info/infosvc.py:2225-2227` reads enableDustLink before adding planet-control information.
- RPC-01847/01848: `eve/client/script/ui/services/flightControls.py:47-48,376-377` read flightControlsThrottle and flightControlStatisticsInterval, with client defaults.
- RPC-01849: `eve/client/script/ui/shared/info/characterInfoWindow.py:205-206` reads hideReportBot before adding the report action.
- RPC-01855: `eve/client/script/ui/shared/market/buySellMultiBase.py:251-253` reads the configured order cap, defaulting to 100, and limits selected items.
- RPC-02103/02104: `eve/client/script/ui/moonmining/schedulingSvc.py:36-40` returns an empty list without the station-manager role and otherwise returns the RPC list; `eve/client/script/ui/structure/structureBrowser/moonminingEventsCont.py:96-113` reads each extraction's structureID, solarSystemID, chunkAvailableTime, and moonID to build browser rows.
- RPC-02105: `eve/client/script/ui/moonmining/schedulingSvc.py:76-78` returns the resource-composition mapping; `eve/client/script/ui/moonmining/schedulingSvc.py:24-25` and `eve/client/script/ui/moonmining/scheduling.py:80-83` consume it for the moon-mining view.

Whole-tree Serena search for `GetGlobalConfig().get(` found these rows plus consumers in character creation, server info, globalConfig helpers, raffles, and monolithconfig. They also use the returned dictionary locally.

## EveJS and eve.rs behavior

EveJS v0.12.9:
- `server/src/services/corporation/lookupSvcService.js:447-458` filters character rows by search string and exact mode; lines 533-557 similarly build owner rows and delegate location-group lookups.
- `server/src/services/machoNet/machoNetService.js:340-343` returns the global config dictionary. Lines 500-509 return the client-consumed three-part map-stat shape with divisor 1.
- `server/src/services/structure/moonExtractionsService.js:1128-1165` filters active/fracturing extractions by corporation, orders by chunk-available time, and returns per-structure resource composition.

eve.rs already registers `lookupSvc` through `CoreServices.CreateLookupService`, `machoNet` through `MachoNetService`, and `moonExtractions` through `MoonExtractionsRpcService`. The regression uses those real services over the Host socket dispatcher. Lookup and moon reads use injected directory/state sources; machoNet uses its cluster config and live statistics provider. Reads make no mutations or notifications. The Host map-stat provider can return live values while EveJS returns safe empty maps; both meet the observed client shape and divisor use.

## Verification

- Focused real-Host wire test `Rpc01790HostCoverageTests.Lookup_machoNet_and_moon_read_calls_use_registered_host_routes_with_macho_version_kwargs` passed in Actions run 40278146386 (1 passed, 0 failed, 0 skipped).
- Focused Host decoder regressions for dungeon resource and support/insurgency RPCs passed in Actions run 40279953894.
- Focused RPC inventory test passed in Actions run 40280610994 with the updated count of 1,424.
- Focused tournament fallback test passed in Actions run 40282048198; `QueryShipList` returns a dict with an empty `allowedShipList`.
- Push CI 40278138665 exposed the decoded unicode assertion failures and the stale count after `DevIndexManagerRpcService.GetAllDevelopmentIndices` was registered (`OperationalRpcServices.cs:27-41`). Those test issues were corrected. Push CI 40280164556 exposed the tournament test expecting a bare list; its expected shape was corrected.
- Latest push-triggered `ci.yml` run 40282042017 for `0a430473` completed successfully: all jobs passed with no skips or failures.
- The local focused `dotnet test` process was stopped after 47 minutes waiting through compilation under shared-machine load; it produced no test result. The same focused Host wire test passed in Actions.
- No graphical-client run was performed.


## P2 client verification batch 29 — RPC-02842–02843

- Verified the whole V24.01 client tree: only spacecomponents/client/components/jumpPolarization.py:23 calls GetWormholePolarization. OnAddedToSpace starts it at :18-19; None clears the component timer fields at :24-25,38-41; populated replies supply end FILETIME and duration used by jumppolarizationcontroller.py:21-30.
- EveJS 261018.1 server/src/services/exploration/wormholes/wormholeMgrService.js:185-205 returns null or [FILETIME, duration seconds]. eve.rs ActivityTravelRpcServices.cs:183-196 uses the endpoint and session character, returns None or [PyLong(FILETIME), PyInt(seconds)]; SpaceRpc.cs:71-75 is the production registration.
- Added a DB-free registered-Host socket test in dotnet/tests/Eve.Host.Tests/WormholePolarizationRpcHostWireTests.cs. It verifies endpoint/character inputs, populated fields and absent state with machoVersion=1.
- Local Host project build passed with zero warnings/errors. Focused Host test passed (1/1, no skips). Required full Eve.Host.Tests run had 471 passed, 327 failed, 2 skipped; failures were PostgreSQL-backed tests lacking EVE_PG_URL. No database was used. Graphical client was not used.
- Updated EVE_RS_COVERAGE.csv and CLIENT_VERIFIED.md for both duplicate call rows.

Focused agent-test run 40366101679 and push CI run 40366086777 passed for e4dd35c0c3b5, with no failed tests or jobs.


## P2 client verification batch 30 — RPC-02814–02817

- Whole-client search found five GetKillMail callers: notificationEntry.py:218; notificationCenter.py:300; warKillEntry.py:133; killReportUtil.py:41; ghostControllerMixin.py:213. The selected P2 notification paths pass the returned KeyVal into the report window; killReport.py:149-184 consumes the mail, and eveCommonUtils.py:140-286 parses killBlob into attackers and items. ghostControllerMixin uses victimShipTypeID and parsed items to simulate a fitting.
- Production route: CoreServices.cs:84 registers WarStatisticMgrService; CharacterComposition.cs:225-240 passes its reader; MissionComposition.cs:160-165 reads killmails records from GameStore. The handler GameRpcServices.cs:1518-1564 returns None for missing records or hash mismatch, otherwise the client KeyVal.
- EveJS 261018.1 warStatisticMgrService.js:183-196 performs the same lookup and hash rejection; killmailState.js:684-716 builds the KeyVal reply.
- Added DB-free Host socket test WarStatisticKillmailRpcHostWireTests.cs:13-70 for the registered service. It checks killID, killTime, victimShipTypeID and UTF-8 killBlob bytes, plus wrong-hash and missing-record None replies. The first assertion showed that serialized PyStr is decoded as PyBytes; the regression asserts the actual decoded wire bytes and text.
- After origin/main merge: Host test project build passed; focused Host test passed 1/1, no skips. Full Eve.Host.Tests had 477 passed, 327 failed, 2 skipped. PostgreSQL-backed failures cannot connect without EVE_PG_URL; no database was used. Graphical client not used.

The focused Actions and push-CI checks for this batch are pending.
