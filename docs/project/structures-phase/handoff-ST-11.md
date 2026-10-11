# Handoff: ST-11 cosmic sites (structures phase, package ST-11)

Updated at every push. Plan: scratchpad `specs/structures_phase.md`, section ST-11. Rules: `specs/AGENT_RULES.md`.

## Increments

1. **Foundation (inert, not bound in the host).** Site definitions as content (`anomaly` / `signature`, `site.{id}`), the static
   site table (`Sites/Data/site-templates.json`, from `tools/dev/extract-site-templates.py`), `CosmicSiteDirector` (the recurring
   `site.spawn` job per system, the `content.{anomaly,signature}.{completed,expired}` respawn subscriber, the slots
   `system:{id}:slot:{n}`), `ContentCosmicSites` as the `ICosmicSiteSource` implementation (signatures with their code and level),
   the async `ICosmicSiteSource` contract and the code / dungeon fields of the scan rows. Tests: `CosmicSitesTests.cs`.
   Not bound in `dotnet/src/Eve.Host`, so the server still behaves as before (`NoCosmicSites` is not replaced yet).
   Status: green. `CosmicSite` filter 11 passed; full `Eve.Services.Space.Tests` 627 passed, 0 failed, 0 skipped (EVE_PG_URL set).
2. **Activation (built).** The host binds the director: `SpaceComposition` attaches it before `foundations.StartAsync`, a system a pilot
   enters gets its sites (`SystemEntered` -> `EnsureSitesAsync`), probe scans read `ContentCosmicSites`, the anomaly reader asks the
   director first, and `BeyonceService` warps to a scan result by its code (`ProbeScanWarpAuthority` keyed by text). Tests:
   `CosmicSitesTests` (warp by code, ship numbers unchanged, partial results refused). Not tested end to end: a scan through the
   Beyonce rig with a content-backed source (the rig builds its probe scans with `sites: null`).
3. **Clear and respawn (built, test-only).** A site cleared by its NPC kills (`npc.killed { site, solarSystemId }` counted per wave)
   and its containers completes, the respawn subscriber replaces it in its slot elsewhere, and a host restart in the middle keeps
   the progress (`CosmicSiteDirectorTests.A_site_cleared_by_its_npcs...`). The kills are published by the test; no NPC is spawned yet.
4. **Probe results (built, tests).** A signature's result follows the coverage rule at the wiki strengths (42, 72, 104):
   `SignatureScanResultTests`. The full 100 % of four well-spread probes is the engine's existing rule, not re-derived here.
5. **NPC waves (built, tests).** `SiteNpcs` (`Sites/SiteNpcs.cs`) makes a site's active wave in the scene of its system when a pilot
   is there and no NPC of the site is alive: the wave's query rolled for the target less the progress (`SpaceSessions.SpawnSiteWaveAsync`,
   `SpaceSessions.Npc.cs`). A site's NPC carries its site id in the host's record (`NpcShip.SiteId`), so `npc.killed` carries `site`.
   Spawns on system entry (`SpaceComposition.EnsureSitesAsync`) and on each completed wave (subscriber `site.npc-waves`). No new
   simulation message: no protocol bump. Tests: `SiteNpcTests` (4 PgFact, 1 pure). Decisions: DEVIATIONS "Cosmic site NPC waves".
   Downtime: `CosmicSiteDirector(occupied:)` gives no site and no wave to a system nobody is in (the host passes `HasPilotsIn`).
6. **Containers (blocked by ST-10).** Data and relic sites have containers (groups 306, 1207); combat sites have none. A container is
   opened by the data or relic analyzer (`Effects/ExplorationModuleEffects.cs`, body pending) or by hacking (ST-10), and the containers'
   contents are not in the static data. `site.container.cleared` has no producer until those exist. Next: ST-10, then place the
   containers through `SpaceContainers` and publish the clear there.

## Next

- ST-10 (analyzer and hacking) unblocks item 6. Then close ST-11: delete `/home/user/wt-st11` and its branch.
- Open for review: the placement of a wave (the spawner's formation) against EveJS dungeon placement; the anchor set against EveJS;
  the level from difficulty (SDE above 5 is level V, decided).

## Gotchas

- `PgFact` tests need `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve` (the socket path does not work for psql; use TCP).
- The shared heavy lock (`/tmp/eve-heavy.lock`) queues builds behind other agents; a build can wait a long time. Use
  `flock -o -w 1800 /tmp/eve-heavy.lock <cmd>` (`-o` keeps the lock fd out of children, a VBCSCompiler server that inherits it
  holds the lock forever) and `-p:UseSharedCompilation=false` on `dotnet build` and `dotnet test`.
- Serena's project paths resolve against `/home/user/eve.rs`, not this worktree: do not use Serena writes here.
- `CosmicSiteDirector.Attach` registers the job and the content, so it must run before `ContentRuntime.AttachAsync`
  and before `foundations.StartAsync`.
