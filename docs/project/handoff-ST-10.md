# Handoff: ST-10 hacking minigame (structures phase, package ST-10)

Updated at every push. Plan: scratchpad `specs/structures_phase.md`, section ST-10. Rules: `specs/AGENT_RULES.md`. Decisions: DEVIATIONS "Hacking minigame (ST-10)".

## Increments

1. **Engine (built, tests).** `dotnet/src/Eve.Hacking`: board generation (seeded, Rule of 8, Rule of Six, tiers), tile reveal and
   exploration, defensive subsystem combat and retaliation, the four utility elements, data caches, virus suppressor, restoration,
   win and loss. Events carry the client's event ids and named fields (`HackingWire`). No Host, no service yet.
   Status: green. `Eve.Hacking.Tests` 25 passed, 0 failed, 0 skipped.
2. **hackingMgr service (not started).** Host registration of `ClickedOnTile`, `UsedUtilityElement`, `UsedUtilityElementOnVirus`,
   `QuitHackingAttempt`; notification `OnHackingTurnComplete` with the event list; attempts held per character in memory.
3. **Analyzer body (not started).** `DataAnalyzerEffect` (`Effects/ExplorationModuleEffects.cs`) starts an attempt on accepted activation
   (EveJS); window closes on warp and when out of `maxRange` 54; two failures destroy the container.
4. **Containers and clear (not started).** Data and relic containers placed through `SpaceContainers`; `site.container.cleared` on
   success (ST-11 item 6); loot per D-5 (wiki silent, EveJS `maybeCompleteAttemptSite`).

`hackOrbital` (Data Subverter) stays refused.

## Gotchas

- Run `dotnet test` with `flock -o -w 1800 /tmp/eve-heavy.lock` and `-p:UseSharedCompilation=false`; a build can queue for minutes.
- Bash grep is denied in this worktree: use python for searches.
