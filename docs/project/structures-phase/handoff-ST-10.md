# Handoff: ST-10 hacking minigame (structures phase, package ST-10) — CLOSED 2026-10-11

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
4. **Containers, loot, clear and the end-to-end chain (built; package-close baseline, follow-up below).** A new data or relic site places its containers in
   its fill transaction (`CosmicSiteDirector.PlaceContainersAsync`): each container is an inventory item of its own (`NewContainerAsync`),
   its id is the container's, and its row is in `site_containers` (sites schema, migration 0001). The containers go into the scene on the fill
   and on each pilot's entry. `SpaceHackingSite.Find` reads them. A win puts the profile's loot (`SiteLoot`: EveJS generic_data_loot /
   generic_relic_loot, all tags) into the container's cargo, owned by the winner, and leaves it looted until empty (a win with no loot clears immediately). The last item move clears the row, publishes `site.container.cleared`, and removes the ball. Two lost
   attempts destroy the row and remove the ball; failures survive a restart. Tests: `SiteContainerTests` (4), `SiteLootTests` (3),
   `ContainerSiteEndToEndTests` (1: fill -> containers -> attempts through `HackingMgrService` with a winning board -> loot in cargo -> cleared ->
   the site leaves the active list). The package-close review left both EveJS behaviors open; see the 2026-10-14 follow-up below for the implementation and parent-reported test result.
   Fixed in this round: the d1 fill never wrote its placement (the placement code was lost before the d1 commit); the test that caught it is the
   end-to-end one.
`hackOrbital` (Data Subverter) stays refused.

## Gotchas

- Run `dotnet test` with `flock -o -w 1800 /tmp/eve-heavy.lock` and `-p:UseSharedCompilation=false`; a build can queue for minutes.
- Bash grep is denied in this worktree: use python for searches.

## Follow-up (2026-10-14)

The production item-move publisher and Host subscriber were already wired. Inventory MultiAdd reaches CharacterShips; PgItemStore writes inventory.item_moved in the move transaction; SpaceComposition routes it to SiteContainerHacks.OnItemLeftAsync. Site containers remain owner 0; SpaceContainers allows only matching looted rows and selected cargo owned by the pilot. A looted row restores its ball after restart; the final committed move clears the row, emits site.container.cleared, and removes the ball.

Parent-reported Cockroach regression: SiteContainerRestartTests — 1 PASS, 0 FAIL, 0 SKIP (3s). Native graphical-client verification and MITM comparison remain unverified.

## Closed

ST-10 package closed 2026-10-11; this 2026-10-14 follow-up closes the two container-emptying behaviors. Decisions: DEVIATIONS "Hacking minigame (ST-10)", "Site loot", "Container items", "Site container types", "Site completion", "Cleared container ball".
