# ST-8 closure (ST-8a controlled-structure modules, ST-8b superweapons)

Owner: single worker (the user confirmed no other worker). Worktree `/home/user/wt-st8` (branch `st8/wt-st8`).
Database: `eve_p4` (Npgsql `Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p4`; Rust through `tools/dev/db-lock.sh`).
The handoff's `eve_rs_test` database does not exist here.

## Baseline (origin/main 581f82d, before any ST-8 change)

- `cargo test -p eve-space --lib`: 821 passed. `dotnet build dotnet/EveRs.slnx`: 0 warnings, 0 errors.
- `Eve.Structures.Tests`: 105 passed. `Eve.Services.Space.Tests` (Structure/Superweapon/Hog/Lightning/ModuleEffects/StructureAreaAuthorization): 105 passed. `Eve.Host.Tests` (Structure): 82 passed.
- No red state on origin/main at that baseline. The handoffs were partly stale: `TypeDef.Published` is already set from the SDE, and the Kind 85-89 wire, the Host candidate resolver and the Sim damage loop are on main.
- origin/main at the merge (3bc9546) was red in one place: the simlink fixture test had no mapping for the entosis fixtures and the message decoder rejected work command 14 (ST-9 integration). Both are fixed on this branch.

## Done

- Sim: `Action::StructureBurst { range, duration_ms, what }` (wire tag 32, protocol 71). Each cycle asks the Host per pulse (Kind 85/86); only granted ships are reached (`modules/burst.rs`). Families: web, paint, damp, disrupt, scramble (bubble), jam, neutralize.
- Sim: a controlled structure's guided bomb blast is authorized per detonation (`deployable.rs`: `BombImpact`, `await_bomb_impact`, `settle_bomb_impact`, `finish_bomb_impact`, 30 s wait then no effect). The blast of a structure is attributed to the structure through `damage_ship_from_structure` (`weapons/fire.rs`); the controller's retained hull is not a target of its own structure's blast.
- Host: `StructureBurstProjectorEffect` (category 66 only) for doomsdayAOE Bubble/Damp/ECM/Neut/Paint/Track/Web; `ProjectedEffect.StructureBurstWhat` reuses the projected numbers. Structure guided bomb launchers (35923, 47325) are no longer refused for their bombs (`SpaceSessions.Modules.cs`); Smartbomb and EcmBurst stay refused for structures. `useMissiles` is a per-pulse granted effect.
- Host fix: `FitsStructureDamageModule` refused every structure module with `disallowRepeatingActivation` when the client's repeat was above 1; the check is removed (the built effect carries the one-cycle budget).
- Standup HOG 47068 stays `MODULE_NOT_PUBLISHED`. Structure Vorton hops are authorized per hop (Host test).
- Gap manifest: `ModuleEffectsTests` scopes the structure-only burst names; ship-fitted variants stay listed. `ModuleRefusals` gives the new refusal reasons wording.
- Merge with origin/main (ST-9): ST-9 owns protocol 70 and action tag 31 (`Entosis`); this branch uses protocol 71 and tag 32 for the structure burst. Entosis fixture mapping and decoder command 14 added.

## Verification on the merged branch

- `cargo test -p eve-space`: 830 lib + integration suites all pass (simlink fixtures 8/8). `cargo test -p eve-net`: pass.
- `dotnet build dotnet/EveRs.slnx`: 0 warnings, 0 errors.
- `Eve.Space.Tests` (codec): 347 passed. `Eve.Services.Dogma.Tests`: 153 passed. `Eve.Services.Space.Tests` focused (burst, bomb, ModuleEffects, Entosis, area, data): 36 passed.
- Full `Eve.Services.Space.Tests`, `Eve.Structures.Tests` and Host Structure filter: see the final report line for this push.
- Environmental: PostgreSQL "too many clients" during parallel runs (restart raised the limit); those tests were rerun.

## Next (open, not verified)

1. Native client acceptance of structure bursts, bombs and Vorton (slim rendering, module HUD, FX) and a live Host-to-Sim walk.
2. Ship-fitted burst projectors (27678, 40635, 40696-40699, 40636, 41350) stay refused (manifest lists the names).
3. Standup energy neutralization burst 35944 is covered by the same handler; not tested individually.
4. Guided bomb: a launcher deactivated or a controller released before the blast leaves the blast without a grant (DEVIATIONS "Structure ST-8").
5. Structure bubble holds granted ships for the pulse (no bubble object) (DEVIATIONS).

## Files

- crates/eve-space/src/modules/{mod.rs,burst.rs,cycle.rs,effects.rs,tests.rs}; deployable.rs; scene/mod.rs; weapons/fire.rs; ewar.rs; link/{mod.rs,decode_action.rs,decode_message.rs,encode.rs}; sim/tests.rs; tests/simlink_fixtures.rs
- dotnet/src/Eve.Space/{SimMessages.cs,SimCodec.cs,SimCodec.Encode.cs,SimCodec.Decode.cs}
- dotnet/src/Eve.Services.Space/{SpaceSessions.Modules.cs,SpaceSessions.StructureDamageCandidates.cs,Effects/StructureBurstEffects.cs,Effects/ProjectedEffect.cs,Effects/ModuleEffects.cs}
- dotnet/src/Eve.Services.Dogma/ModuleRefusals.cs
- dotnet/tests/Eve.Services.Space.Tests/{StructureBurstAuthorizationTests.cs,ModuleEffectsTests.cs}; dotnet/tests/Eve.Space.Tests/SimCodecTests.cs
- tools/dev/gen-simlink-fixtures.py; oracle/fixtures/simlink/messages.json (regenerated: 199 valid, 82 invalid)
- docs: TODO.md (ST-8 section), DEVIATIONS.md (Structure ST-8), ADR 0013 (protocol 71 / tag 32 section)

## Gotchas

- Serena points at the main checkout: edit the worktree with `python3 -I` scripts.
- Bash `cat`/`grep` are denied; read through Serena or `python3 -I -c`.
- `dotnet test --no-build` needs a build of the same worktree first.
- Tethered ships are excluded from area effects (ST-4 rule); the bomb tests place the target accordingly.
