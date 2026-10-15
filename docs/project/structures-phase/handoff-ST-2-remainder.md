# ST-2 remainder handoff (worktree wt-st2b)

Scope: the three open items after upstream work (anchor refusals and sovereignty delay, the reinforcement-change notice, the structureProfiles write gap).

## Done

- structureProfiles write gap (root fix): `PersistenceStructureProfilesRpcBackend.WriteTableAsync` loads the table into the game store cache before writing. The cache refuses a write to an unloaded table (TABLE_NOT_FOUND), so a corporation with no default profile could never get one. Regression: `StructureCompositionTests.A_fresh_store_creates_the_corporations_default_profile_on_the_first_profile_change_and_keeps_it_after_a_restart` (red before, green after; StructureCompositionTests 12/12).

## Verified in code on main, and now run green

- Anchor refusals and default profile: `StructureAnchorTests` (13 with the drop tests) and `StructureAnchorDelayTests`, real PostgreSQL and the pinned SDE. Covers Thera, shattered J015092, starter/career systems, trade hubs, ADM 4.0, extra-large, 1000 km, item id taken.
- Sovereignty delay: `StructureTimerAnchoringDelayTests` (5 with the Structures suite run): persisted `anchoring_ms`, the 15-minute transition, restart, stale TimerDue replay, rollback.
- Reinforcement-change notice: `StructureCompositionTests` (12/12), and now with a forced redelivery through the real bus.
- Unanchor drop: `StructureUnanchorDropTests`, `StructureUnanchorCompletionTests`, `SpaceContainersUnanchorDropsTests` (2/2), Rust group 306 classification.

## Full runs before the final push (eve_p3, db-lock)

- Eve.Host.Tests: 334/334 passed, 0 skipped (9 m 7 s).
- Eve.Structures.Tests: 105/105 passed, 0 skipped.
- Eve.Services.Space.Tests, `SpaceContainersUnanchorDropsTests`: 2/2 passed.

## Commits on main

- 076a9f2: structureProfiles loaded before its first write (the root fix; red then green).
- fada4dd: the reinforcement notice proven once across a forced redelivery; DEVIATIONS and TODO updated.

## Remaining (not in this package)

- Native graphical client acceptance for deployment, drop and scoop (no MITM capture).
- The registered Anchor pilot route needs the simulation's ship position; without the sim it refuses "not in space".
- The unanchor drop and the sovereignty delay are decided in DEVIATIONS "Structure anchoring, unanchoring and notices (ST-2 b, c, d)".

## Gotchas

- Use `EVE_PG_URL='Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p3'` (key=value) for dotnet; wrap PG runs in `tools/dev/db-lock.sh`.
- Build once with `dotnet build`, then `dotnet test --no-build`.
