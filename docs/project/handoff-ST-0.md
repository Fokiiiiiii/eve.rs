# ST-0 handoff (Structure record owner, timeline, timers, GameStore import)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-0). Branch `wt-st0`, worktree `/home/user/wt-st0`.

## Done and pushed
- `dotnet/src/Eve.Structures`: the pure timeline (`StructureStateMachine`, `StructureModel`), the schema (`Migrations/0001_structures.sql`),
  `StructureStore` and `StructureTimers`. The timeline is tested (`StateMachineTests.cs`, 47 pass); the store and timers against PostgreSQL
  (`StructureTimersTests.cs`, 6 pass; 53 in the project with `EVE_PG_URL`).
- The decisions D-1 (SDE `rigSize`) and D-2 (30 days) are in DEVIATIONS "Structure timeline (ST-0)".

## Written, inert (not yet clients-visible)
- Host start-up: `StructureRecords()` migrates the schema and copies the game store's `structures` table once (`Composition/StructureComposition.cs`).
  `ArchitectureTests` lists `Eve.Structures`; `Eve.Scheduler` and `Eve.Events` are the allowed foundations.
- Readers and writers still use the GameStore table. The store is a copy until the switch below.

## Next step
1. The Host rules source over the SDE (`rigSize`, system security band), and `StructureTimers.Register()` with a first pump after start.
2. The reader switch, file by file, with the existing structure tests green: the `ReadGameStoreTableAsync` "structures" branch
   (`Composition/GameStoreReaders.cs`) to `StructureStore.ReadLegacyTableAsync`, and the two writes (`PersistenceStructureProfilesRpcBackend`
   `ChangeProfilesAsync` → `SetProfileAsync`; `PersistenceStructureDirectoryReadModel` description → `SetDescriptionAsync`).
3. A Host test that starts the composition on a seeded game store and checks the copied rows and the store-backed reads.

## Files
- `dotnet/src/Eve.Structures/*`, `dotnet/tests/Eve.Structures.Tests/*`, `dotnet/EveRs.slnx`
- `docs/reference/compat/DEVIATIONS.md` ("Structure timeline (ST-0)"), `docs/project/TODO.md` (section 44)

## Gotchas
- `StructureStore.ToLegacy` gives the readers the legacy JSON shape of the GameStore table, so the readers switch with one delegate; the write paths
  (`PersistenceStructureProfilesRpcBackend` `WriteTableAsync`, `PersistenceStructureDirectoryReadModel` `FlushTableAsync`) need store calls instead.
- The heavy build lock: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test ... -p:UseSharedCompilation=false` (the compiler server keeps the lock fd otherwise).
- Postgres: `tools/dev/pg18.sh start` in `/home/user/eve.rs`; tests need `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve`.
- `Rig.Create` must pump the bus once after creating its subscriber, or the first events are skipped as history (fixed in `Rig.cs`).
- `EVE_PG_URL` as the README's `postgres://` URL makes 13 Host tests fail in `NpgsqlDataSource.Create` ("Format of the initialization string"); the key=value form (`Host=127.0.0.1;Port=5432;Username=postgres;Database=eve`) passes all 163. Not changed here; see the report.
- Serena MCP timed out this session; repository reads used python3 (allowed by AGENT_RULES.md).
