# Space / World gaps implementation record

確認日: 2026-11-17
対象: V24.01 client build 3396210 (`/home/user/eve-logic`), EveJS 0.12.9 (`/home/user/evejs-261108.1`), eve.rs branch `cx3`.

## 確認した仕様と実装

### SW-04: deployment dynamic blockers

**V24.01 client.** `eve/client/script/ui/structure/deployment/deploymentEntity.py:50-64` creates the preview point from the ship ball and asks `GetDeploymentConflictForBalls` to evaluate `ballpark.GetBallsAndItems()`. The deployment blocker includes live ballpark state. The same client builds warp-in blockers in `eve/client/script/remote/michelle.py:1418-1427`; `ballparkCommon/parkhelpers/__init__.py:14-17` selects celestial-category balls of radius at least 90,000 m except wrecks. This includes eligible asteroid belts. Normal warp targeting has a separate exclusion, so the server now has a dedicated deployment warp-point predicate.

**EveJS.** `server/src/services/structure/structureDeploymentPlacement.js:250-300` gathers scene static and dynamic entities and appends client-parity warp-in points; `:417-423` uses that list when a scene exists. I directly invoked `resolveDeploymentBalls` with a synthetic dynamic scene entity. It returned that live entity. This isolated Node process had no loaded itemTypes table, so it did not verify the warp-in point output.

**eve.rs.** Added SimLink query `GetDeploymentBalls` (request kind 166, reply kind 167; protocol 101). Rust `Scene::deployment_balls` settles resident pilots and emits live system bodies, ships, wrecks, live asteroids, Abyssal traces, deployables and missiles, plus deployment-specific client warp-in points. An unloaded scene returns an error. The Host composition reads this query for deployment validation; `StructureAnchorAuthority` merges fresh scene balls with static and persisted candidates, replaces stale real-ball duplicates by item ID, and checks blockers with their radii. Query failure is returned as a failed placement instead of silently using an incomplete scene.

**Regression coverage.** Rust test `deployment_ball_query_returns_live_balls_and_client_warp_in_points` checks dynamic scene balls, warp-in points and the eligible large-belt case. `DeploymentBallsCodecTests` checks the .NET SimLink wire shape. Registered Host test `Structure_anchor_rpc_rejects_a_location_inside_the_client_moon_clearance_and_preserves_the_item` checks static moon/gate/belt cases and a blocker that exists only in the live scene.

### SW-05: FW player kills

`FactionWarfareKillProducer` subscribes to the durable `ShipKillmailEvent` topic from the registered Host composition. The destruction event snapshots the victim's effective war faction at destruction; each attacking pilot's effective war faction and corporation are captured from the session when the damage ledger records that pilot. Legacy events without snapshots fall back to persisted direct/corporation/alliance state, honoring the recorded corporation/alliance downtime boundary. The producer identifies the final-blow pilot by killer ship ID and last-hit order, then counts only a hostile player kill in a mapped warzone. It writes pilot and event-time corporation daily kill rows in the event transaction. Friendly, non-warzone, NPC final-blow, self-kill, missing victim, and missing final-blow cases do not increment the rows. The registered Host PostgreSQL regression changes both characters' affiliations and moves the attacker to another corporation after durable publication but before delivery, confirms the event-time hostility and corporation still count once, and reopens the Host to verify its committed outbox cursor is not replayed.

V24 reads the daily totals through `facWarSvc.py` (`GetStats_Character` and `GetStats_Corp`, lines 340-363). The observed V24 victory-point UI reads/publishes VP state and vulnerability events (`fwVictoryPointSvc.py:31-69`). The player-kill producer records kill statistics only; it does not award system VP. EveJS `fwVictoryPointMgrService.js:92-143` and `fwQueryPayloads.js:13-51` cover VP state read/publish; no corresponding kill producer was found in the inspected EveJS runtime. The ship-kill LP award is still open.

### Control Bunker: lifecycle partial, placement open

V24's Agency callsite (`eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-60`) asks `GetControlBunkerIDInSystem`, then reads the returned inventory item and ball. It does not describe how the ball is positioned. The matching SDE has the unpublished group 925 `FW Infrastructure Hub`, faction-specific types 29286 (Amarr), 29302 (Caldari), 29303 (Gallente), and 29304 (Minmatar), and dogma attributes used for faction and hull state; it has no per-system bunker instances or position table. V24's reserved item range is `inventorycommon/const.py:71-72`. CCP's [Empyrean Age announcement](https://community.eveonline.com/news/news-channels/press-releases/factional-warfare-erupts-in-eve-online-with-launch-of-empyrean-age-1/) confirms the System Control Bunker role but gives no placement coordinates. EveJS's inspected faction service publishes victory state, but provides no Control Bunker spawn/capture runtime.

The Host now persists a stable ID and pending-capture state, handles `SimStructureDamaged` hull depletion for a known group-925 bunker, schedules the next downtime, applies the occupier/VP/LP reset transaction, and publishes a post-commit capture event. Complex and battlefield results update a live bunker state and send the V24 vulnerability notices. `GetControlBunkerIDInSystem` returns a `PyLong` only when the stored item ID resolves to an actual live group-925 scene ball.

**Production placement remains deliberately fail-closed.** Neither the V24 callsites, the matching SDE data inspected here, nor the EveJS runtime provided a verified initial Control Bunker position. `GetSunWarpInPoint` is a client warp-target calculation and is not evidence that a Control Bunker belongs there. The production Host therefore has no `readVerifiedPosition` provider and does not synthesize a bunker ball. The Host lifecycle regression supplies a test-only explicit anchor to verify SDE type/health, damage, downtime capture, reset, and replacement; it separately verifies that no provider means no placement and no Agency ID. This does not verify normal gameplay spawning, capture re-spawn, or Agency presentation.

### Ansiblex travel

The V24 menu invokes `CmdJumpThroughStructureStargate` at `eve/client/script/ui/services/menusvc.py:2095-2109`; autopilot invokes it at `eve/client/script/parklife/autopilot.py:348-352` and stops when the RPC does not report a jump. EveJS `structureJumpBridgeMgrService.js:1713-1884` validates the linked services, access, local and light-year range, disruption, mass, fuel and fee before settlement and session transition.

The production `StructureRpc` registration supplies `StructureJumpBridgeTravel.TravelAsync` and persisted destination/link callbacks. `StructureJumpBridgeTravelHostWireTests` exercises registered Host movement, PostgreSQL structure/fuel/wallet state, notification order, refusal cases, retry replay, duplicate suppression and `SimJumped` persistence. Ansiblex travel was already present in this worktree; this job corrected stale audit/TODO records. Anchoring/setup, initial scene alignment and a graphical/headless travel walk remain open. No matching headless Ansiblex walk was found under `tools/headless-client/tests`.

## 実行した検証

- `cargo fmt --all` and `cargo fmt --all -- --check` completed successfully. `cargo check -p eve-space` and `cargo clippy -p eve-space -- -D warnings` passed. Focused Rust tests: deployment-ball query and Control Bunker vulnerable-hull damage, 2 passed.
- Release .NET builds with `-warnaserror`: `Eve.Space`, `Eve.FactionWarfare`, `Eve.Services.Core`, `Eve.Services.Space`, `Eve.Host`, and `Eve.Space.Tests`, `Eve.FactionWarfare.Tests`, `Eve.Services.Core.Tests`, `Eve.Services.Space.Tests`, `Eve.Host.Tests`; all completed with 0 warnings and 0 errors. Test project assets were missing initially for `Eve.Services.Space.Tests`, so that project was restored before building. Host startup needed the official mission/SKINR schema; `eve-sde ensure --missions --keep-archive` loaded pinned mission build 3405148 into `sde_mission` on `eve_p3` (the gameplay SDE remains build 3396210).
- `DeploymentBallsCodecTests`: 1 passed, 0 skipped.
- Filtered `Eve.FactionWarfare.Tests` classes (database, capture, complex, battlefield, stats, kill, FacWarMgr and LP tax): 98 passed, 0 skipped on `eve_p3`.
- `Eve.Services.Space.Tests`, `SpaceEventsTests`: 17 passed, 0 skipped on `eve_p3`; includes serialized damage-time attacker corporation/war-faction and victim war-faction snapshots.
- Focused `Eve.Host.Tests` source consumers: 6 passed, 0 skipped on `eve_p3`: direct producer and Control Bunker lifecycle tests (2), registered Host-composition kill producer (1), and the three `CharacterHostTests` methods that directly publish/read `ShipKillmailEvent` (including two real-simulation cases). The Host-composition case mutates affiliation and corporation after event publication and checks restart/no replay.
- The earlier broader filter over all `CharacterHostTests` was interrupted before completion because it included unrelated simulation-gated and general Host cases; none of its results count toward the focused test totals. The source-referencing cases above were then run directly with `target/debug/eve-sim` and the pinned 3396210 SDE, 0 skips.

## 未確認

- No graphical V24.01 client walk was run. Host wire tests and a synthetic EveJS helper invocation are not graphical-client verification.
- The production Control Bunker position and normal scene-entry spawn/re-spawn are unresolved, so the Control Bunker feature is not complete.
- No Ansiblex graphical/headless walk or anchoring/setup flow was run.
- The requested branch CI (`jobs all`), origin/main merge, and push are not recorded as complete until their result is observed.
- The EveJS helper invocation ran without static itemTypes loaded; only the dynamic scene-ball result was observed in that run.
