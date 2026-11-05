# ST-8a Host Handoff

## Status

The structure Host module path is wired for ordinary targeted launcher, neutralizer, and EWAR activations. Structure control now sends the controlled structure's `ShipChanged` spec after the session switches to the structure. Structure owner-corporation legality is checked before the Host asks Sim to activate a targeted offensive module.

This does not complete area-effect point defense or guided-bomb authorization. Those remain fail-closed until Sim can ask Host to authorize the entities at each point-defense cycle or bomb detonation; the parent is assigning this wire work to a separate single-owner implementation slice.

## Integration points now present

- `dotnet/src/Eve.Host/Composition/StructureModules.cs` is in `namespace Eve.Host;` and declares `internal sealed partial class CharacterServicesComposition`.
  - `SetStructureWeaponTargetContext(Func<long, long, CancellationToken, Task<WeaponTargetContext>> read)` stores the owner-corporation facts callback.
  - `SetupStructureModules()` assigns `StructureControlReadModel.DogmaAsync` and the stored target-context callback to `SpaceSessions`.
- `SpaceComposition.SpaceSimulation()` calls `SetStructureWeaponTargetContext(weaponSafetyReadModel.ReadStructureAsync)` immediately after the ordinary `WeaponTargetContextOf` assignment.
- `StructureComposition.Structures()` calls `SetupStructureModules()` immediately after `structureControlReadModel` is created.
- `StructureControlBackend.ControlAsync()` now calls `await _space.RefreshShipAsync(session, call.Cancellation)` immediately after `ApplyStructureControlSession(...)`. Refresh exceptions enter the existing compensation path. That path attempts Sim detach, restores/releases the owner row transactionally even if detach fails, restores the session snapshots, and reattaches plus refreshes the displaced controller when possible.
- `SpaceSessions.RefreshShipAsync(ClientSession, CancellationToken)` sends the current `SimShip` from `StructureDogmaOf` for a structure observer and surfaces missing snapshot / disconnected Sim failures. Isolated `SpaceSessions` fixtures without the Host read model still no-op.

## Gameplay and state

- Structure activation/deactivation reads the controlled structure's actual fitted items, charges, online state, structure skills, capacitor and conditions through `StructureControlReadModel.DogmaAsync`.
- Structure offensive authorization uses `PersistenceWeaponTargetContextReadModel.ReadStructureAsync(ownerCorporationId, targetCharacterId, token)`. Its war query receives the structure owner corporation, regardless of the ACL controller's corporation. Illegal neutral player and capsule targets are refused at full safety; active-war targets and non-player entities may proceed.
- Structure control does not use the controller's personal safety level, add aggression/crimewatch flags to that character, end hull invulnerability, or apply structure module operations to the retained personal hull. Structure-targeted overload, offline, rack-overload and heal operations refuse.
- Structure module charge reports use each fitted module's real owner ID. Owner/module tracking survives an ambiguous cancelled or lost Sim activation reply so a delayed `SimChargeSpent` cannot be attributed to the controller. A rejected Sim activation removes newly-added tracking without writing charge changes. Release flushes queued charge events and clears only tracked structure module state.
- Existing `SimAction.Bomb` has blast-area damage; the Host refuses structure `Bomb`, `Smartbomb` and `EcmBurst` packages because checking only the selected target does not authorize every entity in the blast.

## Effects and SDE

- `ProjectedEffect` now recognizes structure neutralizer, web, painter, damp, scramble, weapon disruption and ECM aliases.
- `MissileEffect` serves SDE launcher groups 1327 and 1562; structure festival group 1974 uses its festival family. `BombEffect` recognizes guided-bomb group 1328, which remains refused at activation until impact authorization exists.
- Matching SDE 3396210 records were used for the new Space test: type 35922 is a published Standup Multirole Missile Launcher I; 37848 is a published Standup Light Missile. Point Defense is different: effect 6443 is `effects.PointDefense` with range attribute 99, duration 73 and capacitor discharge 6; the effect is unpublished while module types 35926/47327 are published. The I/II radii are 2500/3000 m, capacitor cost 1500/1800 GJ, cycle 12000 ms and damage multipliers 1/1.25; ammo group is 604 = 4186. Do not infer effect publication from module publication.

## Sim contract still required

Point defense must enumerate current in-radius damageable entities every cycle. Host must authorize the candidate player/capsule IDs against the structure owner corporation for that cycle; Sim must apply damage only to the returned allowlist. A target list checked only when activation starts can hit a neutral that enters the radius later. Guided bombs need the same authorization at actual detonation. Do not connect either AoE action to an unconditional all-target damage path.

## Tests and verification

- Added Space Host-path tests for neutral-player and capsule refusals, active-war and non-player acceptance, no controller crimewatch, and no `SimChargeSpent` after range/capacitor refusals and retry.
- Added a Host read-model test asserting `ReadStructureAsync` uses owner corporation for its war callback rather than the ACL controller corporation.
- Existing `StructureControlReadModelTests` covers structure fitted/online state and the four structure-specific skills.
- Earlier handoff: Serena LSP reported no errors for the then-changed source and tests. The CX9 verification below supersedes its pending-build note.

## Upwell service-module Dogma lifecycle (CX9)

### Implementation
- DogmaIM online/offline calls keep the existing ship path and dispatch structure-bound service modules through StructureServiceModuleAuthority.
- The structure route validates the bound/docked structure, pilot and corporation ownership, module owner/type, and service slot. Taking an online service module offline requires the structure corporation's station-manager role.
- ServiceFuelRulesSource maps module names and reads fuel attributes from pinned SDE build 3396210. Dogma evaluation applies structure hull/service modifiers; the Host wire test verifies Astrahus market fuel at 30/hour and 2160 to online.
- Fuel debit, module state, service state, module cycle, timer and outbox events share the PostgreSQL transaction. Duplicate toggles do not charge again; offline removes the cycle without charge. The persisted OnStructureServiceChanged notification feeds CharacterGetServices and the lobby refresh path.
- In the covered Astrahus + Standup Market Hub I scenario, the source-level behavior matches EveJS for first-online fuel, cycle persistence, duplicate online, role-refused offline, authorized offline, and dogma/service notifications. EveJS also checks SOV upgrades, structure damage, and service proximity for applicable modules (`moduleOperation.js:2560-2640`; `structureServiceModules.js:80-120`; `structureServiceOnlineRequirements.js:75-90`). Those cases were not exercised by this service-fuel lifecycle test, so broader parity is not claimed.

### Evidence
- V24 client local effect and RPC rollback: eve/client/script/dogma/clientDogmaLocation.py:698-723.
- Structure fitting and offline authority: eve/client/script/ui/shared/fittingScreen/structureFittingController.py:38-62; eve/client/script/ui/services/structure/structureControl.py:106-109.
- Client Dogma event processing: eve/client/script/environment/godma.py:205-213 and eve/client/script/remote/michelle.py:487-488.
- `OnStructureServiceChanged` refreshes `CharacterGetServices` at eve/client/script/ui/services/structure/structureServices.py:47-55, which scatters the lobby update consumed at eve/client/script/ui/shared/dockedUI/lobbyWnd.py:302-304. `CharacterGetServices` feeds access decisions at structureServices.py:36-43, structure info at eve/client/script/ui/shared/info/infosvc.py:841-844, and jump-bridge route checks at eve/client/script/parklife/autopilot.py:624-627.
- EveJS module ownership/toggle/fuel/notification flow: evejs-261028.1/src/server/src/services/dogma/dogmaService/moduleOperation.js:2499-2525, 2560-2640, 2770+; service fuel and persisted service state: evejs-261028.1/src/server/src/services/structure/structureServiceModules.js:449-505, 791-929; offline authority: evejs-261028.1/src/server/src/services/structure/structureServiceAuthority.js:334-357.
- The equivalent source-level case is Astrahus + Standup Market Hub I: 2159 fuel refuses without state/event changes; 2160 succeeds once, duplicate online is free, unauthorized offline refuses, and authorized offline is free. These outcomes follow EveJS's first-online fuel guard and previous-state checks in moduleOperation.js:2560-2640.
- Mechanics reference: [EVE University Upwell Structure Services](https://wiki.eveuniversity.org/Structure_Browser#Upwell_Structure_Services) and [Upwell Structures](https://wiki.eveuniversity.org/Structure_Browser#Upwell_Structures).

### Verification (2026-11-04)
- After fast-forwarding to main 2178010953f5, Host service-module wire, compression and notification tests: 14 passed, 0 skipped. This includes the PostgreSQL lifecycle case.
- Host composition tests: 18 passed, 0 skipped.
- Eve.Structures.Tests.StructureFuelTests: 10 passed, 0 skipped, including no-charge refusal, retry, duplicate, restart and fractional hourly cycles.
- Eve.Structures, Eve.Services.Dogma, and Eve.Host built successfully after the merge; Host.Tests and Structures.Tests also built during their test runs.
- eve_p9 needed the official mission-pinned 3405148 sde_mission catalog for unrelated Host startup tests; service fuel values continue to use the pinned 3396210 sde.
- EveJS behavior was compared at source level. Its package has no test script and the existing headless live tests have no focused service-module scenario, so no executable EveJS differential was run.
- The real graphical V24 client was not run.

### Previous push gate (2026-11-04)
- Latest completed main CI is run 1520 on 2178010953f5 and is green: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41929981873.
- At that check, origin/main was at 2178010953f5 and the CX9 feature commit had not been pushed. The newer verification and push state follow.

### Verification (2026-11-05)
- Merged origin/main at a5e2507f. `dotnet build dotnet/src/Eve.Host/Eve.Host.csproj -m:2 -p:UseSharedCompilation=false` succeeded with 0 warnings and 0 errors; this compiled the touched Host dependencies, including Eve.Fitting, Eve.Structures, Eve.Services.Dogma, and Eve.Services.Ships.
- After the merge, the Host selection passed 39/39 with no skips, including the wire lifecycle, preserved ship module success/refusal paths, StructureCompressionTests, StructureNotificationsTests, StructureCompositionTests, StructureControlReadModelTests, and StructureFuelOwnerNotificationTests. `StructureFuelTests` passed 10/10 with no skips. Both used `eve_p9` through `tools/dev/db-lock.sh`.
- The lifecycle test verifies an insufficient-fuel refusal without mutation, retry through the Host RPC, exact `OnMultiEvent` payload names and `clientID` address, outbox order, `OnStructureServiceChanged` arguments, `CharacterGetServices`, role-refused offline, authorized offline, duplicates, and two concurrent authoritative online calls producing only one charge/cycle.
- EveJS was compared at source level; no executable EveJS differential or real graphical V24 client session was used.
- The feature and test changes are not yet pushed. Latest completed main CI is run 1521 on 2417e284c79d, green: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41938528264. Main CI run 1522 for a5e2507f is still in progress: https://github.com/Fokiiiiiii/eve.rs/actions/runs/41948425442.
