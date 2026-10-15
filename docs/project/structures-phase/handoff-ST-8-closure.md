# ST-8 closure (ST-8a controlled-structure modules, ST-8b superweapons)

Owner: single worker (the user confirmed no other worker). Worktree `/home/user/wt-st8` (branch `st8/wt-st8`, from origin/main 581f82d).
Database: `eve_p4` (`EVE_PG_URL` Npgsql form `Host=127.0.0.1;Port=5432;Username=postgres;Database=eve_p4`; Rust via `tools/dev/db-lock.sh`).
The handoff's `eve_rs_test` database does not exist; the suites run on the `eve*` databases that hold the `sde` schema.

## Baseline (origin/main 581f82d, before any ST-8 change)

- `cargo test -p eve-space --lib`: 821 passed, 0 failed.
- `dotnet build dotnet/EveRs.slnx`: 0 warnings, 0 errors.
- `Eve.Structures.Tests`: 105 passed. `Eve.Services.Space.Tests` filter Structure/Superweapon/Hog/Lightning/ModuleEffects/StructureAreaAuthorization: 105 passed. `Eve.Host.Tests` filter Structure: 82 passed.
- No red state was found on origin/main. The handoffs were partly stale: `TypeDef.Published` is already set from the SDE (DogmaStatic), and the Kind 85-89 wire, the Host candidate resolver and the Sim damage loop are already on main.

## Done in this push

- Sim (Rust): `Action::StructureBurst { range, duration_ms, what: Remote }` (wire tag 31, protocol 70). Each cycle asks the Host, per pulse, for the ships in reach (Kind 85/86, `PendingStructureDamageOperation::StructureBurst`); only granted ships are reached (`modules/burst.rs`). Families: web, paint, damp, weapon disruption, warp disruption (scramble), ECM jam (rolls only for granted ships), energy neutralization.
- Host (C#): `StructureBurstProjectorEffect` (category 66 structure modules only): doomsdayAOE Bubble/Damp/ECM/Neut/Paint/Track/Web. Published types are accepted whatever the effect's publication. `ProjectedEffect.StructureBurstWhat` reuses the projected numbers of each family.
- Host fix: `FitsStructureDamageModule` refused every structure module with `disallowRepeatingActivation` whenever the client asked for a repeat above 1 (the RunningModule records the client repeat; the built effect has one cycle). The check is removed; the one-cycle budget is enforced by `ShipEffect.Build`.
- Gap manifest: `ModuleEffectsTests` scopes the structure-only burst names; the ship-fitted variants stay listed as gaps. `ModuleRefusals`: `MODULE_NOT_PUBLISHED`, `REFUSED`, `UNSUPPORTED_MODULE` now read as "cannot be activated".
- Standup HOG 47068 stays `MODULE_NOT_PUBLISHED` (test: `The_standup_hog_is_refused_as_unpublished_and_nothing_reaches_the_simulation`).
- Tests: Sim `a_controlled_structure_burst_web_reaches_only_the_ships_the_host_authorizes_at_each_pulse`, `a_controlled_structure_burst_jam_rolls_only_for_the_ships_the_host_authorizes`; Host `StructureBurstAuthorizationTests` (burst sent as `StructureBurst`, per-pulse grants by owner-corp facts, HOG refusal, Vorton hop grants); simlink fixture `activate a structure ecm burst projector` (197 valid frames).

## Next (not done)

1. Guided bombs (group 1328, `BombEffect`): the Sim detonates synchronously in `advance_deployables`; impact authorization needs the detonation to park until the Host answers (new Rust deployable state, correlation of the launching cycle, Host refusal of structure `Bomb` packages removed). Until then they stay refused.
2. Standup energy neutralization burst 35944 and the other structure-only families are covered by the burst handler; verify 35944 on the structure path in a test.
3. DEVIATIONS: structure bubble holds the granted ships for the pulse (no static bubble object); record it.
4. TODO.md ST-8 rows, DEVIATIONS, full .NET suites (Services.Space, Host, Structures, Space codec) and the full Rust workspace before the final push.

## Files (this push)

- crates/eve-space/src/modules/{mod.rs,burst.rs,cycle.rs,effects.rs,tests.rs}, ewar.rs (rolls_under pub(crate))
- crates/eve-space/src/link/{mod.rs,decode_action.rs,encode.rs}, sim/tests.rs, tests/simlink_fixtures.rs
- dotnet/src/Eve.Space/{SimMessages.cs,SimCodec.cs,SimCodec.Encode.cs}
- dotnet/src/Eve.Services.Space/{SpaceSessions.StructureDamageCandidates.cs,Effects/StructureBurstEffects.cs,Effects/ProjectedEffect.cs,Effects/ModuleEffects.cs}
- dotnet/src/Eve.Services.Dogma/ModuleRefusals.cs
- dotnet/tests/Eve.Services.Space.Tests/{StructureBurstAuthorizationTests.cs,ModuleEffectsTests.cs}
- tools/dev/gen-simlink-fixtures.py, oracle/fixtures/simlink/messages.json (regenerated)

## Gotchas

- Serena points at the main checkout: edit wt-st8 with `python3 -I` scripts; Serena reads are fine only for unchanged files.
- Bash `cat`/`grep` are denied; use Serena or `python3 -I -c` for reads.
- `dotnet test --no-build` needs a prior build of the same worktree.
- The Host's `StartStructureDamageModuleForTest` seeds a running module without a Sim round trip (tests only).
