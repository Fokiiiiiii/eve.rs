# ST-6a handoff (moon extraction record, timers and RPC mutations, host side)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-6). Branch `wt-st6a`, worktree `/home/user/wt-st6a`.

## Done (pushed increments)
- (a) Record and reads: migration `0003_moon_extractions.sql` (tables `structure_moon_extractions`, `structure_moon_resources`, schema of the structures).
  `Eve.Structures/MoonExtractionStore.cs`: `ReadLegacyAsync` (the EVE.js shape of the active and fracturing extractions and of the resource profiles),
  `ImportAsync` (copies GameStore `moonExtractions` once; rows already there are kept). Host: `StructureRecords()` imports the table in the same
  transaction as the structures; `StructureRpc.cs` reads the store (`ReadMoonExtractionsAsync`). `MoonExtractionsRpcService` reads asynchronously now.
  Tests: `Eve.Host.Tests/MoonExtractionStoreTests.cs` (2 PostgreSQL tests, the reads through the real service, idempotent import).

## Done (increment b, pending push at the time of writing)
- (b) `StartNewExtraction`: `MoonExtractions.StartAsync` (domain, `Eve.Structures/MoonExtractions.cs`) and `StructureMoonAuthority` (Host gate, the named
  refusals, the caller's `OnMoonExtractionUpdate`), the SDE rules `MoonRulesSource` (2698, 2728 x 2706, Pochven), the chunk at 30 000 m3/h split by the moon.
  Tests: `MoonExtractionStartTests` (7, Structures) and `MoonExtractionStartHostTests` (6, Host). Decisions: DEVIATIONS "Moon extraction (ST-6a)".

## Open (next increments)
- (c) durable jobs `moon.chunk_arrival`, `moon.auto_fracture`, `moon.field_decay`, (d) CancelExtraction and
  FractureChunkWithStructure with OnMoonExtractionUpdate. Until (b) lands, the three mutations keep their named refusal.
- The moon binding is the seeded resource profile (`structure_moon_resources`); EveJS's `moonMiningBindingRuntime` (bind on read) is not ported.
- The rig modifiers (SDE 2707 fracture delay, 2704 yield) are not applied yet.
- Character notifications of the owner corporation (EveJS `createNotification` for STARTED, FINISHED, LASER_FIRED, CANCELLED, AUTOMATIC_FRACTURE): no
  notification store exists in eve.rs; a gap.
- The control gate (EveJS `isControllingStructureSession` and the profile access setting) waits for ST-3/ST-5; until then the gate is docked at the structure
  and owned by the pilot's corporation (to be recorded in DEVIATIONS when (b) lands).

## Gotchas
- `EVE_PG_URL` is key=value: `Host=127.0.0.1;Username=postgres;Database=eve`.
- Builds: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test <project> -p:UseSharedCompilation=false`.
- A `Func<Task<JsonObject>>` does not convert to `Func<Task<JsonObject?>>` (CS8619); wrap the read in a method with a nullable return type.
