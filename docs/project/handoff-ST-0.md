# ST-0 handoff (Structure record owner, timeline, timers, GameStore import)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-0). Branch `wt-st0`, worktree `/home/user/wt-st0`.

## Done and pushed
- `dotnet/src/Eve.Structures`: the pure timeline (`StructureStateMachine`, `StructureModel`), the schema (`Migrations/0001_structures.sql`),
  `StructureStore` and `StructureTimers`. The timeline is tested (`dotnet/tests/Eve.Structures.Tests/StateMachineTests.cs`, 47 pass).
- The decisions D-1 (SDE `rigSize`) and D-2 (30 days) are in DEVIATIONS "Structure timeline (ST-0)".

## Next step
1. Postgres tests for the store and the timers with `Rig.cs` (scratch schemas `str_*`, `sch_*`, `evt_*`): restart, replay, import idempotency, event with change.
2. The Host rules source over the SDE, the migration and timers registered in the Host, the GameStore import call, `ArchitectureTests`.
3. The reader switch, file by file, with the existing structure tests green.

## Files
- `dotnet/src/Eve.Structures/*`, `dotnet/tests/Eve.Structures.Tests/*`, `dotnet/EveRs.slnx`
- `docs/reference/compat/DEVIATIONS.md` ("Structure timeline (ST-0)"), `docs/project/TODO.md` (section 44)

## Gotchas
- `StructureStore.ToLegacy` gives the readers the legacy JSON shape of the GameStore table, so the readers switch with one delegate; the write paths
  (`PersistenceStructureProfilesRpcBackend` `WriteTableAsync`, `PersistenceStructureDirectoryReadModel` `FlushTableAsync`) need store calls instead.
- The heavy build lock: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test ... -p:UseSharedCompilation=false` (the compiler server keeps the lock fd otherwise).
- Postgres: `tools/dev/pg18.sh start` in `/home/user/eve.rs`; tests need `EVE_PG_URL=postgres://postgres@127.0.0.1:5432/eve`.
- Serena MCP timed out this session; repository reads used python3 (allowed by AGENT_RULES.md).
