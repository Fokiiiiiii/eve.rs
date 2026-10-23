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
- Selected Actions run 81 (`39975686328`) passed the Host bound-call contract and pure launch-target checks with no failures or skips. Push CI run 631 (`39975569853`) on `c884523b` failed four Host composition tests (144 passed, 2 skipped): `CharacterServiceList` looked for a direct `InvBrokerService` after `InventoryRpc` had wrapped it in `IndustryInventoryChangePublisher`. The local correction now unwraps `InnerService` before constructing `RepackagingService`.
- The corrected `Eve.Host` build passed with a single MSBuild node, disabled build servers, and disabled built-in analyzers (0 warnings/errors). The composition regression awaits an Actions rerun for the fix included in this handoff's commit.
- The first parallel full build and first full dependency build ended with compiler exit 137. Reduced single-node project builds succeeded afterward. No graphical V24.01 client walk or live headless walk has been checked; the task forbids PostgreSQL use.
- The Host composition correction and merged main changes were pushed as `ca599ce5`. Selected Actions run 100 (`39989389824`) on this SHA and Windows CI run 225 (`39989390647`) failed before the composition regression could run: `ShipSkinrCatalog` queried the absent pinned-SDE table `sde.skinrComponentPointValues` during Host startup. The push has no `ci.yml` run recorded for this abbreviated SHA.
- To isolate the broker fix, selected Actions run 103 (`39990664003`) tested the previously failing `ClientStaticDataHostTests` on commit `a55b84e9`; it passed with no failed tests or skips. The test still needs a clean rerun on a current main head after the SKINR SDE startup issue is resolved.
- `origin/main` has since advanced to `951a8744`. `CharacterServiceList` now resolves the broker through `SingleRegisteredService<InvBrokerService>(services)`; the latest main-head test result was not available at this update. CI run 647 (`39991023879`) for the earlier `872df205` head was still in progress.

## Next work

1. Dispatch and inspect selected Host and pure launch target Actions checks; fix any failures.
2. Implement or wire an authoritative operation-site destination owner and verify operation spawnpoint/system data.
3. Add an authorized live instance lookup for `epinstance` and `externalDungeon`, including visibility/materialization and current-system checks.
4. Locate a maintained moon-mining point owner that supplies the beacon's real coordinates and system; do not derive it from an unsupported approximation.
5. Add provider tests for persisted owner resolution, expiry/staleness, cross-system refusal, retry safety, restart persistence, and no mutation on refusal.
6. Use the graphical client or an allowed live headless setup for each reachable UI path. Keep unreachable flows open and record verified results.
