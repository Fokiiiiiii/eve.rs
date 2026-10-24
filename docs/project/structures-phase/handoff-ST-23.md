# ST-23 handoff: control tower fuel, dungeon pockets, the return to the ship, the moon live check (2026-10-16)

Worktree `wt-st23` (removed at the end of the session). Database `eve_p4` (live port 26103, `tools/dev/db-lock.sh`). Rules: `agent-rules.md`. Decisions: DEVIATIONS "Structure ST-23" (two sections). Rows: TODO section 61. Commits on main: `046f8d43` and `305afe0c` (tower), `b3871234` (pockets, protocol 74), `52d7377d` (return and live), then the docs commit.

## 1. Control tower fuel and unanchoring (done)

- Fuel per hour: `ControlTowerFuelRules` (Eve.Ships): SDE size (attribute 1031), 10 x 2^(size-1) blocks per period, race fuel block (SDE raceID: 1 Nitrogen 4051, 2 Hydrogen 4246, 4 Helium 4247, 8 Oxygen 4312). The bay is the items at the tower's id (`CharacterShips.TakeTowerFuelAsync`, all or nothing).
- Durable job `space.control_tower.fuel_hour` (`SpaceContainers.ControlTowerFuelAsync`) pays the period in the job's transaction, sets `deployed.tower_state` to `online` or `offline` (migration 0016), and schedules the next period (SDE 722, one hour). The first period is at anchoring.
- `posMgr.UnanchorStructure` (`PosMgrService` -> `SpaceSessions.ControlTowerUnanchor` -> `SpaceContainers.UnanchorControlTowerAsync`): corporation and system rules, anchored and offline, state `unanchoring`, job `space.control_tower.unanchor` after the SDE's 676 delay. `CompleteControlTowerUnanchorAsync` takes the tower out of the simulation and forgets the record, and keeps the item: marked with the unanchor-drop marker and placed as a wreck (`PlaceUnanchorDropAsync`). The scoop accepts that marker on Starbase items.
- Tests: `ControlTowerHostTests` 4/4, `PosMgrServiceTests` 5/5.

## 2. Dungeon deadspace pockets in the scene (done, protocol 74)

- Sim: `PlaceDeadspacePocket`, kind 93 (`PLACE_DEADSPACE_POCKET`), a disruptor's placement with no type, group, range or owner. The pocket is in the scene at the dungeon's origin with radius 1e9 m (EveJS `DEADSPACE_SITE_RADIUS_METERS`). No client ball, no scan (`scan_targets` lists the roles it scans), it stops no field. Rust: `Role::DeadspacePocket`, `Scene::place_deadspace_pocket`; C#: `SimPlaceDeadspacePocket`, codec, `ISimulation`, fakes. `PROTOCOL_VERSION` 74 (Rust `link/mod.rs`, `SimCodec.ProtocolVersion`, `gen-simlink-fixtures.py` KINDS 93, `messages.json` +22 lines, `sim/tests.rs` pin).
- Host: `DungeonWaveDirector.ActivePocketsAsync` (replaces `ActiveOriginsAsync`); `SpaceContainers.Pockets.cs`: record kind `dungeon_pocket` (migration 0017; id = `PocketIdBase` + instance id), `PlaceDeadspacePocketAsync`, `ReconcileDeadspacePocketsAsync`, job `space.deadspace.pockets` every 10 s (placed while its dungeon runs, taken out when it does not). The observatory deadspace check reads the pocket record; `RestoreDeployedAsync` puts the body back with the system. The cosmic sites remain `ReadDeadspaceAnchors`.
- Open: the EveJS pocket warp rules (A: no warp between rooms; B: a warp into a gated pocket is redirected) are not in the simulation. The reconcile delays a new pocket by up to ten seconds for the observatory check.
- Tests: `MobileObservatoryHostTests` (11/11 incl. `A_dungeon_pocket_is_placed_at_its_origin...`), `DungeonWaveTests`, Rust `deployable::tests::a_deadspace_pocket...`, simlink fixtures, codec 353/353.

## 3. Live flows (done: eject to own ship; moon fracture, completion, owners' rows)

- Eject back to the own ship. Server: a boarding from space records the ship's place while its pilot still pilots it (`SpaceSessions.RecordBoardingPlaceAsync`, called from `StructureControlBackend`), and the return check reads the simulation's answer or, when the simulation answers the pilot only, the saved place (`ScenePositionOfAsync`). Host tests: `StructureControlBackendTests` (2 new). Live: `test_a_pilot_in_space_boards_the_structure_and_ejects_back_into_its_own_ship`: the pilot is seeded 48 km from a second refinery (`RETURN_STRUCTURE`, 200 000 km from Manifest's station: the station's ball pushes a ship out inside ~70 km of it, so a refinery 5 km from the station cannot be reached by a ship), `OnShipCosmeticsChanged` carries the ship's id.
- Moon: `test_a_moon_chunk_is_refused_until_ready_then_fractured_and_completed_and_its_owners_are_told`: refused by name before ready; fired (`fracturing`); the owners' laser-fired row (type 205, `notifications` game store) is written after the fire; completed as `fractured` 30 s later; the ore field is in the system (space items with the extraction id). The live seed had test-data errors, not server bugs: the ore shares were Tritanium and Kernite (not moon ores: the fracture validates them) and the moon 40000001 is not an SDE moon. The seed now uses Zeolites and Cobaltite and moon 40491234.
- The server's moon notices and fracture follow EveJS already (`moonExtractionsService.js`: 202 start, 203 cancel, 204 chunk ready, 205 laser fired, 206 automatic fracture on decay; EveJS writes no notice for a fractured completion). No moon server change was needed.

## 4. Verification (eve_p4)

- Default live list (`tools/dev/run-eve-live.sh`, no `EVE_RUN`, `EVE_MARKET_SEED=skip`): exit 0; every non-slow test passed. Skipped: the slow-mode tests (`EVE_LIVE_SLOW` unset) and the skill-speed test, as in ST-21. `test_structures.py` includes the two new scenarios.
- Wide .NET (Debug, `dotnet test --no-build`, 0 failed, 0 skipped): Space 930, Host 377, Core 738, Ships 229, Eve.Space (codec) 353, Dogma 127.
- Rust: `cargo test -p eve-space -p eve-net` 898 passed (after the protocol change); `cargo +1.94.1 clippy --workspace --all-targets -- -D warnings` clean (target-194); `cargo fmt --all -- --check` clean.

## 5. Things that will bite

- `grep`, `cat`, `head`, `find` are denied in Bash; use `python3 -I` or Serena reads. Edits via python.
- Release binaries: `CARGO_TARGET_DIR` is shared with the main checkout (`/home/user/eve.rs/target`); another checkout's build overwrote `target/release/eve-sim` (protocol 73) and the live run failed with "the host speaks version 74, this is 73". Build the release binaries into a worktree-private target dir (used: `/home/user/wt-st23-target`).
- The live script needs `tools/headless-client/.venv` in the worktree (gitignored): link it to `/home/user/eve.rs/tools/headless-client/.venv`; do not commit the link.
- The live run needs `EVE_SDE_DIR` (the sim reads static data from it: `/home/user/eve.rs/.cache/sde/eve-online-static-data-3396210-jsonl`).
- `EVE_LIVE_LOG_DIR` with `KEEP_LOGS=1` keeps the three processes' logs (`host.out`, `sim.out`, `net.out`); without it the logs are deleted.
- The durable event bus delivers the owners' notices and the ore field a few seconds after the fire or the completion: live checks poll.
- Disk: the release builds and the wide runs need about 6 GB free.

## 6. Control tower return live walk (2026-10-23)

Added `ShipService.Drop` for the V24 client call, connected to the existing deployable provider with the item-keyed result list and CustomNotify error row the client helper consumes. A deterministic scenario in `tools/headless-client/tests/test_structures.py` uses a player corporation in the legal low-security system, places an Amarr Control Tower through `ship.Drop`, checks that an online tower is refused by `posMgr.UnanchorStructure`, changes the seeded tower to offline, then follows the unanchoring job through hull drop and `ship.Scoop`. The assertion checks ownership, location, cargo flag, the item-change notice, and removal of the space row. It moves only this scratch-schema job's due time forward so the Action does not wait for the SDE unanchoring delay.

The existing `live-slow.yml` selection already includes `test_structures.py`. The focused Action result will be recorded here after the push.

## 7. GitHub Actions verification (2026-10-23)

- Commit `01053a2e290731e0ea551abb2b855e6dbaf7c0b9`, Agent test run [40041270573](https://github.com/Fokiiiiiii/eve.rs/actions/runs/40041270573): `Eve.Services.Space.Tests` focused scoop test 1 passed, 0 failed, 0 skipped; `tools/headless-client/tests/test_structures.py` 22 passed, 0 failed, 0 skipped.
- This confirms the headless `ship.Drop` -> offline unanchor -> hull drop -> `ship.Scoop` flow, including `OnItemChange` and cargo state. No graphical V24 client run was part of this check.

## 8. Eject to an original ship at a distant structure (2026-10-24)

The `EjectFromStructure` path restores the exact physical ship recorded by `BoardStructure` after validating that it remains the character's singleton ship in the same solar system. It no longer applies the client boarding-distance rule to this restoration. EveJS v0.12.9 restores `restoreShipID` without a proximity check; the existing live scenario places the return refinery 200,000 km from the station-side ship and checks the `OnShipCosmeticsChanged` notification.

The first push gate (run 737 on `486812c1`) also showed that the focused Host test still expected a distance refusal. It now asserts that the distant original ship is restored and the structure pilot is released. Both `Eve.Services.Space.Tests` and `Eve.Host.Tests` Release builds passed with 0 warnings and 0 errors; their focused Actions checks are pending.

## 9. Actions verification (2026-10-24)

- Commit `fd8f993872f6ee812533e9652bfa2e56a954fe12`, Agent test run [40043915164](https://github.com/Fokiiiiiii/eve.rs/actions/runs/40043915164): `Eve.Host.Tests` RPC inventory test 1 passed, 0 failed, 0 skipped; `tools/headless-client/tests/test_structures.py` 22 passed, 0 failed, 0 skipped.
- The live selection includes the distant-structure Eject restoration and the control-tower unanchor/drop/scoop scenario. It ran headlessly; no graphical V24 client check was part of this result.

## 10. CI 737 test corrections (2026-10-24)

- CI 737 on `486812c1` failed the distant-ship Host test because it still expected the old range refusal. The test now verifies ship restoration and pilot release at 155,000 m surface distance.
- Agent test run 231 on the same commit passed all 22 selected live structure tests. Its Space test filter passed 2 and failed 2 because the new fixture names exceeded the two-space limit; the names are now valid and the focused rerun is pending.
