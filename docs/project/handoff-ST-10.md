# Handoff: ST-10 hacking minigame (structures phase, package ST-10)

Updated at every push. Plan: scratchpad `specs/structures_phase.md`, section ST-10. Rules: `specs/AGENT_RULES.md`. Decisions: DEVIATIONS "Hacking minigame (ST-10)".

## Increments

1. **Engine (built, tests).** `dotnet/src/Eve.Hacking`: board generation (seeded, Rule of 8, Rule of Six, tiers), tile reveal and
   exploration, defensive subsystem combat and retaliation, the four utility elements, data caches, virus suppressor, restoration,
   win and loss. Events carry the client's event ids and named fields (`HackingWire`). No Host, no service yet.
   Status: green. `Eve.Hacking.Tests` 25 passed, 0 failed, 0 skipped.
2. **hackingMgr service (built, tests; Host registration not yet covered by a Host test).** `Eve.Services.Space/HackingMgrService.cs`:
   `ClickedOnTile`, `UsedUtilityElement`, `UsedUtilityElementOnVirus`, `QuitHackingAttempt`; `OnHackingTurnComplete` (clientID) with
   the event list as util.KeyVal records; one `HackingAttempts` store per composition (`SpaceRpc.cs`). A call without an open attempt is
   acknowledged. `HackingMgrService.Start` opens an attempt (the analyzer calls it in increment 3). Tests: `HackingMgrServiceTests`
   5 passed, 0 failed. Gap: no Host-level test that the composition routes the calls, and no end-to-end client run.
3. **Analyzer body (built, tests).** `DataAnalyzerEffect` is a one-cycle `ShipEffect` (repeat 1, module duration and capacitor). Activation
   (`SpaceSessions.Modules.cs`) refuses before the cycle with `ANALYZER_TARGET_INVALID` / `ANALYZER_TYPE_MISMATCH` / `ANALYZER_TARGET_BUSY`
   (`HackingMgrService.AnalyzerRefusalAsync`), and opens the attempt after the simulation accepts it (`OpenAnalyzerAttemptAsync`, virus values
   1909/1910/1911, skill kinds 21718/13278, difficulty less access bonus 902 / 5). The window closes on the next call out of reach
   (`maxRange` 54 + 1 m, sim positions via `SurfaceDistanceToItemAsync`) and on a `WarpTo` accepted for the pilot. A lost or won attempt
   reports `IHackingSite.AttemptEndedAsync`. Targets come from `SpaceHackingSite.Find`, which is empty until increment 4, so every activation
   is refused today. Tests: `HackingMgrServiceTests` (9 passed). `ModuleEffectsTests` manifest updated for doHacking (3 assertions).
   Host routing: `HackingCompositionTests` (1 PgFact) builds the real composition and calls `hackingMgr` through the registry; it found and
   fixed a start-up failure with `EVE_SIM=off` (the service now registers without a space, with no reach).
   Gaps: reach is checked per call, not per tick; the sim's own range rule still applies to the cycle.
4. **Containers and clear (d1 built, tests 4 passed; loot and the end-to-end test not started).** The containers of a new data or relic site
   are placed in the site's fill transaction (`CosmicSiteDirector.PlaceContainersAsync`, rows in `site_containers`, sites schema, migration 0001),
   one per template count, each beside the site (150 m spacing, 50 m radius). They go into the scene at the site's fill and on each pilot's entry
   (`SpaceContainers.RestoreSiteContainersAsync`). `SpaceHackingSite.Find` reads them (`SiteContainerHacks.FindAsync`, secure and unexpired); a win
   clears the row and publishes `site.container.cleared` {solarSystemId, site, container} in one transaction; two failed attempts destroy the row
   and remove the ball. Failures live in PostgreSQL and survive a restart (`SiteContainerTests`, 4 passed). Container types are the static data's names
   in groups 306/314/1207; a name the static data does not have falls back to the lowest type id of the kind's group (306 data, 1207 relic) (DEVIATIONS).
   Still to write: loot on a win (D-5: wiki silent, EveJS data/relic loot tables), and the end-to-end test (site -> container -> analyzer -> win ->
   loot + clear -> site completion; the analyzer cycle itself needs the simulation, so the test opens the attempt through the service).
`hackOrbital` (Data Subverter) stays refused.

## Gotchas

- Run `dotnet test` with `flock -o -w 1800 /tmp/eve-heavy.lock` and `-p:UseSharedCompilation=false`; a build can queue for minutes.
- Bash grep is denied in this worktree: use python for searches.
