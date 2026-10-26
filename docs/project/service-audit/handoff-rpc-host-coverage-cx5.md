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

Focused Agent test 40369367192 passed (1 passed, 0 failed, 0 skipped). Push CI 40369355715 failed only `Eve.Services.Tests.DispatchGoldenTests` for the `packets:arguments` list sequence. After merge, `b213ee17` aligned that fixture with the V24.01 service positional sequence; the full `Eve.Services.Tests` project then passed (261 passed, 0 failed, 0 skipped).

## P2 client verification batch 31 — RPC-02792, RPC-02794–02795

- Whole V24.01 client tree: only `voidspace/client/void_space_service.py:132` invokes `ClientIsReady` on entry to Void Space and ignores its return. The only `VoidSpacePlayerJump` caller is `voidspace/client/void_space_service.py:45-47`; it forwards `itemID,typeID` and ignores normal return. `void_space_activation_controller.py:145-148,158-164` makes the session-change call and consumes the error object's `errors` field. Its `:127-143` stores those code/argument pairs for the activation UI. `voidspace/common/error.py:8-43,46-110` defines code 22 as UNKNOWN_ERROR and maps it to the unknown-jump label.
- EveJS 261018.1: `server/src/services/activity/voidSpaceMgrService.js:90-93` returns null from ClientIsReady; `:74-80` rejects VoidSpacePlayerJump while the encounter runtime is unavailable; `:49-63` constructs the wrapped error list.
- eve.rs `dotnet/src/Eve.Services.Core/LoginInitializationRpcServices.cs:704-760` matches that behavior. `CoreServices.Create` registers the service and `CoreServices.Names` exposes its name; production Host registers the CoreServices list at `dotnet/src/Eve.Host/Hosting/HostComposition.cs:235-243`.
- Added `dotnet/tests/Eve.Host.Tests/VoidSpaceRpcHostWireTests.cs`, covering Host CallRsp/None and ErrorResponse with exact `VoidSpaceJumpError.errors=[(22, [])]`. The existing movement Host test now uses the CoreServices registration rather than adding a test-only service.
- The duplicate rows RPC-02794/02795 describe the same single client method, covered once by the same wire call.
- After the final `git fetch origin main && git merge origin/main` (already up to date), the two focused Host wire tests passed (2 passed, 0 failed, 0 skipped), and the Core registration test passed (1 passed, 0 failed, 0 skipped). The required full `Eve.Host.Tests` shard completed with 479 passed, 101 failed and 227 skipped. Failures include database configuration errors; no database was used, and those results do not count as passes. Full `Eve.Services.Tests` had passed after the preceding merge (261 passed, 0 failed, 0 skipped). No graphical client run was performed.
- Pushed as `9c517b76` (`Register and verify Void Space RPCs`). Focused Agent Actions run `40375269871` passed (994 passed; no failures or skips). Push-triggered CI run `40375243337` failed `CoreCatalogTests.The_catalog_names_the_core_services`: the expected catalog omitted the newly registered `voidSpaceMgr`. The next batch fixes the expected list in `dotnet/tests/Eve.Services.Core.Tests/CoreCatalogTests.cs`; the focused catalog test passes locally.

## Whole-client RPC argument check following batch 30 CI

- `carbon/common/script/net/ServiceCallGPCS.py:66-139` passes the CALL_REQ payload's args slot as the service positional sequence. The client builder at `:172-177,234` constructs that slot from a Python `*args` tuple; `carbon/common/script/sys/basesession.py:1491-1497` likewise forwards `*args`. A list-valued RPC parameter is nested inside that tuple; an args slot that is itself a list is the sequence-compatibility form accepted by eve.rs and EveJS 261018.1 `server/src/common/pyPacket.js:115-239`. The merged fixture correction b213ee17 preserves a strict flattened response assertion for that form.


## P2 client verification batch 32 — RPC-02790–02791

- Whole V24.01 tree search found one direct RPC call at `vgs/client/vgsClient.py:28-30`: it passes `PLX_CURRENCY`, receives a numeric balance, and returns the balance or zero. The helper itself has no other references in the client tree; `eve/client/script/environment/vgsService.py:79-80` constructs its VgsClient and Store. The duplicate CSV rows refer to this one callsite.
- EveJS 261018.1 `server/src/services/account/vaultManagerService.js:20-31` reads the current character's wallet projection (`walletState.js:236-265`) and returns a number. eve.rs `StoreOffersService.cs:369-388` accepts PLX and uses the authenticated user ID; `RewardRpc.cs:94` registers the production route; `PersistenceNewEdenStoreRuntime.GetPlexBalanceAsync` aggregates that account's character balances. CCP documents the PLEX Vault as shared account state, recorded in `docs/reference/compat/DEVIATIONS.md`.
- Added DB-free socket regression `VaultManagerRpcHostWireTests.Vault_manager_balance_uses_the_client_wire_shape_and_authenticated_account`: byte-string PLX request, numeric positive/zero CallRsp, authenticated account input, and unsupported currency ErrorResponse without calling the provider. No notification is sent by this read RPC.
- After fetch/merge (already up to date), the focused Host wire test passed (1 passed, 0 failed, 0 skipped). The full Host shard completed with 480 passed, 101 failed and 227 skipped; database-backed failures could not run without EVE_PG_URL. The full Core service project completed with 840 passed, 8 failed and 57 skipped; all 8 failures are PostgreSQL-backed fixture setup without EVE_PG_URL. The focused CoreCatalog test passed (1 passed, 0 failed, 0 skipped). No database was used and no graphical-client run was performed.
- Batch 32 also fixes the catalog expectation from CI run `40375243337`. Pushed as `8438195d` (`Verify PLEX balance RPC over Host wire`). Focused Agent Actions run `40377994541` passed (995 passed; no failures or skips). Push-triggered CI run `40377977520` completed successfully with no failed tests or jobs: https://github.com/Fokiiiiiii/eve.rs/actions/runs/40377977520.
