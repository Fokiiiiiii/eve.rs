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
5. **NPC waves (open).** The director's objectives count `npc.killed { site, solarSystemId }`, but no NPC is spawned for a site yet and
   no event carries `site`. Until this lands an anomaly is never cleared, only expired.
6. **Containers.** Objectives `containers` count `site.container.cleared`; no container is placed yet (`SpaceContainers`).

## Next

- NPC spawning for a site (item 5) and containers (item 6), both need the sim side; then a real kill through the simulation.

## Gotchas

- `PgFact` tests need `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve` (the socket path does not work for psql; use TCP).
- The shared heavy lock (`/tmp/eve-heavy.lock`) queues builds behind other agents; a build can wait a long time. Use
  `flock -o -w 1800 /tmp/eve-heavy.lock <cmd>` (`-o` keeps the lock fd out of children, a VBCSCompiler server that inherits it
  holds the lock forever) and `-p:UseSharedCompilation=false` on `dotnet build` and `dotnet test`.
- Serena's project paths resolve against `/home/user/eve.rs`, not this worktree: do not use Serena writes here.
- `CosmicSiteDirector.Attach` registers the job and the content, so it must run before `ContentRuntime.AttachAsync`
  and before `foundations.StartAsync`.
