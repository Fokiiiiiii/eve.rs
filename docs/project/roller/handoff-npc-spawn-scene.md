# NPC spawn scene handoff

## Change

The simulation validates a spawn scene key and lazily creates a missing public solar-system scene from its authoritative universe data before accepting an NPC spawn. Unknown systems return the universe lookup error, invalid keys return `BAD_SCENE_KEY`, and missing private scenes still return `NO_SCENE`. The host continues to log a rejected spawn and records an explicit `SpawnFailed` belt result when the simulation accepts no ships; it only applies the belt respawn cooldown after at least one ship is accepted.

The existing simulation-link request/reply remains unchanged. `SimClient.SpawnAsync` awaits the reply correlated by request ID (`dotnet/src/Eve.Space/SimClient.cs:598-600`). `SpaceSessions.SpawnAsync` preserves the simulation refusal and logs it (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:118-135`); `SpawnGroupAsync` records each NPC only after receiving an accepted session (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:465-519`). Belt arrival reports an empty-group refusal explicitly (`dotnet/src/Eve.Services.Space/SpaceSessions.Npc.cs:528-564`).

## Behavior evidence

EveJS v0.12.9 resolves NPC groups through `spawnNativeNpcGroupInSystem` (`server/src/space/npc/nativeNpcService.js:2333-2360`); its context resolver ensures the public scene for a system spawn (`server/src/space/npc/nativeNpcService.js:857-942`). This matches that behavior while keeping scene ownership in the simulation.

The client-visible Destiny wire shape is unchanged. V24.01 `Michelle.DoDestinyUpdate` sends the update batch through the bound ballpark (`eve/client/script/remote/michelle.py:481-512`); `Park.AddBalls2` decodes each new slim item and queues its ball (`michelle.py:1255-1278`), while `Park.ProcessBallAdd` registers it and emits `OnBallAdded` (`michelle.py:1207-1233`). A whole-repository search of direct `OnBallAdded` consumers found the moon scanner (`eve/client/script/parklife/moonscan.py:131-138`), sensor suite (`sensorSuiteService.py:746-750`), ship orbit camera (`ui/camera/shipOrbitCamera.py:488-492`), ego change smoother (`ui/camera/util/egoChangeSmoother.py:30-34`), structure proximity tracker (`ui/services/structure/structureProximityTracker.py:159-165`), health widget (`progression/client/widgets/healthwidgets.py:90-99`), objective container (`objectives/client/objective_task/container.py:179-184`), destroy counter (`destroy_counter.py:59-63`), target task (`target.py:259-263`), and destroy entity-group/type tasks (`destroy_target.py:130-134, 179-183`). No client call signature, notification, or payload changed.

The simulation remains the authoritative owner of solar-system scenes: `Sim::spawn` builds a missing public scene from the Universe’s loaded system data before inserting the NPC (`crates/eve-space/src/sim/spawn.rs:809-845`); `Sim::forget` removes an empty public scene after the last player leaves (`sim/spawn.rs:783-795`), with NPCs alone not retaining it (`crates/eve-space/src/scene/mod.rs:953-963`). The regression then re-enters and binds the client, checking that the existing NPC is included in its Destiny update and visible in `ships_in_view` (`crates/eve-space/src/sim/tests.rs:1217-1260`).

## Regression coverage

`crates/eve-space/src/sim/tests.rs:1217-1260` covers the captured no-scene state after the last player leaves: the valid public solar-system scene is built from authoritative universe data, then the NPC remains visible when the player re-enters and binds. The same test checks unknown-system, missing-private-scene, and invalid-key failures. `dotnet/tests/Eve.Services.Space.Tests/BeltRatsHostTests.cs:113-125` checks that a Host belt arrival reports a simulation refusal instead of claiming a spawn.

## Local checks

- `cargo +1.94.1 fmt --all -- --check`: passed.
- `CARGO_BUILD_JOBS=2 cargo +1.94.1 check -p eve-space`: passed.
- Focused Rust simulation test: 1 passed, 892 filtered.
- `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings`: passed.
- `MSBUILDDISABLENODEREUSE=1 dotnet build dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj -p:UseSharedCompilation=false`: passed, 0 warnings/errors.
- The local checks used a temporary restore of the pre-existing deleted worktree files; their deletions were reapplied afterward. No PostgreSQL database was used. The database-backed Host refusal test ran on Actions instead.
- No graphical client was used.

## Actions

- Agent-test run `40072728395` on code commit `2ec772626fd27acfd8179b46d2ca9a8c1be62433` ran the focused Host belt-refusal filter in `Eve.Services.Space.Tests` and completed successfully; no failing tests or jobs were reported.
- Push-triggered CI run `40070216012` for the same code commit failed. The live suite failed at `tests/test_structures.py:482`: Astrahus anchoring was refused with “You must be in space to deploy a structure.” The `.NET (space tests 2/2)` shard was cancelled after 30.3 minutes. Rust tests, format/clippy, build, Windows, and other .NET shards passed. Eight slow-live/skill-speed tests were skipped by their configured gates. The focused NPC regression passed; the CI failures are outside this NPC scene change.
- Handoff evidence commit `ef56b04d` was pushed to `main` in merge `722828d549cd92bf722e959119e21dd3e6eb7a28`. Focused belt-refusal Actions run `40074207990` on that pushed commit completed successfully. `ci-status.py failures` found no push-triggered `ci.yml` run for that merge commit at the time of checking.
