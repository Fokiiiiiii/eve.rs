# Host coverage handoff: RPC-00877–00896 and RPC-01235–01239

## Scope and result

The 25 rows reduce to ten methods: seven on `allianceRegistry`, one on `corpFittingMgr`, and two on `corpMiningLedger`. Existing Host registrations were found for all methods. This change adds one database-free registered-dispatch wire test for `corpFittingMgr.GetCommunityFittings`, `corpMiningLedger.GetObserversWithMiningEvents`, and `corpMiningLedger.GetObserverLedger`. The test passes `machoVersion=1` and checks the cache-wrapped dictionary and both list reply shapes.

The alliance methods still lack a Host wire regression. They read authoritative corporation/alliance state through PostgreSQL. The task prohibits using PostgreSQL, so this work did not create a persistent alliance fixture or exercise those methods through the Host. Existing service tests in `dotnet/tests/Eve.Corporations.Tests/AllianceTests.cs:756-795` cover alliance membership, ranking, membership-day, and employment row shapes directly, but they do not replace the required Host wire coverage.

The community-fittings provider remains absent from production composition, as already tracked in `docs/project/TODO.md` under “UNIMPLEMENTED COMMUNITY CATALOG PROVIDER”. The new test checks the currently exposed empty cached response, not a populated authoritative catalog. The mining test checks the no-corporation/no-access state; data-backed entries are covered by `dotnet/tests/Eve.Services.Core.Tests/MiningLedgerServiceTests.cs`, which needs a PostgreSQL scratch schema.

## V24.01 caller and consumer evidence

Whole-tree Serena searches in `/home/user/eve-logic` found these direct RPC calls and consumers:

- `GetAlliance`: `eve/client/script/ui/services/alliances/all_cso_alliance.py:40-53` makes the remote call. The alliance wrapper is consumed through `all_cso.py:131-132`; UI and service callers use `executorCorpID` in `ui/shared/export.py:363`, `ui/shared/neocom/Alliances/all_ui_sovereignty.py:40`, `ui/shared/neocom/corporation/corp_ui_wars.py:57`, `ui/shared/neocom/corporation/war/warLandingPage.py:176`, `ui/shared/neocom/corporation/war/warEntry.py:331`, `ui/shared/addressBook/addressbookService.py:1240`, `ui/shared/factionalWarfare/enlistmentFlow/enlistCont.py:164`, `ui/shared/fitting/fittingMgmtCont.py:125,168`, `ui/shared/fitting/fittingUtil.py:361`, `ui/services/menusvc.py:2327`, `ui/shared/neocom/Alliances/all_ui_applications.py:430`, `all_ui_home.py:120,395`, and `all_ui_systems.py:32`. Donation panels also consume it at `ui/shared/neocom/wallet/panels/corp/corpDonateEMPanel.py:46` and `.../personal/personalDonateEMPanel.py:57`.
- `GetAllianceMembers`: `eve/client/script/ui/shared/info/infosvc.py:2895`; the returned members feed alliance-info display.
- `GetAllianceMembersOlderThan`: `eve/client/script/ui/shared/neocom/wallet/panels/corp/corpDonateEMPanel.py:51`; returned corporation IDs populate the eligible donation list and owner-name cache.
- `GetAlliancePublicInfo`: `eve/client/script/ui/services/alliances/all_cso.py:134-135` forwards the RPC; alliance info panels call it at `ui/shared/info/infoContentBase.py:690,1466` and read `warFactionID`; `ui/services/wars/warPermit.py:25-26` reads `executorCorpID`.
- `GetDaysInAlliance`: `ui/shared/neocom/wallet/panels/corp/corpDonateEMPanel.py:43-44` and `.../personal/personalDonateEMPanel.py:55-61` compare the integer day count with the donation minimum, including the executor corporation.
- `GetEmploymentRecord`: `ui/shared/info/infosvc.py:2669-2670`; `GetAllianceHistorySubContent` handles an empty sequence and iterates the history rows for the info panel.
- `GetRankedAlliances`: `ui/services/alliances/all_cso_alliance.py:22-30` caches the returned rows for five seconds; `ui/shared/neocom/Alliances/all_ui_rankings.py:57-65` reads `data.alliances`, then each row's `allianceID` and `executorCorpID`.
- `GetCommunityFittings`: `eve/client/script/environment/fittingSvc.py:399-410` memoizes the returned dictionary under the community fitting owner; `ui/shared/fittingScreen/browsers/fittingBrowser.py:80-85,199-207,271` counts and merges its entries. Cache invalidation is called at `fittingSvc.py:1276`.
- `GetObserversWithMiningEvents`: `eve/client/script/ui/shared/ledger/ledgerSvc.py:31-40` reads each row's `itemID`, `solarSystemID`, and `itemName`, then indexes rows by observer and builds a system list.
- `GetObserverLedger`: `ui/shared/ledger/ledgerSvc.py:43-44` returns the entry rows to the corporation ledger UI.

## EveJS v0.12.9 comparison

The matching functional reference is `/home/user/evejs-v0.12.9`:

- Alliance methods: `server/src/services/corporation/allianceRegistryRuntime.js:526-578` resolves alliance data, produces ranked/member rows, public info and employment history; `:1210-1220` returns membership days and corporations older than a threshold.
- Community fittings: `server/src/services/fitting/corpFittingMgrService.js:55-62` wraps the catalog dictionary in a 15-minute cached method result; `server/src/_secondary/fitting/fittingStore.js:667-669` provides the catalog response.
- Mining ledger: `server/src/services/mining/corpMiningLedgerService.js:83-112` returns visible observer rows and observer-entry rows, or an empty list without corporation, observer, or ledger permission.

## Verification

- `MSBUILDDISABLENODEREUSE=1 dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter FullyQualifiedName~Community_fittings_and_corporate_mining_ledger_use_registered_wire_shapes -p:UseSharedCompilation=false --no-restore`: 1 passed, 0 failed, 0 skipped.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj -p:UseSharedCompilation=false --no-restore`: succeeded, 0 warnings and 0 errors.
- No database connection or graphical-client run was used. The new test constructs an unreachable data source but exercises only the pre-read access branch.
- Push and Actions verification remain pending.
