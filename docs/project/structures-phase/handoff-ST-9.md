# ST-9 handoff: Entosis link and Sovereignty Hub capture

Worktree `/home/user/wt-st9` (branch `wt-st9` from origin/main). Database `eve_p2`, lock through `tools/dev/db-lock.sh`.
Sources: SDE build 3396210 (type 32458 Sovereignty Hub, group 1012; type 34593 Entosis Link I: attribute 54 maxRange 20000, 73 duration 300000, 713 consumptionType 16275, 6063 effect `entosisLink`); wiki "Sovereignty" (fetched 2026-10-15: the hub's base capture 595 s, ADM 1 to 6, one attacker link is enough, both links pause, defender alone regenerates over 50 min, 48 h reinforcement, command nodes); EVE.js `space/modules/entosisLinkRuntime.js`, `services/sovereignty/sovCaptureState.js` (neutral hub 595 s), `services/sovereignty/sovCampaignState.js` (owned hub, scoring rates, DEFENDER_REGEN_RATE), `services/sovereignty/sovState.js` (ADM tables).

## Increments
- (a) DONE: `SpaceSovHubs` (Eve.Services.Space) places a system's Sovereignty Hub in the sim on system entry, from the sovereignty read model; inert. Host wiring in `StructureComposition.cs`. Tests `SpaceSovHubsTests` (5, PG). See DEVIATIONS "Sovereignty Hub ball (ST-9 a)".
- (b) OPEN: `EntosisLinkEffect` body. Plan: SimAction tag 31 `Entosis { range_meters }` (one f64; protocol 69 -> 70 once); work command 14 (`WORK_ENTOSIS`) parks each cycle for the host; host refusals by name (`ENTOSIS_TARGET_NOT_SOV_HUB`, `ALLIANCE_MEMBERSHIP_REQUIRED`) through a SpaceSessions callback like the cyno jammer check; sim refusals TARGET_REQUIRED / TARGET_NOT_FOUND / TARGET_NOT_LOCKED / TARGET_OUT_OF_RANGE as Scan does.
- (c) OPEN: capture state per hub, durable (new migration in the structures schema), checkpointed progress, DurableScheduler completion job.
- (d) OPEN: completion. Neutral hub: claim through the sovereignty write path. Owned hub: reinforcement; command nodes are out of scope.
- (e) OPEN: tests through real registration and Sim; `ModuleEffectsTests` gap manifest.

## Gotchas
- Bash `cat`, `grep` and `find` are denied in this shell; read with `python3 -I` or the Read tool. Serena points at main, not this worktree.
- Build once, then `dotnet test --no-build --filter ...`.
