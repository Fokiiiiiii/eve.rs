# EveJS 261108.1 wormhole port notes

Date: 2026-11-10. Target client: EVE V24.01 build 3396210.

## Result

Ported the documented named Sleeper compositions and wave recovery for Core Citadel (dungeon 3704). The old eve.rs template chose each ship from the full `sleepers_deadspace` pool, so the first wave did not match the published site table. The implementation now spawns the authored type IDs, records typed kills in the content instance's existing JSON state, and restores only the remaining ships after the system scene is recreated.

The rest of the 261108.1 changes to wandering populations, Thera slot counts, and other sites were not copied without matching retail evidence. Details and remaining checks follow.

## Client and gameplay evidence

The V24.01 client call sites were searched across the full `/home/user/eve-logic` tree. `movementFunctions.py:593-599` calls `wormholeMgr.WormholeJump(itemID)`. `spaceObject/wormhole.py:15-29`, `infoContentBase.py:1500-1548`, and `spaceMgr.py:489-506` consume the server's wormhole class, age, mass-size and destination-type fields. `eveCfg.py:535-537` looks up a system's wormhole class. The client code defines these presentation and travel contracts; it does not author Sleeper site NPC populations.

CCP's [Thera and the Shattered Wormholes](https://www.eveonline.com/news/view/thera-and-the-shattered-wormholes) article confirms that Thera has multiple static exits to high-, low- and null-security space, plus wandering and K-space-to-W-space connections. It does not give the number of statics in each security band. The EVE University [Core Citadel](https://wiki.eveuniversity.org/Core_Citadel) regular Sleeper table gives the wave compositions below. Its capital escalation is separate and was not included.

EveJS 261108.1's `content-packs/evejs-wormhole-sites-1.2.1.json` records those NPC rows as sourced from the Core Citadel EVE University page (first encounter around lines 2421-2600; later waves around 2895-5079). Its manifest targets server SDE build 3396210 but client dungeon build 3475087 (`manifest.builtAgainst`, lines 26-29). I used the named NPC rows as corroborating reference data, not the newer client anchors or layout.

## Core Citadel composition port

| Wave | Spawned ships |
|---|---|
| 1 | 1 Sleepless Keeper, 1 Sleepless Sentinel, 1 Sleepless Warden |
| 2 | 8 Emergent Keeper, 3 Sleepless Sentinel, 1 Sleepless Warden |
| 3 | 8 Emergent Keeper, 3 Sleepless Keeper, 3 Sleepless Warden |
| 4 | 8 Emergent Keeper, 2 Sleepless Guardian |

These are the public row counts, not a claim that a graphical V24 client trace was captured. The existing NPC authority already has matching profiles and the pinned SDE supplies their type records. `wormhole-site-templates.json` stores the per-wave `profileCounts`; `SpaceSessions.Npc.cs` resolves them through `NpcAuthority`, loads the SDE types, checks that each hull has hit points, and uses the normal NPC spawn path. The Host registers the same `SiteNpcs.Register` method exercised by the space-service fixture.

The flow is: the active content objective selects a wave; `SiteNpcs` asks `SpaceSessions` to spawn its named profiles; a kill publishes `npc.killed` with the site ID and type ID; the Host event subscriber persists per-type kill totals through `ContentRuntime.UpdateDataAsync` in the event transaction; objective completion starts the next wave. On scene recreation, the stored type ledger and objective progress determine which profiles remain. Older in-progress sites without a complete typed ledger keep the existing generic recovery path.

The PostgreSQL-backed `SiteNpcTests` exercises the real content runtime, event bus, `SpaceSessions`, Host registration helper, persisted kill ledger, scene rebuild, and all four wave transitions. The regression first failed on the generic pool (expected type IDs `[30196, 30197, 30198]`, received `[30202, 30209, 30211]`). After the port, it verifies the exact type IDs across every Core Citadel wave and that the killed Keeper is not respawned after the scene rebuild.

## Already present in eve.rs

- `WormholeTypeCatalog` reads target class, lifetime, stable mass and maximum jump mass from the pinned build 3396210 SDE dogma attributes 1381-1385. `WormholeRules` and `WormholeLifecycle` derive transit eligibility, remaining mass, collapse, age and slim-state values from those fields. These SDE-backed rules were not changed.
- `WormholePlanner` seeds the configured J-space statics and rolls starting mass in the existing 90%-110% band. `WormholeWorld` persists the world through `WormholeStore`; `WormholeJobs` handles expiry, mass collapse, static replacement, and client-visible age updates.
- `TheraConnections` already recognizes the eight wormhole types that name Thera at one end, including Q063 to high-sec, V898 to low-sec and E587 to null-sec. It seeds 12 selected connections by drawing among those types. The project documents this as an approximation based on EVE University's “a dozen or so” active connections, not an exact retail static-slot table.
- The current 68 wormhole site templates already define NPC objective totals and generic Sleeper pools. Exact profiles existed in `NpcAuthority`; the missing part addressed here was using named profiles for Core Citadel and retaining their type-specific kill history across a scene rebuild.
- No Turnur-specific wormhole static/wandering implementation was found in `dotnet/src`. The K-space Turnur record in EveJS's static table has an empty `staticSlots` array (file lines 37601-37615). Its separate Zarzakh gate behavior is not a wormhole rule.

## Not ported and why

- **K-space wandering population:** EveJS's `server/src/services/exploration/wormholes/kspaceWanderingProfiles.json:5` explicitly calls its populations an “Emulator baseline, not measured retail counts.” `wormholeRuntime.js:1160-1164` turns those estimates into desired counts. The rate and counts have no retail evidence, so eve.rs continues to leave K-space wandering connections cut.
- **Exact Thera slot counts:** EveJS's `tools/DatabaseCreator/staticTables/explorationWormholeStatic/data.json:95486-95606` assigns Thera eight K-space static slots (two Q063, three V898 and three E587). Its `source.staticSpec` points to `local:scripts\\DataSync\\spec\\explorationWormholeStatics.json` at line 6, but that spec is absent from the supplied EveJS tree. CCP confirms the destination bands but publishes no per-band count. The eight-slot table therefore remains unverified and was not substituted for eve.rs's existing selected-connection model.
- **Other site NPC compositions:** the pack has 48 site templates and labels its encounter rows `exact: false` with a documented-minimum policy. Its client dungeon build is newer than the requested client. The first pass ported only Core Citadel because its four regular waves were checked directly against the named EVE University table and fit eve.rs's existing wave objectives. The remaining named rows have not yet been independently checked against their site pages, especially encounters that mix Sleeper ships with sentry towers or whose listed ship count does not equal the current kill objective. Those waves continue to use their existing generic pool until that source/objective reconciliation is complete.
- **Client dungeon anchors and layouts:** not copied from the pack because its manifest names build 3475087 rather than the target 3396210. Core Citadel ships continue to use eve.rs's established site anchor and spawn-distance rules.

## Verification limits

Verified by headless PostgreSQL-backed tests and Release builds in this worktree. The change was not exercised with the graphical client or a TQ trace. No EveJS wormhole-site test runner was present in the supplied tree; its pack's Core Citadel entries and spawn path were traced as the functional reference, while this change's actual end-to-end test ran against eve.rs. The SDE snapshot used for local checks is build 3396210.
