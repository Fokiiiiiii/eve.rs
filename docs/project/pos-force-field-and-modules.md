# POS fuel, force field, and industry arrays (2026-11-17)

## Scope and source trace

The V24.01 checkout at `/home/user/eve-logic` was searched across the client before editing. POS fuel/module state is read through the tower UI in `eve/client/script/ui/inflight/moonmining.py`; module onlining uses `eve/client/script/ui/services/menusvc.py:2493-2512` and `eve/client/script/parklife/playerOwned.py:328-332`. Force-field admission uses `eve/client/script/parklife/playerOwned.py:91-115,173-191`, and the settings are saved from `eve/client/script/ui/inflight/moonmining.py:1195-1219`. OBO targeting is initiated by `eve/client/script/parklife/playerOwnedTargetMgr.py:84-102`. The industry client calls facility discovery/detail/locations from `eve/client/script/industry/facilitySvc.py:42-72,114-120` and job installation/status from `eve/client/script/industry/industrySvc.py:92-110,139-160`.

The broad `rg` checks across `/home/user/eve-logic/eve` found the `forceFieldID` comparison only in `playerOwned.py:106-109`, the OBO calls only in `playerOwnedTargetMgr.py:87,102`, and the bunker lookup only in `factionWarfareSystemInfoContainer.py:54`. Industry tax reads/writes are consumed by `facilitySvc.py:90-94` and `facilityTaxWindow.py:30,73`. The requested `docs/project/coverage-sweep-1.md` is not present in this checkout; the five POS partial rows were cross-checked in `docs/project/service-audit/EVE_RS_COVERAGE.csv` and the supplied POS audit documents.

The comparison source is the pinned EveJS tree at `/home/user/evejs-261108.1`. Relevant behavior is in `server/src/services/ship/controlTowerRuntime.js:935-1032,1488-1554` (field create/reconcile and online transitions), `server/src/services/starbase/starbaseFuelRuntime.js:410-507` (fuel-failure shutdown), `server/src/services/starbase/starbaseAccessRuntime.js:162-170,216-263` (harmonic and admission), `server/src/services/starbase/starbaseControlRuntime.js:210-225` (OBO delegation), and `server/src/services/industry/starbaseIndustryRuntime.js:19-104` (POS industry profiles and association). EveJS is used for observable behavior; its state ownership and implementation are not copied.

## Verified changes

### Fuel failure and module state

An unpaid tower fuel period now writes the tower offline, writes associated online/onlining modules offline in the same PostgreSQL transaction, and cancels pending module-online jobs. After the transaction commits, the Host reads persisted tower/module state and updates the live simulator slim items (`posState` and `posTimestamp`). The same module shutdown is applied when a tower is explicitly taken offline. The change is in [`SpaceContainers.ControlTowers.cs`](../../dotnet/src/Eve.Services.Space/SpaceContainers.ControlTowers.cs:98).

The database-backed regression in [`ControlTowerHostTests.cs`](../../dotnet/tests/Eve.Services.Space.Tests/ControlTowerHostTests.cs:916) onboards two modules, onlines them, triggers an empty-fuel period, and checks both persisted state and simulator slim state after the scheduler transaction. This verifies Host/database/simulator state; it does not establish graphical-client rendering or the complete fuel-bay UI.

### POS assembly-array industry

The Host now resolves anchored, online POS assembly arrays and laboratories associated with a live control tower. The supported installation/activity/filter data is checked in at [`starbase-assembly-profiles.json`](../../dotnet/src/Eve.Host/Data/starbase-assembly-profiles.json) for V24 client build 3396210; the resource hashes and selection basis are recorded in the JSON. The Host registers deployed modules in facility discovery and implements detail, owner-bound facility taxes, corporation-hangar locations, and job install/read behavior. Jobs preserve the tower association internally, disappear from the public payload where EveJS omits it, and report paused while the tower/facility is offline. Material/output custody remains the existing corporation inventory authority.

The registered Host/PostgreSQL regressions are in [`IndustryFacilityHostFlowTests.cs`](../../dotnet/tests/Eve.Host.Tests/IndustryFacilityHostFlowTests.cs:72): facility listing/detail/locations and exact public fields; at line 137 tax authorization, notification and persistence across Host restart; and at line 206 a real corporation-hangar job install, material consumption, persisted tower association, offline pause and online completion. Tax changes require Station Manager access and notify `OnFacilitiesUpdated` after commit. EveJS `industryPayloads.js:113-184` omits internal `inventoryLocationID` and `starbaseTowerID`; the tests assert those fields do not leak.

Only the profiles in the checked-in data file are supported. No graphical V24 industry flow was run.

Capital profiles receive the V24 type-dogma security check before the Host reports them online. Profiles whose dogma requires a sovereignty level or infrastructure-hub upgrade remain listed offline: Host does not yet calculate authoritative hub-upgrade power/fuel state (the same unresolved budget recorded in `TODO.md` ST-17 c). `Capital_POS_assembly_array_obeys_its_V24_system_security_limit` covers the registered Host response for type 24656 in Jita. This is intentionally fail-closed; it is not evidence that the capital arrays can complete jobs in a qualifying system.

### POS battery control grant (2026-11-18)

V24.01 `playerOwned.AssumeStructureControl` (`playerOwned.py:428-453`) calls `posMgr.AssumeStructureControl(itemID)` for a battery, and `RelinquishStructureControl` (`playerOwned.py:455-475`) releases it. Both take one item ID and return nothing the client reads.

Implemented in the Host:

- `AssumeStructureControl`: the battery must be a starbase module whose type carries dogma attribute 1167, must be online or onlining, and the calling pilot must belong to its corporation, be in its system and in space, and be within interaction distance (`RequireControlTowerAccessAsync`). The grant is one row of `starbase_battery_control` written under the battery's deployed-row lock. A grant held by another pilot is refused. Asking again for a grant the pilot holds is idempotent.
- `RelinquishStructureControl`: releases the calling pilot's grant only. It is idempotent, and it does not need the interaction-distance check, so a grant cannot be stranded.
- Leaving: when the holder leaves the cluster, its grants are released through `SpaceSessions.OnCharacterLeavingAsync`.
- No server notification is sent. The client's own `OnAssumeStructureControl` / `OnRelinquishStructureControl` are local scatters.

Verification status:

- `PosMgr_missing_client_methods_are_dispatched_through_the_registered_host_service` passed (1 passed, 0 skipped). It sends both methods through the registered Host and checks the argument shapes.
- `ControlTowerHostTests.PosMgr_battery_control_is_held_by_one_pilot_over_host_wire_and_is_released_by_relinquish_or_logoff` covers exclusivity, idempotence, relinquish, the holder's leaving, and the offline and non-battery refusals. Run on PostgreSQL `eve_p4` on 2026-11-18 after the SDE group join fix: 1 passed, 0 failed, 0 skipped. The class `ControlTowerHostTests` ran 11 passed, 0 failed, 0 skipped; PosMgr filters ran 6 passed (Space.Tests) and 5 passed (Host.Tests), all 0 skipped.
- The battery type is looked up at test time from the SDE: `sde.types` joined to `sde.groups` on the group key, filtered to `categoryID` 23 and published, then attribute 1167 from `sde.typeDogma`. The pinned SDE has 211 published category-23 types; 16631 and 16688 carry attribute 1167.

Still open: `AddTargetOBO` and `RemoveTargetOBO`, the `OnTargetOBO` notification, and graphical acceptance. See the OBO blocker below.

### POS OBO targeting: blocked, client shapes verified (2026-11-18)

`AddTargetOBO` and `RemoveTargetOBO` are not implemented in this run, and no RPC name is registered, because a registered handler would be a fake success.

Verified client contract (V24.01, read through codebase-memory `get_code_snippet` after the Read tool was refused for `/home/user/eve-logic`):

- `PlayerOwnedTargetMgr.LockTargetOBO(sid, tid, who)` (`eve/client/script/parklife/playerOwnedTargetMgr.py:84-99`) calls `godma.GetDogmaLM().AddTargetOBO(sid, tid)` and reads `(flag, targetList)`; a false flag runs `OnTargetAdded`. A `UserError` named `DeniedShipChanged` is swallowed; other messages are shown.
- `PlayerOwnedTargetMgr.UnLockTargetOBO(sid, tid)` (lines 101-103) calls `GetDogmaLM().RemoveTargetOBO(sid, tid)` and ignores the result.
- `OnTargetOBO(what, sid, tid, reason)` consumers: `playerOwned.py:501-507` (`add` stores `currenttargets[sid] = tid`, `lost` deletes it) and `ui/inflight/moondefencebutton.py:764-781` (`add`, `clear`, `lost`, matched on `sid` = the battery).
- EveJS (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseControlRuntime.js`): `addTargetOBO(session, batteryID, targetID)` (line 210) goes through `getControlledContext`, which requires the grant held by this session, refreshes the grant, and calls `scene.addTarget` for the battery's session. `removeTargetOBO` (line 217) cancels a pending lock and removes the target with `notifySelf` and `notifyTarget`. `notifyStarbaseTargetEvent` (line 66) sends `OnTargetOBO` to the grant holder only, with the id type `charid` and args `[what, batteryID, targetID, reason]`. Other pilots never receive OBO target IDs.

eve.rs state:

- The DogmaIM targeting partial (`dotnet/src/Eve.Services.Dogma/DogmaIMService.Targeting.cs`) has only the ship `AddTarget` / `RemoveTarget` / `CancelAddTarget` / `ClearTargets` / `GetTargets` / `GetTargeters` surface. `OrderAsync` acts on the session's own ship. The DogmaLM bound object does not exist in `dotnet/src` (no `DogmaLM` symbol).
- The Rust `crates/eve-space/src/targeting/mod.rs` holds locks for a session ship or a controlled Upwell structure. `check_lock` (`targeting/lock.rs`) reads `self.pilots[session]`, so a battery lock can reuse it only through a temporary pilot, as `with_controlled_structure_modules` (`scene/session.rs:50`) does for a controlled structure.
- Control towers are deployable balls (`Role::ControlTower`, `deployable.rs`) placed by `Message::PlaceControlTower`. A battery is a module, not a ball, and no link message places it or sets it online.
- Consequence: a handler that accepted `AddTargetOBO(battery, target)` through the existing ship path would change the pilot's ship lock, not the battery's. That is the failure `audit-fixes-dogma.md` already warned about.

Decision (2026-11-18): the battery actor is not committed as dead, unwired code. It needs three slices, in order:

1. Rust: a link message that places a battery and sets its online state (ADR 0007 kind codes on both codecs, `crates/eve-space/src/link/kind.rs` and the C# `SimMessages`), a battery entry in `Scene` with its own `Targeting`, lock through the temporary-pilot path, and translation of the `OnTarget` outputs to `OnTargetOBO` addressed to the grant holder, plus a settle hook for pending locks. Rust unit tests cover lock, pending completion, cancel, remove, the range and cap refusals, and the offline refusal.
2. .NET: a DogmaLM bound object in `Eve.Services.Dogma` with `AddTargetOBO` and `RemoveTargetOBO`. Each validates the `starbase_battery_control` grant held by the caller, that the battery is online, and that the target is valid, then forwards the order to the Rust battery actor. The return shape is `(flag, targetList)`.
3. Host tests through the real registration: grant held and not held, battery offline, invalid target, lock and remove, and the `OnTargetOBO` notification with `[what, batteryID, targetID, reason]` to the holder only.

Graphical V24 acceptance is still required after slice 3; none of these slices has been run against the client.

Slice 1 status (2026-11-18, Rust and codec, protocol 102): done in the simulation and the C# codec. `PlaceBattery` (168), `BatteryPlaced` (169), `BatteryTarget` (170) and `BatteryTargets` (171) are in `crates/eve-space` (`link`, `scene`, `targeting/battery.rs`, `sim/dispatch.rs`) and in `dotnet/src/Eve.Space` (`SimCodec*.cs`, `SimMessages.cs`). The battery has its own lock state; the holder alone is told `OnTargetOBO`. Verified: `cargo test -p eve-space --lib` 1055 passed, 0 failed, 0 skipped (8 battery scene tests, 2 link frame tests and 1 simulation test by `handle`/`poll` among them); `dotnet test dotnet/tests/Eve.Space.Tests` 571 passed, 0 failed, 0 skipped (2 battery codec tests); `cargo clippy -p eve-space --all-targets -- -D warnings` clean; `Eve.Space` Release build with warnings as errors clean.
Not done in slice 1: `oracle/fixtures/simlink/messages.json` is not regenerated. `tools/dev/gen-simlink-fixtures.py` contains kinds 168 to 171, the frame writers, valid and invalid cases, and version 102, but the script could not be run in this session (the command policy refused it), so the shared golden frames do not yet cover the battery kinds. Run the generator before relying on the fixtures; the Rust and C# round-trip tests above are the only coverage until then. The `simlink_fixtures` Rust tests (14 passed) and the C# fixture tests still pass against the old file.

Slices 2 and 3 status (2026-11-18, Host): `AddTargetOBO` and `RemoveTargetOBO` are on the dogma location (`DogmaIMService.Targeting.cs`), through `ISpaceTargeting.BatteryOrderAsync`, `SpaceContainers.Batteries.cs` (grant, online state, SDE numbers, placement, order) and the sim client's battery replies. Verified: `ControlTowerHostTests` battery tests 5 passed, 0 failed, 0 skipped on `eve_p4` (no grant, offline, invalid target, lock and remove, holder-only `OnTargetOBO`); the whole class 16 passed alone. Release builds with warnings as errors: `Eve.Host`, `Eve.Services.Space.Tests`, `Eve.Space`. Open: the full `Eve.Services.Space.Tests` run is not green in one pass (intermittent unrelated failures under parallel runs; the baseline comparison was not completed), the fixture regeneration, a battery going offline outside an order, and graphical acceptance.

### POS force field ball, slice 1 (2026-11-18)

Scope: the force field ball of an online control tower, in the simulation, created and removed by SimLink kinds 180 to 183 (protocol 103). The V24 client reads a tower's field through `playerOwned.py:91-115` (`forceFieldID` and the ball's harmonic, compared with the ship's); EveJS creates the field as an inventory item with `createSpaceItemForOwner` (`controlTowerRuntime.js:935-1032`).

Implemented:

- `crates/eve-space`: `PlaceForceField` / `ForceFieldPlaced` / `RemoveForceField` / `ForceFieldRemoved` in `link` (kind 180 to 183), a `Role::ForceField { tower_id }` deployable in `deployable.rs` (`place_force_field`, `remove_force_field`), and dispatch in `sim/dispatch.rs`. A field is a ball with the scene's usual visibility, removal and watcher handling.
- `dotnet/src/Eve.Space`: the same four records in `SimMessages.cs`, their decode and encode arms, kind constants and a placement validator in `SimCodec.Shared.cs`; `SimCodec.ProtocolVersion` is 103.
- `tools/dev/gen-simlink-fixtures.py`: kinds, encoders, valid and invalid cases for the four messages, and Hello/HelloAck at 103.

Verified:

- `cargo test -p eve-space --lib`: 1064 passed, 0 failed, 0 skipped. Nine of them are new: four link frame tests (`link::force_field_tests`) and five simulation tests (`sim::tests::force_field_tests`): placement and removal, an unchanged re-placement, a replacement id, a refused radius (0, -5, NaN), an id held by another tower, a missing field, and an unknown system.
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean. `cargo fmt --all` applied.
- `dotnet test dotnet/tests/Eve.Space.Tests`: 574 passed, 0 failed, 0 skipped (3 are new in `ForceFieldCodecTests`).
- Release builds with warnings as errors: `Eve.Space` and `Eve.Space.Tests`, 0 warnings, 0 errors.

Not done in slice 1 (the gaps stay open):

- The golden fixtures are not regenerated: `tools/dev/gen-simlink-fixtures.py` is refused by the command policy in this session, so `oracle/fixtures/simlink/messages.json` and the shared Rust/C# fixture tests do not cover kinds 180 to 183 or version 103. The generator source is updated and must be run to regenerate the file.
- The Host does not send `PlaceForceField` / `RemoveForceField`. The tower-online and tower-offline transitions (`SpaceContainers.ControlTowers.cs`) need a durable item id for the field (EveJS creates a real inventory item); the Host has no allocator for it, and inventing an id would be a fake success. No `ISimulation` / `SimClient` method for the two kinds exists yet.
- The field's category is the default charge category (`category_of`), not the SDE category of the force field type; the radius and the type come from the message.
- Admission and the boundary are slice 2 (below); the harmonic publication and the field's start time are slice 3 (below).

### POS force field admission and boundary, slice 2 (2026-11-18)

Scope: who a force field admits and what happens to a ship it does not admit, in the simulation. Sources: EveJS `starbaseAccessRuntime.js` (`getForceFieldAccess`, `canShipEnterForceField`, `matchesPassword`), `starbaseForceFieldBoundary.js` (`enforceForceFieldMovement`, `resolveDeniedEntry`, `synchronizeShipHarmonic`, `BOUNDARY_CLEARANCE_METERS` 1 m, repulsion 500 to 2 500 m/s), and `forceFieldContainment.js` (group 411 containment, the `centerDistance + shipRadius < fieldRadius` test, which the boundary does not use). The client compares a ship's harmonic with the field's (`playerOwned.py:91-115`).

Implemented in `crates/eve-space/src/scene/force_field.rs`:

- Admission (`Access::admits_group` plus the password set): a ship is admitted by its corporation when the tower allows the corporation and the ids are non-zero; by its alliance when the tower allows the alliance and the id is non-zero; or by a matching ship password. Other ships are not admitted.
- Boundary (`enforce_force_fields`, called from the pilot settle path in `scene/movement.rs`): for each field, the boundary is the field radius plus the ship radius plus 1 m. A move that crosses the boundary is stopped on its surface where it enters. A ship that starts inside is not pulled back, and is pushed out at 500 to 2 500 m/s. A crossing move is always in EveJS's boundary-contact band, so its outward speed is 10% of the inward speed, capped at 100 m/s. An order that pulls the ship back in is cancelled (`Course::Stop`). A ship in warp is not checked; a warp that completes is checked at its end.
- Harmonic (state only): a ship admitted inside a field gets the tower's id as its harmonic, a ship with no admitted field has none. It is held in `Scene::force_field_harmonics` and is told to the client in slice 3 (below).
- Protocol 103, kinds 184 `SetForceFieldAccess` and 185 `ShipForceFieldPassword` (no answer). `Access` is held per tower, so an access set before the field is placed applies to it. A pilot that is no longer in the scene loses its password and harmonic at the next settle.

Verified:

- `cargo test -p eve-space --lib`: 1082 passed, 0 failed, 0 skipped, after a clean rebuild of the package. Slice 2 adds 18 of them: 15 scene tests (`scene::force_field_tests`: the EveJS geometry, admission by corporation, alliance and password, a denied alliance, a field without access, the boundary push and its 100 m/s contact value, the 500 m/s push for a ship inside, the order cancel, the warp exemption, the scene settle path, and the leave cleanup), two link frame tests for kinds 184 and 185 (roundtrip, kinds, bad flags), and one sim test (access and password kept without an answer).
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean. `cargo fmt --all` applied.
- `dotnet test dotnet/tests/Eve.Space.Tests`: 575 passed, 0 failed, 0 skipped. One is new: `Force_field_access_and_ship_password_roundtrip_over_the_sim_link`.
- Release builds with warnings as errors: `Eve.Space` and `Eve.Space.Tests`, 0 warnings, 0 errors.

Gaps and approximations (not verified against a client):

- Standings admission is not implemented: the pinned EveJS tree does not back it (the rule the task named), so it is recorded here as a gap rather than invented.
- The Host does not send `SetForceFieldAccess` or `ShipForceFieldPassword`, and it cannot place a field yet (the slice 1 gap: no field item id). The Host already has `SetShipPasswordAsync` (`dotnet/src/Eve.Services.Space/SpaceSessions.cs:181-217`), which matches the password with `StarbasePasswordHash` and sets a `harmonic` slim field from the tower's deployable id without any field. That is a separate path from the simulation's harmonic and must be reconciled when the Host is wired.
- A ship that is not in a field's range is never checked, so a field that is not placed does not stop anything. Nothing reaches the client yet: the boundary correction moves the ship in the simulation, and the correction is published only through the existing position updates.
- Approximations: a warp is treated as completed when the flight is gone after a settle (EveJS marks the completion explicitly); the order cancel for `Goto` uses the heading, and for `Follow` and `Orbit` always cancels (EveJS's `targetPoint` is null for an entity target, which is what it does); NPC pilots are checked like player pilots, which EveJS does not confirm for NPC ships.
- The `Role::ForceField` ball still has the default charge category (slice 1).

### POS force field presentation, slice 3 (2026-11-18)

Scope: what the clients that see a force field and its ships are told. Client facts used (V24.01, read with codebase-memory `get_code_snippet` and not run): `PlayerOwned.CompareShipStructureHarmonic` (`playerOwned.py:91-115`) returns true when the field ball's harmonic is not -1 and equals the ship's; `ForceField.Assemble` (`environment/spaceObject/forceField.py:11-23`) reads the slim item's `forcefieldStartTime` to play the field's start animation after 10 s. EveJS shapes: `buildForceFieldBoundaryCorrectionUpdates` and `buildStarbaseHarmonicUpdates` (`destiny/commands/starbaseForceField.js`), and `buildSetBallHarmonicPayload` (`destiny/stream/actions.js:209-226`, `SetBallHarmonic` with entity, harmonic, corporation, alliance, forcefield flag).

Implemented:

- The field's ball carries its tower as its harmonic (`deployable.rs` `ball_harmonic`, in the AddBalls the watchers receive).
- The field's slim item carries `forcefieldStartTime` set to the tick it was placed at (a field placed again starts again).
- A boundary stop is told to the bound pilot and to its observers (`scene/force_field.rs` `publish_force_field`, through the same `tell_split` path as the collision correction): `SetSpeedFraction 0` and `Stop` if the order was cancelled, then `SetBallPosition` if the ship was moved, then `SetBallVelocity`, in EveJS's order.
- A change of a pilot's harmonic is told as `SetBallHarmonic [ship, harmonic, corporation, alliance, 0]`, with -1 for none.
- A field going down and coming back is the existing deployable path: the ball is removed from, or added to, the views of the pilots who see it.

Verified (headless, Rust unit tests on the scene's own output):

- `cargo test -p eve-space --lib`: 1087 passed, 0 failed, 0 skipped after a clean rebuild of the package. Slice 3 adds five: the ball's harmonic, the start time (placed, and placed again), the stop told as position and velocity with no harmonic, the cancelled order told in EveJS's order, and an admitted ship told the tower's harmonic.
- `cargo clippy -p eve-space --all-targets -- -D warnings`: clean.

Not verified and not done:

- No graphical client run, so nothing here is shown to a pilot yet.
- The notification a pilot sees when a field goes down or comes back is not implemented. The client events for a tower's state come from the Host through `posState` and `OnStructureFullyOnline` (`playerOwned.py:73-89`); no client event named for a force field going down was found (codebase-memory: `forceField.py`, `moonmining.py`, `menusvc.py` `EnterForceFieldPassword` only). The Host does not place fields yet, so there is nothing to notify.
- A ship's harmonic is told only when the boundary runs on a move and the harmonic changes. A pilot who is already inside an admitted field when another pilot first sees it is not given the harmonic in its first ball (its ball is sent with no harmonic), and the `SetBallHarmonic` argument shape is EveJS's, not a client-verified one.
- A pilot's harmonic is kept per session; a ship that is not moved is not re-told, which matches EveJS only as far as its move-driven sync goes.
- The field's category is still the default charge category (slice 1).

### POS force field Host wiring, slice 4 (2026-11-19)

Scope: the Host gives an online control tower its force field and sends the admission state, so slices 1 to 3 reach the simulation. The gap this closes was the missing durable item id: the field is now a real inventory item, as EveJS creates it (`controlTowerRuntime.js:935-1032`, `TYPE_FORCE_FIELD` 16103, group 411, name "Force Field", `ATTRIBUTE_SHIELD_RADIUS` 680).

Implemented:

- Persistence: `SpaceItemKind.ForceField` (6) in `PgSpaceItemStore.cs`, and migration `0034_force_field_kind.sql`, which widens the `space_items_kind_valid` check to 0 to 6 and adds a partial unique index on `launcher_id` where `kind = 6`, so a tower has at most one field item. The field item is created with `PgItemStore.CreateAsync` and its space row written with `PgSpaceItemStore.SaveAsync`. Its launcher is the tower, its corporation and alliance are the tower's, its radius is the tower type's SDE `shieldRadius` (30 000 m for the Amarr Control Tower, type 12235), and its type is 16103.
- Reconcile (`SpaceContainers.ControlTowers.cs` `ReconcileForceFieldAsync`): run by `SynchronizeControlTowerSlimStateAsync` after the state transaction commits, and by `RestoreDeployedAsync` when a system opens. An online tower gets one field item (kept on a repeat sync) and `PlaceForceField`. Any other state deletes the item and sends `RemoveForceField`, and a removal the simulation answers with `FORCE_FIELD_NOT_IN_SPACE` counts as done. A placement the simulation refuses is an error (`InvalidOperationException`) and the item stays, so the next sync retries.
- Triggers covered: the online job (`CompleteControlTowerOnlineAsync`), the hourly fuel job when the bay cannot pay, explicit offline (`OfflineControlTowerAsync`), and restart.
- Simulation interface: `ISimulation.PlaceForceFieldAsync` and `RemoveForceFieldAsync` (answers matched by field id and tower id in `SimClient`), and `SetForceFieldAccess` (kind 184) and `SetShipForceFieldPassword` (kind 185), both without an answer.
- Access: `SetTowerPassword` sends `SetForceFieldAccess` after its commit, with the corporation and alliance of the tower and the access flags it saved. A password of `false` keeps the saved password, as before.
- Ship password: `SpaceSessions.SetShipPasswordAsync` now matches the password once against every control tower of the system. The harmonic slim field is written once from the first online match (as before), and the simulation receives `SetShipForceFieldPassword` for each tower with that same match, so the two cannot disagree.

Verified (PostgreSQL `eve_p6`, `EVE_SDE_DIR` of the pinned SDE, Release, warnings as errors):

- `ControlTowerHostTests`: 20 passed, 0 failed, 0 skipped. Three are new: `Force_field_follows_its_online_tower_once_and_is_taken_out_when_the_tower_goes_offline_by_fuel_or_by_request` (one item, SDE radius and type, repeat sync keeps the id, restart re-places it, fuel-out removal, online again, explicit offline removal), `A_field_the_simulation_refuses_is_an_error_and_is_placed_again_once_the_simulation_accepts_it`, and `Tower_access_reaches_the_simulation_each_time_the_posMgr_password_is_saved_over_host_wire` (real posMgr Host wire).
- `SetShipPasswordHostTests`: 1 passed, 0 failed, 0 skipped. The existing registered-route test gained assertions: one harmonic write per call, one `SetShipForceFieldPassword` matching the tower, a refused match for the wrong password, and none for an invalid password. Its existing assertions are unchanged.
- Release build of `Eve.Services.Space.Tests` with `TreatWarningsAsErrors`: succeeded, 0 warnings.

Not verified and not done:

- The full `Eve.Services.Space.Tests` project on `eve_p6` (Release, warnings as errors): 1270 passed, 0 failed, 0 skipped (8 m 13 s).
- `Eve.Host` and `Eve.Host.Tests` were not built or run for this slice.
- `oracle/fixtures/simlink/messages.json` is not regenerated (the generator is not run, per the session's policy).
- A tower's password change does not re-match pilots that already hold a credential: `SpaceSessions` keeps only the SHA-256 digest of each pilot's password, so it cannot match against the new tower hash. Those pilots are matched again only when they set their password.
- The simulation's per-pilot harmonic (`SetBallHarmonic`, slice 3) and the ship's `harmonic` slim field (the Host's write) remain two separate paths, each written once per event; they are not unified.
- Standings admission remains a documented gap (slice 2).
- No graphical client run.

### POS force field password re-match, slice 5 (2026-11-19)

Scope: a tower's saved password changes while pilots already hold a ship password. Client fact (V24.01, `eve-logic` `playerOwned.py:182-191`, `EnterShipPassword`, read with codebase-memory `get_code_snippet`): the client sends the plaintext ship password only from its prompt, so the Host can never be sent it again. The digest is therefore the only credential kept, as before.

EveJS fact (`/home/user/evejs-261108.1/server/src/services/starbase/starbaseAccessRuntime.js:197-263`, read with the Read tool): the credential keeps `sha256(password)` and the match is `scrypt(hex(digest), salt)` against the tower's saved hash, so a new saved hash is checked again from the same digest. The Host's `StarbasePasswordHash` (`dotnet/src/Eve.Services.Space/StarbasePasswordHash.cs`) uses the same scheme (`scrypt` N=16384, r=8, p=1, 32 bytes, with the lowercase hex digest as the input), so no plaintext is needed.

Implemented:

- `StarbasePasswordHash.MatchesDigestAsync` and `DeriveFromDigestAsync` take the raw SHA-256 digest. The password overloads delegate to them, so stored hashes are unchanged.
- `SpaceSessions.ShipPasswords.cs`: `MatchShipPasswordAsync` is the one per-tower match (the first online match is the harmonic, and every tower's answer is returned). `SetShipPasswordAsync` uses it, unchanged in effect.
- `RematchShipPasswordsAsync(systemId)` runs for each pilot in the system that holds a credential for its current ship. It matches the kept digest again, writes the harmonic slim field, and sends `SetShipForceFieldPassword` for each tower. A pilot whose credential changed meanwhile is skipped.
- `SpaceContainers.SetTowerPasswordAsync` calls it after the commit when the saved hash changed (a password of `false` does not).

Verified (PostgreSQL `eve_p6`, Release, warnings as errors):

- `ControlTowerHostTests.Tower_password_change_matches_a_pilot_holding_a_ship_password_again_from_its_kept_digest`: 1 passed, 0 failed, 0 skipped. Set "field-secret" (harmonic = tower, simulation told a match), change the tower to another password (harmonic -1, simulation told a refusal), set it back (harmonic = tower again). The posMgr wire carries every tower change.
- `ControlTowerHostTests` class: 21 passed, 0 failed, 0 skipped.
- `SetShipPasswordHostTests`: 1 passed, 0 failed, 0 skipped (the existing route test, unchanged assertions).
- Release build of `Eve.Services.Space.Tests` (with its project graph) with `TreatWarningsAsErrors`: 0 warnings, 0 errors.

Not done: the harmonic is still written by the Host from its own match, and the simulation's per-pilot harmonic is a separate path; this is slice 6 (unification, below). No graphical client run.

## Open items

- **POS force field and password admission:** the Host now places and removes an online tower's field and sends its access and ship-password state (slice 4, migration 0034). A tower's password change re-matches pilots holding a credential from the kept digest (slice 5). Still open: the client-visible force-field presentation is not verified on the graphical client; standings admission is not implemented; the simulation's per-pilot harmonic and the ship's `harmonic` slim field are separate paths.
- **Starbase OBO target calls:** `AddTargetOBO` and `RemoveTargetOBO` remain unimplemented. The battery control grant (`AssumeStructureControl` / `RelinquishStructureControl`) now exists (see POS battery control grant below), but the DogmaLM bound object and the battery's target lock state in the Rust scene do not. Registering the two RPC names without them would not implement the client flow.
- **Sentry targeting:** sentry settings persist and round-trip, but no associated battery targeting/defense controller consumes the policy.
- **Other POS inventory/use consumers:** silo `viewput`/`viewputtake` is enforced. Ship-maintenance, reprocessing/compression arrays and jump-bridge `use` behavior remain incomplete.
- **Moon/reaction production:** module onlining/resource accounting and silo-to-silo transfer are implemented, but historical moon output is not configured in the pinned EveJS legacy authority, and the SDE contains no historical moon abundance. The server does not fabricate output.
- **Control Bunker:** `GetControlBunkerIDInSystem` remains `None`. The client at `eve/client/script/ui/shared/agencyNew/ui/contentPageInfoConts/factionWarfareSystemInfoContainer.py:49-60` requires a live inventory item and ball. The pinned SDE has bunker types but no per-system live item IDs/positions, and the matching EveJS tree has no live bunker placement authority. Do not synthesize an ID or position.

These gaps remain `PARTIAL-TESTED` or open in [`EVE_RS_COVERAGE.csv`](service-audit/EVE_RS_COVERAGE.csv) and [`DEVIATIONS.md`](../reference/compat/DEVIATIONS.md). The control-bunker evidence is in [`DEEPCHECK_facWarMgr.md`](service-audit/DEEPCHECK_facWarMgr.md); POS client/EveJS row traces are in [`posmgr-partial-audit-2026-11-13.md`](posmgr-partial-audit-2026-11-13.md).

## Verification limits

Registered Host/PostgreSQL coverage and simulator slim-state assertions are headless checks. A graphical V24 client was not launched, and no complete same-scenario EveJS POS run was executed. The exact local pass counts and build results follow.

## Verification (2026-11-17)

Verified:

- `IndustryFacilityHostFlowTests`: 17 passed, 0 failed, 0 skipped on PostgreSQL `eve_p4`.
- Related Host industry classes (`IndustryJobPersistenceTests`, `IndustryJobsHostWireTests`, `IndustryHostRpcTests`, `IndustryFacilityManagerTests`, `IndustryJobNotificationWireTests`, `IndustryActivityPersistenceTests`, `IndustryCorporationPersistenceTests`): 29 passed, 0 failed, 0 skipped on `eve_p4`.
- `IndustryJobsServiceTests` and `PosMgrServiceTests`: 23 passed, 0 failed, 0 skipped.
- `StructureCompositionTests` and `StructurePosHostWireTests`: 21 passed, 0 failed, 0 skipped on `eve_p4`.
- `CommandBurstHostTests` and `SpaceContainerTests`: 51 passed, 0 failed, 0 skipped on `eve_p4`.
- `ControlTowerHostTests`: 10 passed, 0 failed, 0 skipped on `eve_p4`.
- Release builds with warnings as errors for `Eve.Host`, `Eve.Services.Core`, `Eve.Services.Space`, and their three test projects: 0 warnings, 0 errors.
- `cargo fmt --all --check`, `cargo check --workspace`, and `cargo clippy --workspace --all-targets -- -D warnings` passed. No Rust source changed.
- The profile JSON content matches the pinned EveJS profile file; the checked-in Git blob uses LF line endings while the source file uses CRLF. `git diff --check` passed.

Not checked:

- Graphical V24 acceptance, a POS-specific headless-client walk, and a complete paired EveJS/eve.rs POS run.
- Force-field ball lifecycle/admission, OBO targeting, sentry control, bunker placement, and hub-upgrade power/fuel behavior; these remain open as listed above.
- Repository-wide CI for the uncommitted worktree.
