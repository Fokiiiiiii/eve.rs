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
- Admission and the boundary are slice 2 (below); the harmonic publication and the client notifications are slice 3.

### POS force field admission and boundary, slice 2 (2026-11-18)

Scope: who a force field admits and what happens to a ship it does not admit, in the simulation. Sources: EveJS `starbaseAccessRuntime.js` (`getForceFieldAccess`, `canShipEnterForceField`, `matchesPassword`), `starbaseForceFieldBoundary.js` (`enforceForceFieldMovement`, `resolveDeniedEntry`, `synchronizeShipHarmonic`, `BOUNDARY_CLEARANCE_METERS` 1 m, repulsion 500 to 2 500 m/s), and `forceFieldContainment.js` (group 411 containment, the `centerDistance + shipRadius < fieldRadius` test, which the boundary does not use). The client compares a ship's harmonic with the field's (`playerOwned.py:91-115`).

Implemented in `crates/eve-space/src/scene/force_field.rs`:

- Admission (`Access::admits_group` plus the password set): a ship is admitted by its corporation when the tower allows the corporation and the ids are non-zero; by its alliance when the tower allows the alliance and the id is non-zero; or by a matching ship password. Other ships are not admitted.
- Boundary (`enforce_force_fields`, called from the pilot settle path in `scene/movement.rs`): for each field, the boundary is the field radius plus the ship radius plus 1 m. A move that crosses the boundary is stopped on its surface where it enters. A ship that starts inside is not pulled back, and is pushed out at 500 to 2 500 m/s. A crossing move is always in EveJS's boundary-contact band, so its outward speed is 10% of the inward speed, capped at 100 m/s. An order that pulls the ship back in is cancelled (`Course::Stop`). A ship in warp is not checked; a warp that completes is checked at its end.
- Harmonic (state only): a ship admitted inside a field gets the tower's id as its harmonic, a ship with no admitted field has none. It is held in `Scene::force_field_harmonics` and is not yet published to the client (slice 3).
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

## Open items

- **POS force field and password admission:** the simulation now has the force-field ball (slice 1, protocol 103), but the Host does not yet send it when a tower comes online or goes offline (no field item id), does not publish `forceFieldID`, update its Destiny harmonic, or check corp/alliance/ship-password admission at a movement boundary. `SetTowerPassword` persistence alone is not the feature. The existing command-burst check uses the SDE shield radius and tower online state for that module's eligibility; it does not supply the force-field ball or crossing behavior.
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
