# Handoff: ST-11 cosmic sites (structures phase, package ST-11)

Updated at every push. Plan: scratchpad `specs/structures_phase.md`, section ST-11. Rules: `specs/AGENT_RULES.md`.

## Increments

1. **Foundation (inert, not bound in the host).** Site definitions as content (`anomaly` / `signature`, `site.{id}`), the static
   site table (`Sites/Data/site-templates.json`, from `tools/dev/extract-site-templates.py`), `CosmicSiteDirector` (the recurring
   `site.spawn` job per system, the `content.{anomaly,signature}.{completed,expired}` respawn subscriber, the slots
   `system:{id}:slot:{n}`), `ContentCosmicSites` as the `ICosmicSiteSource` implementation (signatures with their code and level),
   the async `ICosmicSiteSource` contract and the code / dungeon fields of the scan rows. Tests: `CosmicSitesTests.cs`.
   Not bound in `dotnet/src/Eve.Host`, so the server still behaves as before (`NoCosmicSites` is not replaced yet).
   Status: green, `CosmicSite` filter 11 passed, 0 skipped (EVE_PG_URL set); full Services.Space suite run in progress at push time.
2. **Activation (next).** Host wiring: `SpaceComposition` creates and attaches the director before `foundations.StartAsync`,
   `SystemEntered` runs `EnsureAsync`, `ContentCosmicSites` is passed to `SpaceProbeScans`, the anomaly read model asks the
   director first, `BeyonceService` warps to a scan result by its code. The patch is kept at scratchpad `st11/activation.patch`
   (it was written against the tree of increment 1; re-check it when applying).
3. **NPC waves.** The director's objectives count `npc.killed { site, solarSystemId }`, but no NPC is spawned for a site yet and
   no event carries `site`. Until this lands an anomaly is never cleared, only expired.
4. **Containers.** Objectives `containers` count `site.container.cleared`; no container is placed yet (`SpaceContainers`).

## Gotchas

- `PgFact` tests need `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve` (the socket path does not work for psql; use TCP).
- The shared heavy lock (`/tmp/eve-heavy.lock`) queues builds behind other agents; a build can wait a long time.
- Serena's project paths resolve against `/home/user/eve.rs`, not this worktree: do not use Serena writes here.
- `CosmicSiteDirector.Attach` registers the job and the content, so it must run before `ContentRuntime.AttachAsync`
  and before `foundations.StartAsync`.
