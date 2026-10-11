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
4. **Containers, loot, clear and the end-to-end chain (built; two EveJS items open).** A new data or relic site places its containers in
   its fill transaction (`CosmicSiteDirector.PlaceContainersAsync`): each container is an inventory item of its own (`NewContainerAsync`),
   its id is the container's, and its row is in `site_containers` (sites schema, migration 0001). The containers go into the scene on the fill
   and on each pilot's entry. `SpaceHackingSite.Find` reads them. A win puts the profile's loot (`SiteLoot`: EveJS generic_data_loot /
   generic_relic_loot, all tags) into the container's cargo, owned by the winner, clears the row and publishes `site.container.cleared`. Two lost
   attempts destroy the row and remove the ball; failures survive a restart. Tests: `SiteContainerTests` (4), `SiteLootTests` (3),
   `ContainerSiteEndToEndTests` (1: fill -> containers -> attempts through `HackingMgrService` with a winning board -> loot in cargo -> cleared ->
   the site leaves the active list). Open, both EveJS: (a) the site completes on the win, not when the hacked containers are emptied; (b) a
   cleared container's ball stays in the scene (EveJS removes an emptied one). Both recorded in DEVIATIONS.
   Fixed in this round: the d1 fill never wrote its placement (the placement code was lost before the d1 commit); the test that caught it is the
   end-to-end one.
`hackOrbital` (Data Subverter) stays refused.

## Gotchas

- Run `dotnet test` with `flock -o -w 1800 /tmp/eve-heavy.lock` and `-p:UseSharedCompilation=false`; a build can queue for minutes.
- Bash grep is denied in this worktree: use python for searches.
