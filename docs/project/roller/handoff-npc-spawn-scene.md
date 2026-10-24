# NPC spawn scene handoff

## Change

The simulation now validates a spawn scene key and lazily creates a missing public solar-system scene from its authoritative universe data before accepting an NPC spawn. Unknown systems return the universe lookup error, invalid keys return `BAD_SCENE_KEY`, and missing private scenes still return `NO_SCENE`. The host continues to log a rejected spawn and records an explicit `SpawnFailed` belt result when the simulation accepts no ships; it only applies the belt respawn cooldown after at least one ship is accepted.

The existing simulation-link request/reply remains unchanged. `SimClient.SpawnAsync` awaits the reply correlated by request ID (`dotnet/src/Eve.Space/SimClient.cs:598-600`). `SpaceSessions.SpawnAsync` preserves the simulation refusal and logs it (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:118-135`); `SpawnGroupAsync` records each NPC only after receiving an accepted session (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:465-519`). Belt arrival now reports the empty-group refusal explicitly (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:528-564`).

## Behavior evidence

EveJS v0.12.9 resolves NPC groups through `spawnNativeNpcGroupInSystem` (`server/src/space/npc/nativeNpcService.js:2333-2360`); its context resolver ensures the public scene for a system spawn (`server/src/space/npc/nativeNpcService.js:857-942`). This change matches that observable behavior while keeping scene ownership in the simulation.

The client-visible destiny wire shape is unchanged. V24.01 `Michelle.DoDestinyUpdate` flushes received state to the ballpark (`eve/client/script/remote/michelle.py:482-512`). `Park.AddBalls2` decodes added ball data into slim items and queues them (`michelle.py:1256-1278`); `Park.ProcessBallAdd` registers each ball and emits `OnBallAdded` (`michelle.py:1208-1233`). Consumers found across eve-logic include the moon scanner (132-138), sensor suite (747-750), ship orbit camera (489-492), ego change smoother (31-34), structure proximity tracker (160-165), and objective/target consumers (`objectives/client/objective_task/container.py:180-184`, `destroy_counter.py:60-63`, `target.py:260-263`, plus entity-group/type destroy tasks at 131-134 and 180-183). No client call signature, notification, or payload changed.

## Regression coverage

`crates/eve-space/src/sim/tests.rs:1217-1260` requests an NPC in a visited system after its last player leaves and the scene is removed, then re-enters and asserts the NPC is in the player's visible scene. The same test checks explicit unknown-system, missing-private-scene, and invalid-key failures. `dotnet/tests/Eve.Services.Space.Tests/BeltRatsHostTests.cs:113-125` checks that a Host belt arrival reports a simulation refusal instead of claiming a spawn.

## Local checks

- `cargo +1.94.1 fmt --all -- --check`: passed.
- `cargo +1.94.1 check -p eve-space`: passed.
- Focused Rust simulation test: 1 passed.
- `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings`: passed.
- `dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- No database was used. The database-backed Host belt refusal test was not run locally.
- No graphical client was used.

## Actions

- Required agent-test dispatch on `2ec772626fd2`: run 303 passed for `Eve.Host.Tests,Eve.Npc.Tests`, Spawn filter; no test failures or skips were reported.
- Focused Host refusal dispatch: run 304 passed for `Eve.Services.Space.Tests`; no test failures or skips were reported.
- Push-triggered CI run 778 for `2ec772626fd2` completed with failures. The end-to-end live suite failed at `tests/test_structures.py:482`: the Astrahus anchor attempt was refused with “You must be in space to deploy a structure.” The `.NET (space tests 2/2)` shard was cancelled after 30.3 minutes. Rust tests, format/clippy, build, Windows, and the other .NET shards passed. Eight unrelated slow-live/skill-speed cases were skipped by their configured gates. These results do not indicate a failure in the NPC spawn regressions, which both selected agent-test runs passed; the remaining CI failures were not changed because they are outside this NPC scene fix.
