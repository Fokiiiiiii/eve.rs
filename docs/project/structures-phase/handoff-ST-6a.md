# ST-6a handoff (moon extraction record, timers and RPC mutations, host side)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-6). Branch `wt-st6a`, worktree `/home/user/wt-st6a`.

## Done (pushed to main, in order)
- (a) Record and reads: migration `0003_moon_extractions.sql` (`structure_moon_extractions`, `structure_moon_resources`, schema of the structures).
  `Eve.Structures/MoonExtractionStore.cs`: `ReadLegacyAsync` (the EVE.js shape of the non-completed extractions and of the resource profiles), `ImportAsync`
  (copies GameStore `moonExtractions` once; rows already there are kept). Host: `StructureRecords()` imports in the structures transaction; the moon service
  reads the store (`ReadMoonExtractionsAsync`). Tests: `MoonExtractionStoreTests` (2).
- (b) `StartNewExtraction`: `MoonExtractions.StartAsync` (domain, `Eve.Structures/MoonExtractions.cs`); `StructureMoonAuthority` (Host gate, named refusals,
  the caller's `OnMoonExtractionUpdate`); `MoonRulesSource` (SDE 2698, 2728 x 2706, Pochven). Chunk at 720 000 m3 a day (30 000 m3/h), split by the moon.
  Tests: `MoonExtractionStartTests` (7, Structures), `MoonExtractionStartHostTests` (Host).
- (c) Jobs: `moon.chunk_arrival` (ready at the chunk time; publishes `moon.chunk_arrived`), `moon.auto_fracture` (natural decay, or the end of a fracture),
  `moon.field_decay` (48 h after a fractured or decayed chunk; `field_decayed_ms`, migration 0004; publishes `moon.field_decayed`). Completion publishes
  `moon.extraction_ended {reason}`; the Host subscriber `moon.session_notice` sends `OnMoonExtractionUpdate` to the corporation's and the structure's
  sessions. Reactions: a drill offline (service 14) ends a chunk not yet arrived as `offlined`; a destroyed structure ends any open extraction as `offlined`.
  Restart: deadlines are durable; `EnsureScheduledAsync` re-arms imported open extractions at start-up. Tests: `MoonExtractionJobTests` (6).
- (d) `FractureChunkWithStructure` (`MoonExtractions.FractureAsync`): a ready chunk fires; the fracture completes 30 s later as `fractured`; the field decays
  after 48 h. A fire on a fracture in progress changes nothing; no chunk answers None; a chunk not ready is refused by name.
  `CancelExtraction` (`MoonExtractions.CancelAsync`): ends the chunk as `cancelled`, removes its jobs; Station Manager role (corprole 2048) required.
  All three mutations share the gate of `StructureMoonAuthority` (structure, docked, owner corporation, not Pochven, drill online).
  Tests: `MoonExtractionFractureTests` (6, Structures); `MoonExtractionStartHostTests` (Host, includes the fracture and cancel RPCs).
- Decisions: DEVIATIONS "Moon extraction (ST-6a)". Plan wording: the resource profiles are a second table (`structure_moon_resources`).

## Remaining gaps (ST-6a)
- Owner-corporation character notifications (EveJS `createNotification`: STARTED, FINISHED, LASER_FIRED, CANCELLED, AUTOMATIC_FRACTURE): no
  notification store in eve.rs yet.
- Rig modifiers: 2707 (fracture delay) and 2704 (yield) through the fitted rigs are not applied.
- The control gate (EveJS `isControllingStructureSession` and the profile access setting) waits for ST-3 and ST-5; until then the gate is docked
  at the structure and owned by the pilot's corporation.
- The moon binding is the seeded resource profile; EveJS's `moonMiningBindingRuntime` (bind on read) is not ported.
- The moon field's rocks and their space are 6b (not in this package).
- origin main's Host suite is red from a26283f (SpaceRpc composed before the simulation); it is filed as a suggested task.

## Gotchas
- `EVE_PG_URL` is key=value: `Host=127.0.0.1;Username=postgres;Database=eve`.
- Builds: `flock -o -w 1800 /tmp/eve-heavy.lock dotnet test <project> -p:UseSharedCompilation=false`.
- A `Task<JsonObject>` does not convert to `Task<JsonObject?>` (CS8619); wrap the read in a method with a nullable return type.
- The event bus delivers an event published from another subscriber on the next pump: tests tick twice after a reaction.
- `PyInt` is 32-bit on the wire: moon ids above int range are sent as `PyLong`.
