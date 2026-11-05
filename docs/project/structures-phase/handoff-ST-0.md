# ST-0 handoff (Structure record owner, timeline, timers, GameStore import)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-0). Branch `wt-st0`, worktree `/home/user/wt-st0`.

## Done and pushed
- `dotnet/src/Eve.Structures`: the pure timeline (`StructureStateMachine`, `StructureModel`), the schema (`Migrations/0001_structures.sql`),
  `StructureStore` and `StructureTimers`. The timeline is tested (`StateMachineTests.cs`, 47 pass); the store and timers against PostgreSQL
  (`StructureTimersTests.cs`, 6 pass; 53 in the project with `EVE_PG_URL`).
- The decisions D-1 (SDE `rigSize`) and D-2 (30 days) are in DEVIATIONS "Structure timeline (ST-0)".

## Host wiring (done, pushed)
- Start-up: `StructureRecords()` migrates the schema and copies the game store's `structures` table once; it runs right after the character stores migrate.
- Timers: `StructureRulesSource` (SDE) and `StructureTimers` are registered in `Structures()`; deadlines of imported structures are scheduled.
- Readers: `ReadGameStoreTableAsync("structures")` returns `StructureStore.ReadLegacyTableAsync` (`Composition/GameStoreReaders.cs`).
- Writers: the description (`PersistenceStructureDirectoryReadModel`) and the profile change (`PersistenceStructureProfilesRpcBackend`) write the store.

## Status
ST-0 is closed: the module, the timeline, the timers, the store, the Host wiring and the readers and writers are on main, with the tests in TODO section 44.
The open items there (the profile root write, the sim ball and the client notices) belong to other packages.

## Files
- `dotnet/src/Eve.Structures/*`, `dotnet/tests/Eve.Structures.Tests/*`, `dotnet/EveRs.slnx`
- `docs/reference/compat/DEVIATIONS.md` ("Structure timeline (ST-0)"), `docs/project/TODO.md` (section 44)

## Gotchas
- `StructureStore.ToLegacy` gives the readers the legacy JSON shape of the GameStore table, so the readers switch with one delegate. The
  structure profile root (`structureProfiles`, GameStore) still has the TABLE_NOT_FOUND write gap, see TODO section 44.
- The heavy build lock: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test ... -p:UseSharedCompilation=false` (the compiler server keeps the lock fd otherwise).
- Postgres: `tools/dev/pg18.sh start` in `/home/user/eve.rs`; tests need `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve`.
- `Rig.Create` must pump the bus once after creating its subscriber, or the first events are skipped as history (fixed in `Rig.cs`).
- `EVE_PG_URL`: use the URL form (`postgres://postgres@127.0.0.1:5432/eve`). `psql` in `tools/dev/run-eve-live.sh` rejects the key=value form. The host and the tests convert the URL with `PgConnection.ToConnectionString`.
- Serena MCP timed out this session; repository reads used python3 (allowed by AGENT_RULES.md).
