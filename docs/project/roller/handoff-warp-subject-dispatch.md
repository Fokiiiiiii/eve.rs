# Warp subject dispatch handoff

## State

`beyonce.CmdWarpToStuff(subject, subjectID, **kwargs)` preserves the V24.01 request shape. The Host contract test calls a bound `beyonce` object and checks both positional arguments, keyword values, response marshalling, refusal, retry, and response-before-Destiny-notification order.

`launch` resolves through the existing PlanetStore owner. It only accepts a caller-owned, non-deleted, unexpired launch record in the current solar system with finite persisted coordinates, then uses the existing point-warp path with the requested range. This subject has regression coverage, but no real client walk has been recorded.

`operationDungeon`, `epinstance`, `externalDungeon`, and `moonMiningPoint` remain refused. Do not report these UI paths as supported or supply guessed/captured coordinates.

## Provider findings

- Operation sites: `KeeperService.GetOperationSpawnpointSolarSystem` and `IsOperationSite` have optional callbacks in `dotnet/src/Eve.Services.Core/UnportedFeatureRpcServices.cs`; absent callbacks return `None` and `false`. No operation spawnpoint owner is wired in Host. EveJS v0.12.9 resolves a per-character or shared site spawnpoint record from `server/src/services/dungeon/operationSiteRuntime.js:95-106`, then checks the current system in `server/src/services/ship/beyonceService.js:1750-1778`.
- Dungeon instances: `PersistenceDungeonReadModel` reads active anomaly/signature content and `dungeonRuntimeState`; `DungeonInstanceCacheMgrService` can expose a persisted position. This read side does not materialize a visible scene target. EveJS requires a seeded active instance in the current system, materializes its contents, and resolves the resulting scene entity (`server/src/services/ship/beyonceService.js:1847-1918`). A position alone does not establish that the target can be seen after warp.
- Moon mining: no moon-mining-point coordinate authority exists in the current eve.rs sources. The moon extraction owner stores extraction/resource state, not mining-point coordinates. EveJS validates packaged point data against the moon and system (`server/src/services/structure/moonMiningPointAuthority.js:73-151`); do not substitute the moon's SDE position or an estimated offset.
- Planet launch: `Eve.Planets.PlanetStore.LaunchOfAsync` owns persistent launch rows. Host resolves those rows by character and time through PlanetMgr.

## Checks

- Local builds passed for `Eve.Services.Space`, `Eve.Services.Planets`, `Eve.Host`, `Eve.Host.Tests`, and `Eve.Services.Space.Tests`. They used a single MSBuild node, disabled build servers, and disabled built-in .NET analyzers; source generation remained enabled. Earlier parallel/full dependency builds exited 137; reduced project builds passed.
- Host bound-call contract selected Actions run 81 (`39975686328`): passed, no failures or skips.
- Focused launch service Actions run 109 (`39993846661`), commit `ca599ce5`: 1 passed, 0 failed, 0 skipped. The service test uses an injected resolver; it does not exercise PlanetStore persistence. The pure launch target test covers missing/stale/foreign-owner/cross-system/non-finite targets.
- Isolated Host composition regression Actions run 103 (`39990664003`), commit `a55b84e9`: passed, no failures or skips.
- Runs 100 (`39989389824`) and 225 (`39989390647`), and push CI runs 647 (`39991023879`) and 648 (`39992377417`), failed during Host startup because `ShipSkinrCatalog` queried missing pinned-SDE table `sde.skinrComponentPointValues`. Run 648's .NET build passed. This issue belongs to the unrelated SKINR feature and is recorded as open.
- No PostgreSQL database was used locally. No graphical V24.01 client walk or live headless client walk was performed. The UI transitions and next client actions are still open for all five subjects.

## Next work

1. Wire operation-site resolution to its authoritative spawnpoint owner and prove owner/system/expiry checks without adding a parallel store.
2. Connect dungeon warps to an authoritative active-instance provider that verifies visibility/materialization as well as system and lifecycle state.
3. Add the moon-mining-point authority from pinned SDE or another verified owner; cover missing and cross-system points.
4. Add success, refusal, retry, persistence/restart, exact response-shape, and notification-order tests for each supported subject. Keep unsupported subjects refused until their provider and tests exist.
5. Perform the reachable operation-site, in-space site, moon-mining, and planetary launch headless walks. Record each real state transition and subsequent client action; keep unreachable flows open.
