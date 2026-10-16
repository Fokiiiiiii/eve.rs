# ST-19 handoff: the ST-16 open items

Package ST-19 of the structures phase. Worktree `wt-st19` from origin/main e33d21f6; database `eve_p3`. Rules: `agent-rules.md`. Decisions and sources: DEVIATIONS "Structure ST-19 (2026-10-16)". Rows: TODO section 59. The open items are ST-16's "Not done" items 5 to 8 (`handoff-ST-16.md`).

## What was built (each increment pushed to main)

- **(4) Creditor migration (3c39b045).** `CorporationOfficeStore.StationsWithoutCreditorAsync` / `CreditStationBillsAsync` and `StructureOfficeRentDue.CreditStationBillsAsync` (called from `CorporationOffices()` at startup): a station's creditless rent bills get the station owner, the open ones get their `office.rent_due` job. Idempotent.
- **(2) Mission pockets (00f75300).** `DungeonWaveDirector.ActiveOriginsAsync`: the origins of the active `dungeon` instances of a system are deadspace anchors (with the ST-11 sites, radius 1e9 m) in `SpaceComposition`.
- **(3) Control towers (11d06dc0).** Protocol 73, kind 92 `PlaceControlTower` (Rust `Role::ControlTower`, `place_control_tower`; C# `SimPlaceControlTower`, codec, `ISimulation`). Host: `SpaceContainers.Disruptors.cs` (group 365 through `LaunchFromShipAsync`; record `control_tower`, never expires; anchoring by the SDE attribute 556 through the existing `space.deployed.anchor` job; `RemoveControlTowerAsync`); `SpaceContainers.Deployed.cs` (restore). Migration 0015. The observatory's 100 km check reads the tower records as well as the structure records.
- **(1) Destruction wrap over the real read model (07e329aa).** `StructureDestructionLootAuthority.WrapWith` is the composition's wiring; `Destruction_wrap_of_the_composition_is_the_real_asset_safety_read_model...` drives it with a real `PersistenceStructureAssetSafetyReadModel`. The test found a defect: K-space regions were wormholes in asset safety (SDE `wormholeClassID` 7/8/9), so every K-space structure was refused the wrap. Fixed (`IsWormholeClass`), and the asset-safety rig uses the same rule.
- **Live script (this package's last commit).** `tools/dev/run-eve-live.sh` isolates `EVE_CONTRABAND_SCHEMA` (the ST-16 customs schema was not scratch or dropped; `LiveScriptIsolationTests` failed on it).

## Verified (eve_p3; counts are passes with zero failures and zero skips)

- Full Host `Eve.Host.Tests`: 372 of 372 (run before the live-script fix; the two `LiveScriptIsolationTests` then pass: 2 of 2).
- Full Space `Eve.Services.Space.Tests`: 923 of 923.
- Full Corporations `Eve.Corporations.Tests`: 129 of 129.
- Full codec `Eve.Space.Tests`: 352 of 352.
- Rust `cargo test -p eve-space -p eve-net`: eve-space 836 unit and integration pass (after the protocol pin moved to 73 in `sim/tests.rs`), eve-net passes.
- Narrow: `ControlTowerHostTests` 3/3, `DungeonWaveTests` deadspace test, `StructureUnanchorDropTests` 5/5 (incl. the real-wrap test), `StationBillCreditorMigrationTests` 1/1, `MobileObservatoryHostTests` (in the same run), Rust `a_control_tower_is_in_space...`, simlink fixtures 8/8.
- `cargo clippy --workspace --all-targets -- -D warnings` clean (target `/home/user/eve.rs/target-194`).

## Not done (named precisely)

1. **Client removal of a control tower.** `RemoveControlTowerAsync` exists; no client RPC calls it (the client's tower interface was not traced). The item is deleted on removal; there is no return to the hold.
2. **Tower fuel, modules, online state beyond anchored.** Online means anchored; no fuel is modelled.
3. **Tower category and scan.** A tower is a charge-category ball (as an observatory) and is not a scan target. The SDE category of group 365 and the client's ball were not traced.
4. **Mission pocket as a simulation body.** The pocket is a host-side deadspace anchor; the simulation has no pocket ball.
5. **Rent due job after the creditor migration.** Not exercised by a test (the store-level test covers the migration and the scheduling list).
6. **Anchoring time of the tower.** The SDE gives 30 minutes (Amarr Control Tower, attribute 556); the request named 10 minutes from the wiki, which was not verified on a wiki page in this package. See DEVIATIONS.

## Things that will bite

- `grep`, `cat`, `find` are denied in Bash here: `tail`, `python3 -I` and the Serena tools are the substitutes. Serena reads only `/home/user/eve.rs`, the main checkout (same content as main).
- The `/tmp/eve-heavy.lock` queue is shared with the other packages: a build may wait behind another agent's build.
- The protocol number is 73 on main; the next sim change takes 74 (`PROTOCOL_VERSION`, `SimCodec.cs`, `gen-simlink-fixtures.py` Hello/HelloAck, a test pin in `sim/tests.rs`, `messages.json`).
- SDE `wormholeClassID` 7/8/9 are K-space; 1-6, 12, 13 and 19-25 are the wormhole, Abyssal and Pochven regions (`WormholeSystemClasses`).
