# ST-2 Unanchor Drop Completion Handoff

## Status

The C# drop authority, timer callback, post-commit scene placement, and bound `Ship.Scoop` path are implemented. Parent owns the serial .NET build/test run and the Rust scene classification change. No .NET command or Git command was run in this worker.

## Runtime and Persistence Contract

- `StructureTimers.ApplyAsync` invokes the required completion callback only when `TimerDue` produces `Removal=Unanchored`. The callback receives the same PostgreSQL connection and transaction as the structure state write and outbox event. A callback or later transaction failure rolls back the structure transition, inventory items, and space rows together.
- The callback creates the hull as an owned physical item at the structure's solar system and exact `X/Y/Z`, with the original SDE hull `typeID` and `groupID`. If fittings or a missing installed core exist, it creates a type `10167` `Abandoned Container` at that same position and moves the attached assets into it. Rigs in flags 92–99 are deleted. Installed module/charge/fighter/fuel/deed item type IDs are retained; moved assets use the container's `ItemFlags.Hangar` flag. The Quantum Core type comes from SDE dogma attribute 3101 on the hull type.
- Space item radius comes from SDE attribute 162. Drop space rows have no expiry (`long.MaxValue`), and retain the SDE group ID. Hull/fittings markers correlate the item to the retired structure without replacing its type or group identity.
- `StructureStateChanged.UnanchorDrop` carries committed item IDs through the existing EventBus outbox. `SpaceContainers.SubscribeUnanchorDrops` places those items after commit. A later system entry restores them from `PgSpaceItemStore`; `SpaceContainers` keeps its existing idempotent placement set.
- Parent wiring in `StructureComposition.cs` uses `CreateStructureUnanchorDropAuthority()`, passes `CompleteAsync` to `StructureTimers`, and subscribes the existing `SpaceContainers` instance to the same EventBus. `CharacterServicesComposition.RunAsync` calls `LoadCatalogsAndStores()` before `SpaceSimulation()` and `Structures()`. `LoadCatalogsAndStores()` sets `spaceItemStore = new PgSpaceItemStore(source, spaceSchema)` before `Structures()`, independent of whether simulation is enabled. The factory reuses the same `PgItemStore`, `PgSpaceItemStore`, and schema owners; the `SpaceMoonFields` facade uses the same source/schema.

## Client Scoop Contract

- V24 client `MenuSvc.RealScoop` calls `ship.Scoop(objectID)` or `ship.Scoop(objectID, password)` after the ship is in interaction range. The new `[Method("Scoop")]` is on the existing bound `ShipService`; it uses the existing binder ownership check and keeps the normal no-value success response.
- `SpaceContainers.ScoopAsync` accepts only a valid unanchor hull marker on an SDE `Structure` type or the marker-verified type `10167` fittings container. Fittings containers open through `OpenAsync` even though their SDE group is 306. Any pilot may open, take from, or scoop these containers. For hulls, CCP says anyone may scoop an unanchored Upwell structure; unanchored Starbase hulls retain EveJS v0.12.9's owner/corporation access gate.
- Scoop checks the active ship/system, asks the simulation to measure the item, enforces the 2,500 m interaction range, and runs the existing cargo-hold type/capacity rules. `CharacterShips.ScoopSpaceArtifactAsync` locks the active ship, root item, and descendants; transfers the root to the character's cargo, changes every contained item's owner while preserving its type/location/flag/quantity, and removes the space row in one transaction. A stale item, invalid SDE/group record, full cargo hold, or persistence error cannot report a successful scoop or remove the scene item. `RemoveWreck` and ship inventory notifications happen only after commit. Container moves/loot-all now lock the container before its child items so they serialize with scoop.
- The optional password argument is accepted for the V24 call shape; these generated artifacts are not secure containers and do not acquire a guessed password rule.

## SDE and CCP Evidence

- The pinned official SDE build is 3396210. In that build, type `10167` is `Abandoned Container`, group `306`, category `11`; `typeDogma` attribute 3101 supplies each Upwell hull's Quantum Core type. Attribute 162 supplies the space radius. The space protocol must carry group 306 unchanged.
- CCP's [Upwell Structure Deployment and Unanchoring](https://support.eveonline.com/hc/en-us/articles/208289335-Upwell-Structure-Deployment-and-Unanchoring) describes the seven-day unanchor, says rigs are destroyed, and returns fitted modules, charges, Quantum Core, and fighters to a container that anyone may scoop; the hull remains in space as a scoopable item. [Structure Management](https://support.eveonline.com/hc/en-us/articles/208289605-Structure-Management) identifies the Quantum Core move into a space container. SDE service documentation: [Static Data](https://developers.eveonline.com/docs/services/static-data/).
- Read-only executable reference: EveJS v0.12.9 commit `828823a75f211fdecb945b087a26c225f111491f`. `ShipService.Handle_Scoop` dispatches to `scoopCargoContainerToCargo` / `scoopSpaceItemToShipHold`; that routine checks interaction distance, cargo room, removes the scene entity, transfers the root and all descendants atomically, and restores presentation on a failed inventory update. V24 client source is the pinned `eve-logic` revision `1dbb`.

## Required Parent/Rust Integration

`crates/eve-space/src/wreck.rs` currently has `CONTAINER_GROUP = 12`, and `Wreck::is_container()` relies on a group-only comparison. The fittings container must arrive from Host as `typeID=10167, groupID=306`; do not rewrite the SDE group to 12 in `SimPlaceWreck` or persisted state. Update the Rust domain classification so group 306/type 10167 is an openable/scoopable container while retaining the original group value. The unanchored hull keeps its own SDE group and uses its hull marker/category for the scoopable hull classification. Rust and protocol source remain parent/single-owner work.

## Focused PostgreSQL Tests for the Parent's Serial Run

Run with `EVE_PG_URL` pointed at the pinned SDE database:

```powershell
dotnet test dotnet/tests/Eve.Structures.Tests/Eve.Structures.Tests.csproj --filter "FullyQualifiedName~StructureUnanchorCompletionTests"
dotnet test dotnet/tests/Eve.Host.Tests/Eve.Host.Tests.csproj --filter "FullyQualifiedName~StructureUnanchorDropTests"
dotnet test dotnet/tests/Eve.Services.Space.Tests/Eve.Services.Space.Tests.csproj --filter "FullyQualifiedName~SpaceContainersUnanchorDropsTests"
```

The Structures tests cover the same-transaction callback/outbox, restart delivery, replay idempotency, and callback failure rollback. The Host test covers rollback after asset creation, dynamic SDE core selection, correct flags, rigs destroyed, group identity, position, and no-expiry persistence. The Space tests cover live post-commit placement, restart restore, open, bound `Ship.Scoop`, child ownership/notifications, and hull cargo-capacity behavior.

## Parent Test Run Update

Parent reported an initial focused Space test run with 9 tests: 8 passed and `Bound_ship_scoop_moves_the_fittings_artifact_and_children_then_checks_hull_hold_room` failed because its fixture reused the station-bound OID after undocking. The bound service correctly rejected that OID, and the handler returned `None` without changing custody. The fixture now calls `BindShipInSpaceAsync(session)` after `Undock`, matching the existing jettison test path. Parent must rerun the focused filter; this worker did not execute it. No production distance, authorization, or capacity rule was relaxed.

## Remaining Verification

Serena diagnostics are clean for the new drop authority, timer, scoop backend, and new test files after the fixes above. Serena still reports a `CS1503` at the pre-existing `ShipService.MachoBindObject` call (line 51) saying `ShipService` cannot convert to `IService`; it is outside the scoop method and may depend on generated service interfaces. Parent's serial .NET build must resolve whether this is a language-server/source-generator diagnostic. Graphical V24 client execution is not verified in this worker.


## 2026-10-24 Hull scoop access correction

- The bound scoop path now applies ownership authorization only to unanchored Starbase hulls. Upwell Structure hulls are open to any pilot, matching CCP's [Upwell Structures – Quick Facts](https://support.eveonline.com/hc/en-us/articles/209985225-Upwell-Structures-Quick-Facts) and EveJS v0.12.9's hull-kind-specific access check.
- The transactional path also checks that the Upwell hull item's owner matches the corporation recorded on its deployed-space row, so the public access rule does not bypass custody integrity.
- Focused tests cover a foreign pilot reaching the real cargo-capacity refusal for an Upwell hull, another corporation's tower being refused without scene removal, and owner/space-row mismatch being rejected before mutation.
- The Release build of `Eve.Services.Space.Tests` passed locally with 0 warnings and 0 errors. The focused service and live checks will be recorded after Actions; no local PostgreSQL test was run.


## Focused Actions follow-up (2026-10-24)

- Agent test run 231 on `486812c1` passed all 22 selected live structure tests. Its Space test filter passed 2 and failed 2 because the added pilot names exceeded the two-space character-name rule; the fixtures now use valid names, and the focused rerun is pending.


## Focused Actions rerun on 2026-10-24

- Agent test run 238 on `4930fd25` passed 3 of 4 Space cases and the Host Eject case passed 1 of 1. The remaining custody test passed no business assertion because its test session had no `corpid` field; it now reads the pilot's corporation from the authoritative character store. Focused rerun pending.
