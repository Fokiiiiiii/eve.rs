# ST-23 handoff: control tower fuel and unanchoring; mission pockets and live flows open (2026-10-16)

Worktree `wt-st23` from origin/main b474dae2 (kept; its `dotnet` bin/obj were removed for disk space). Database `eve_p4`, live port 26103, `tools/dev/db-lock.sh`. Rules: `agent-rules.md`. Decisions: DEVIATIONS "Structure ST-23 (2026-10-16)". Rows: TODO section 61.

## 1. Control tower fuel and unanchoring (done, pushed: 046f8d43)

- Fuel per hour: `ControlTowerFuelRules` (Eve.Ships). SDE size attribute 1031, period 722 (one hour), race fuel block from SDE `raceID` (1 Nitrogen 4051, 2 Hydrogen 4246, 4 Helium 4247, 8 Oxygen 4312). The bay is the items at the tower's id (`CharacterShips.TakeTowerFuelAsync`, all or nothing).
- Durable job `space.control_tower.fuel_hour` (key `tower:{id}`, `SpaceContainers.ControlTowerFuelAsync`) runs in the job's transaction: pays the period, sets `deployed.tower_state` to `online` or `offline`, schedules the next period. First period at the anchoring time (scheduled by the launch).
- `posMgr.UnanchorStructure` (`PosMgrService` -> `SpaceSessions.ControlTowerUnanchor` -> `SpaceContainers.UnanchorControlTowerAsync`): corporation and system rules, must be anchored and offline, state `unanchoring`, job `space.control_tower.unanchor` after the SDE's 676 delay. `CompleteControlTowerUnanchorAsync` removes the tower from the simulation and the record, keeps the item, marks it with the unanchor-drop marker and places it as a wreck (`PlaceUnanchorDropAsync`). The scoop accepts that marker on Starbase items.
- Tests: `ControlTowerHostTests` 4/4 (the removal test now asserts the item is kept), `PosMgrServiceTests` 5/5. Wide runs: Space 928/928, Host 375/375, Core 738/738, Ships 229/229, Dogma 127/127, all 0 skipped; `cargo fmt --all -- --check` clean. No sim protocol change (protocol stays at 73).
- Migration `0016_control_tower_state.sql` (adds `deployed.tower_state`).

## 2. Mission pockets as simulation bodies (NOT started)

- Today: `DungeonWaveDirector.ActiveOriginsAsync` gives the mission pocket's origin (20 km ahead of the pilot) to the observatory deadspace check only. No sim message places a pocket. The sim has no deadspace kind (`SimCodec.Shared.cs` kinds, `SimMessages.cs`).
- Needed: a new sim ball (deadspace pocket, radius `DeadspaceSiteRadiusMeters` 1e9 m as in ST-16, or an SDE/EveJS value if one is found) with `SimPlaceDeadspacePocket` in `SimMessages.cs`, `SimCodec.cs`/`SimCodec.Encode.cs`, the Rust decode and dispatch in `crates/eve-space` (`link/`, `sim/dispatch.rs`), `PROTOCOL_VERSION` 74 (take the next free number at push time: `link/mod.rs`, `SimCodec.cs`, `gen-simlink-fixtures.py`, `sim/tests.rs`, `messages.json`), and a Host test that the pocket is placed on activation and removed at its end. A client check of how the pocket looks is needed; the wiki gives no pocket size.

## 3. Live flows (NOT done this session)

- Own-ship eject: `EjectFromStructure(shipId)` needs `owner.FromSpace`, `shipId == owner.OriginalShipId`, and `CanReturnToStructureShipAsync`: the ship's surface distance to the structure at most 6 500 m (V24.01 maxBoardingDistance). The live pilot undocks at Manifest, far from the refinery, so the test must bring the ship within that distance first (the warp rule of 1 000 km from structures applies to warp destinations; a warp to the item with a minimum range is not tried). Reference: EveJS `structureControlState.js` `ejectFromStructureControl` (restores the ship id and the system). Host-tested only.
- Moon fracture: `FractureChunkWithStructure` (`StructureMoonAuthority.FractureAsync`) after the chunk is ready (`chunkAvailableTime` of the extraction answer). The automatic fracture is a durable job (attribute 2698, default 10 800 s). A live test can set the due times through the database (`dbseed`) and then read the field row and the owner's mailbox row (`MoonOwnerNotifications`, type 206 for an automatic fracture). Not written.

## 4. Live run and disk (blocker)

- The default live list (`tools/dev/run-eve-live.sh`, no `EVE_RUN`) was NOT run in this session. The machine's disk was 99% full (453 MB free) during the session: the live run builds Host Release and the Rust release binaries. The `/tmp/claude-0/.../scratchpad/evejs-data*` folders (about 6 GB) are used by a running EveJS process (pids 7495 and others) and were left alone.
- Before the live run: free disk (about 4 GB for the release builds), then `EVE_PG_URL='postgres://postgres@127.0.0.1:5432/eve_p4' EVE_LIVE_PORT=26103 EVE_MARKET_SEED=skip tools/dev/db-lock.sh tools/dev/run-eve-live.sh`.
- `eve_p4` was created this session: `copy-sde-database.py` copies nothing when `eve` has no SDE, so it was loaded from the cached build with `eve-sde ensure --pg` (build 3396210 into `sde`, 3405148 into `sde_mission` by `--missions`). `eve` itself has no SDE schemas on this machine.

## Things that will bite

- `grep`, `cat`, `head` and `find` are denied in Bash here; `python3 -I` and the Serena reads work. `setsid`/background jobs started from a multi-command shell line can be killed when the line ends: write a script file and start it with `setsid nohup`.
- `dotnet build` of `EveRs.slnx` takes about 2 minutes warm; the Space test project takes about 17 to 20 minutes.
- The fuel job runs in the scheduler's transaction, the unanchor job does not (it calls the simulation, the marker and the space item store, each idempotent): a crash in the unanchor job is retried by the scheduler.
