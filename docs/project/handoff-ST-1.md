# ST-1 handoff (structure ball in the simulation, protocol 59)

Owner: the structures phase (plan in the scratchpad `specs/structures_phase.md`, section ST-1). Branch `wt-st1`, worktree `/home/user/wt-st1`.

## Increments c and d (in progress, see TODO section 46)
- (c) `scene/docking.rs`: docks and undocks take a structure; refusals `STRUCTURE_DISABLED` and `SHIP_TOO_LARGE` (DEVIATIONS "Structure docking (ST-1 c)").
- (d) `dotnet/src/Eve.Services.Space/SpaceStructures.cs` with the composition wiring in `Eve.Host/Composition/StructureComposition.cs`; `Eve.Services.Space` now references `Eve.Structures`. Tests: `dotnet/tests/Eve.Services.Space.Tests/SpaceStructuresTests.cs` (3 PG tests); `Rig` gained a `StructureSchema`.
- (e) `tools/headless-client/tests/test_structures.py` is written. It is skipped unless the live environment is set (`EVE_LIVE_HOST`, `EVE_LIVE_STRUCTURE_ID`, `EVE_LIVE_STRUCTURE_SYSTEM`, `EVE_LIVE_CHARACTER`, `EVE_LIVE_USER`). Collected and skipped with the headless venv (`/home/user/eve.rs/tools/headless-client/.venv`). It has NOT run against a live server: the character-select step is the assumed client call (`charMgr.SelectCharacterID`), and the ballpark parsing reuses `tests/test_undock` helpers. Verify it with the live stack before trusting it.
- Open: structure placement on anchoring (ST-2); the live run of (e); the slim `repairing` and `unanchoring` fields.

## Pushed to main
- `8eaccdb` ST-1 (a): sim structure body, commands 74-76, protocol 59.
- `6186267` ST-1 (b): structure health, per-second cap, `StructureDamaged` (77), protocol 60.
- Open: (c) dockable body in `scene/docking.rs`, (d) host `SpaceStructures.cs` and the vitals/limits source, (e) headless test. Nothing below is started.

## Done (increments a and b)
- Increment b: `crates/eve-space/src/health/structure.rs` (`StructureHealth`: the ship's layers with a per-second limit per layer and the repair-pause report); `Scene::damage_structure`; `Out::StructureDamaged` -> kind 77; `PlaceStructure` carries the vitals, recharge and limits. Protocol is now 60 (59 was pushed with increment a; see ADR 0013).
- Tests for b: `health::structure::tests` (2), `scene::tests::a_hit_on_a_structure_is_cut...` (1), the fixtures for `PlaceStructure` and `StructureDamaged` in Rust and C#.
- Simulation: `BodyKind::Structure` and `Detail::Structure` (`crates/eve-space/src/world.rs`); `StructureStatus` carries the host's slim fields.
- Wire: kinds 74 `PlaceStructure`, 75 `RemoveStructure`, 76 `SetStructureState`; `PROTOCOL_VERSION` 58 -> 59 (Rust, `SimCodec.ProtocolVersion`, ADR 0013 addendum).
- Scene: `crates/eve-space/src/scene/structures.rs` places, removes and re-states a structure body and tells the bound pilots (`AddBalls2`, `RemoveBalls`, `OnSlimItemChange`). A placed structure is in every later `SetState`.
- Slim item: `slim::structure_fields` (corpID, allianceID, state, upkeepState, timer, unanchoring, repairing; categoryID 65).
- Fixtures: three cases in `tools/dev/gen-simlink-fixtures.py`, regenerated `oracle/fixtures/simlink/messages.json` (176 valid); the Rust and C# fixture tests decode and re-encode them.

## Tests actually run (increment a)
- `cargo test -p eve-space`: all suites pass (711 unit tests, then the integration suites, including `simlink_fixtures`).
- `cargo test -p eve-sim`: pass.
- `cargo check --workspace --all-targets`: clean.
- `dotnet build dotnet/EveRs.slnx`: 0 warnings, 0 errors.
- `dotnet test dotnet/tests/Eve.Space.Tests`: 276 pass.
- `EVE_PG_URL='Host=127.0.0.1;Username=postgres;Database=eve' dotnet test dotnet/tests/Eve.Services.Space.Tests`: 648 pass, 0 skipped.
- New scene tests in `scene/tests.rs`: a structure is a body in the bind; placed while a pilot is in the system it is told; its state change is told once; removal is told once.

## Next steps (c, d, e are open; the order is c, then d, then e)
0. (c) and (d) are the next work; the TODO rows 46 are the status.
1. The host supplies the vitals (SDE 263/265/9 layers, 2034-2036 limits, 3354-3356 pauses) in `PlaceStructure`; nothing does yet. A weapon path that hits a structure body is not wired (ST-8a).
2. (c) Dockable body in `scene/docking.rs` (`Station` -> dockable body; state gate from J `STRUCTURE_DISABLED_STATES`).
3. (d) Host `dotnet/src/Eve.Services.Space/SpaceStructures.cs`: restore on first pilot entry, `structure.state_changed` -> `SetStructureState`, `StructureDamaged` -> ST-0 machine.
4. (e) Headless test `tools/headless-client/tests/test_structures.py` (MITM rows stay UNVERIFIED until the graphical client is used).

## Gotchas
- The Serena MCP view did not match this worktree: edits made through it did not reach `/home/user/wt-st1` (git stayed clean). Increment a was applied with python3 scripts from Bash, as AGENT_RULES.md prescribes. Check `git status` after any edit.
- Bash `grep` is denied in this repository; use `tail`/`head` on command output, or python3 for searches.
- `EVE_PG_URL` must be the key=value form (`Host=127.0.0.1;Username=postgres;Database=eve`). `Eve.Services.Space.Tests` takes about 8 minutes; run it in the background.
- The heavy lock: `flock -o -w 1800 /tmp/eve-heavy.lock <cmd>`, with `-p:UseSharedCompilation=false` on dotnet build and test, and `CARGO_TARGET_DIR=/home/user/eve.rs/target`.
- The structure ball is `Kind::Station` and is not verified on the client (DEVIATIONS "Structure ball (ST-1)").
- Protocol 59 is on main with the increment-a layout; the health layout is protocol 60. Do not reuse 59 for the health.

## Files
- `crates/eve-space/src/{world.rs, scene/structures.rs, scene/mod.rs, scene/tests.rs, sim/dispatch.rs, payload.rs, slim.rs, warp.rs}`, `link/{kind.rs, mod.rs, decode_message.rs, payload.rs, encode.rs}`, `tests/simlink_fixtures.rs`
- `dotnet/src/Eve.Space/{SimCodec.cs, SimCodec.Decode.cs, SimCodec.Encode.cs, SimCodec.Shared.cs, SimMessages.cs, ISimulation.cs, SimClient.cs}`, `dotnet/tests/Eve.Space.Tests/SimCodecTests.cs`, `dotnet/tests/Eve.Services.Space.Tests/FakeSim.cs`
- `tools/dev/gen-simlink-fixtures.py`, `oracle/fixtures/simlink/messages.json`
- `docs/design/adr/0013-module-work-cycle-wire.md` (addendum), `docs/reference/compat/DEVIATIONS.md` ("Structure ball (ST-1)"), `docs/project/TODO.md` (section 46)
