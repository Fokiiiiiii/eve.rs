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
- Serena LSP reported no errors for all changed source files and both added tests. No .NET build or tests were run here; the parent must run them serially after this handoff.
