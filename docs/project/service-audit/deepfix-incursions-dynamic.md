# Deep correctness handoff: incursions, dynamic content, Abyssal and wormhole sites

Date: 2026-11-02

## Verified change

Incursion creation reported client state `Mobilizing` immediately. The director inserted `phase = mobilized` and scheduled the 48-hour transition from that state (`dotnet/src/Eve.Services.Space/Incursions/IncursionDirector.cs:299-303` before this fix). The client maps state values in `eve/common/lib/appConst.py:1943-1945`; `incursionJournal.py:69-73` indexes its status labels from `data.state`, and Agency stores the same field in `agencyNew/contentPieces/incursionContentPiece.py:21`. EVE University says a new incursion starts Established (up to five days, as short as one day), then Mobilized for 48 hours, then Withdrawing for 24 hours ([Incursions, phases](https://wiki.eveuniversity.org/Incursion#Phases_of_an_incursion), lines 227-239 on the retrieved page).

The director now starts at Established and schedules the one-day minimum documented Established period, then advances Established -> Mobilized -> Withdrawing (`IncursionDirector.cs:294-298,331`). The duration keeps the shortest value described by the wiki; its variable Established duration behavior is not modeled because the page says its exact influence-dependent rule is unknown. The 48-hour and 24-hour phases remain as documented. `ReadActiveAsync` supplies this state to the map/tale read model, and `IncursionAnomalyService.BuildIncursionReport` and `BuildTaleData` serialize `state` (`dotnet/src/Eve.Services.Core/IncursionAnomalyService.cs:224-257`).

Incursion location selection also used the wrong eligibility and system set. It used to accept a constellation with only three total systems and included systems from every security band. UniWiki specifies a minimum of six systems in one broad security category; systems of another security category are excluded from that incursion ([Incursions, constellation layouts](https://wiki.eveuniversity.org/Incursion#Constellation_layouts), lines 223-226; [Constellation layouts for Incursions](https://wiki.eveuniversity.org/Constellation_layouts_for_Incursions)). `IncursionRules.MinimumLayoutSystems` is now six, candidate constellations must have six systems in the specific band, and `SpawnAsync` builds the layout only from systems in that band (`dotnet/src/Eve.Dungeons/Incursions/IncursionRules.cs:77`; `IncursionDirector.cs:119,271`). This makes the client report's `incursedSystems` exclude off-band systems; the map's rowset consumers listed below therefore color and filter the correct systems.

## Client path cross-check

Whole-tree V24.01 search found these consumers:

- Tale updates/removals and solar-system transitions: `eve/client/script/ui/shared/incursionSvc.py:67-122`; the state is retained as `incursionData` and the UI is refreshed at :82 and :120. Status labels consume `state` at `incursionJournal.py:69-73` and Agency stores it at `agencyNew/contentPieces/incursionContentPiece.py:21`.
- `map.GetIncursionGlobalReport`: `eve/client/script/ui/shared/neocom/journal.py:780-782` consumes report fields and requests delayed rewards; `agencyNew/contentProviders/contentProviderIncursions.py:41` also calls it.
- `map.GetSystemsInIncursions`: callers are `maps/starModeHandler.py:977,1153`, `mapView/mapViewColorHandler.py:954,983`, `mapView/filters/mapFilterIncursion.py:19`, and `agencyNew/contentPieces/incursionContentPiece.py:88`. These use the rows to color/filter participating systems and render the Agency entry.
- Cosmic signatures and anomalies enter the probing UI through `scanSvc.GetResults`, which returns `(results, ignored, filtered, anomalies)` (`eve/client/script/parklife/scanSvc.py:584-587`); `scannerFiles/scanResults.py:202-236` renders those rows and uses the three non-result values for its empty-state UI. `OnCosmicSignatureAdded` / `OnCosmicAnomalyAdded` highlight new site IDs (`scannerFiles/scanResults.py:102-128`, `probeScannerWindow.py:125-126`). The existing wormhole site tests exercise plan-to-signature/anomaly classification and probe target strength at this boundary.
- Wormhole entry is `menuSvcExtras/movementFunctions.py:592-598`, a one-argument session change. Polarization is queried on component addition at `spacecomponents/client/components/jumpPolarization.py:23-37`; `None` clears the display state and a pair drives the timer and follow-up component update. `spaceMgr.py:93-97` consumes `OnWormholeJumpCancel` and shows the collapsed-wormhole message. The earlier `deepfix-wormholes.md` records its Host transition and mass-race fix.
- Abyss entry and exit session transitions are `menuSvcExtras/movementFunctions.py:565-578` (`AbyssalGateActivation`, `AbyssalEndGateActivation`). No change was made to either travel path in this increment.

## EveJS comparison

EveJS 261028.1's `server/src/services/incursion/incursionRuntime.js:1-20` only stores/clones the current incursion list; `incursionPayloads.js:14-18,55-90` defaults missing state to Established and serializes the client report shape. It has no durable lifecycle, timer or site-spawn flow to compare against. The eve.rs lifecycle remains PostgreSQL-backed and uses the shared Content/DungeonWave runtime; this change corrects its initial client-visible state without copying the EveJS in-memory storage.

## CI red-gate fix

Main CI run 1418 failed `AbyssalSpaceSessionsTests.Disconnect_persists_the_domain_transition_but_keeps_the_sim_resident_ship`: `LocalTimers.Set` cancelled a previous entry after its timer task disposed the `CancellationTokenSource`. `LocalTimers.Entry.Cancel` now treats that interleaving as an already-finished timer (`dotnet/src/Eve.Scheduler/LocalTimers.cs:16-27,53,100`). The Abyssal gate client call sequence remains `AbyssalGateActivation` / `AbyssalEndGateActivation` in `eve/client/script/ui/services/menuSvcExtras/movementFunctions.py:565-578`; no wire behavior changed. The focused CI test passed locally before and after the guard, so the CI timing did not reproduce locally. After the fix, `LocalTimersTests` passed 5/5; `AbyssalSpaceSessionsTests`, `IncursionDirectorTests` and `CosmicSiteDirectorTests` passed 39/39 on `eve_p4`.

## Regression evidence

- Before the code fix, changing the spawn assertion in `IncursionDirectorTests.Each_band_is_filled_to_its_capacity_with_a_staging_incursion_and_its_five_staging_sites` to Established failed on all five new incursions: expected state `2`, received `1`.
- With phase correction in place and band filtering temporarily removed, the same spawn test failed because system 4006 was reported as LowSec inside a HighSec incursion. With band filtering restored, `IncursionDirectorTests` passed on `eve_p4`: 11 passed, 0 failed, 0 skipped. This includes six-system eligibility, off-band exclusion, phase timing, restart, site influence completion, mothership conditions and map read-model values (`dotnet/tests/Eve.Services.Space.Tests/IncursionDirectorTests.cs:31,62,133,188,226`).
- `dotnet build dotnet/tests/Eve.Services.Space.Tests -p:UseSharedCompilation=false` passed with 0 warnings and 0 errors.
- `dotnet build dotnet/tests/Eve.Dungeons.Tests -p:UseSharedCompilation=false` passed with 0 warnings and 0 errors; `IncursionRulesTests` passed 23/23.
- Existing `CosmicSiteDirectorTests` were also run through `tools/dev/db-lock.sh` on `eve_p4`: 8 passed, 0 failed, 0 skipped. They cover wormhole plan population, probes, expiry replacement, resource cleanup and restart behavior; no change was made to that path.

## Scope not exercised

The V24.01 graphical client was not run. Dynamic content and wormhole site-generation behavior were not changed in this increment; the documented wormhole handoff covers jump semantics rather than generated-site population. Incursion Established duration remains fixed at the documented one-day minimum pending evidence for its variable duration rule.
