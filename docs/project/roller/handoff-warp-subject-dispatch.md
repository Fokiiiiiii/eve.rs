# Warp subject dispatch handoff

## State

`beyonce.CmdWarpToStuff(subject, subjectID, **kwargs)` keeps the V24.01 positional and keyword contract. The Host dispatch test registers a contract service under the `beyonce` name, binds its returned object ID, and exercises success, refusal, retry, response types, kwargs, and response-before-notification ordering.

`launch` is wired through the existing planet manager. The persisted launch row is queried by session character and launch ID; only a non-deleted, unexpired row in the caller's current solar system with finite coordinates can reach the existing point-warp path. That path keeps `minRange`, movement persistence, and Destiny updates.

The subjects `operationDungeon`, `epinstance`, `externalDungeon`, and `moonMiningPoint` still fail closed. The current eve.rs tree has no authoritative live destination resolver for operation sites or moon mining points. Dungeon state exists, but no wired resolver was found that verifies instance ownership, live scene visibility/materialization, and the correct system. The client subject strings and IDs alone do not establish safe coordinates. Do not add numeric fallbacks or captured positions.

## Evidence and checks

- Client request: V24.01 `Michelle.CmdWarpToStuff(subject, subjectID, **kwargs)`; menu actions pass `operationDungeon` and `moonMiningPoint`; transmission, surroundings, objective, and journal paths pass `epinstance`, `externalDungeon`, and `launch`.
- Persistent launch owner: `Eve.Planets.PlanetStore.LaunchOfAsync`; PlanetMgr resolves by owner and current time; Host injects that resolver into Beyonce.
- The Space service regression covers absent and cross-system launch refusal, no movement on refusal, accepted point warp, and range forwarding. It uses an injected target callback and does not exercise the persistent resolver.
- The pure launch target test covers owner, expiry, system, ID, missing record, and finite-coordinate validation without a database.
- Local builds passed for `Eve.Services.Space`, `Eve.Services.Planets`, `Eve.Host`, `Eve.Host.Tests`, and `Eve.Services.Space.Tests`. They used a single MSBuild node, disabled build servers, and disabled built-in .NET analyzers to fit the shared machine; required source generation remained enabled. The Host bound-call test passed 1/1. The database-backed service regression was compiled and not executed under the task's PostgreSQL restriction.
- The first parallel full build and first full dependency build ended with compiler exit 137. Reduced single-node project builds succeeded afterward. No graphical V24.01 client walk or live headless walk has been checked; the task forbids PostgreSQL use.

## Next work

1. Run the pure launch target test and Host contract test through the selected GitHub Actions checks; fix any failures.
2. Implement or wire an authoritative operation-site destination owner and verify operation spawnpoint/system data.
3. Add an authorized live instance lookup for `epinstance` and `externalDungeon`, including visibility/materialization and current-system checks.
4. Locate a maintained moon-mining point owner that supplies the beacon's real coordinates and system; do not derive it from an unsupported approximation.
5. Add provider tests for persisted owner resolution, expiry/staleness, cross-system refusal, retry safety, restart persistence, and no mutation on refusal.
6. Use the graphical client or an allowed live headless setup for each reachable UI path. Keep unreachable flows open and record verified results.
