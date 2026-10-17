# Acceleration gates and dungeon rooms

Status: the sim transit (protocol 75, command 34), the Host room layout, the room and gate placement in
running dungeons, and the `keeper.ActivateAccelerationGate` walk are built and unit-tested. Not yet done:
the live headless walk and MITM capture, the room-state writer (`currentRoomID`), room locks until
objectives are cleared, the SDE radius of the gate type, and the 3 gates of type 35776.

## Model

- A dungeon site is a set of rooms in one solar system. `DungeonRoomLayout` (`dotnet/src/Eve.Dungeons`)
  orders them (entry first, then by key number), anchors them (the site for the first room; others 1 000 km
  out plus 400 km per room, bearing by the golden angle, vertical step 10 km per room index), and places each
  gate of type 17831 22 km from the anchor of the room before the one it leads to.
- A pilot arrives in a room 15 km from its anchor, away from the site.
- Rooms after the entry are deadspace pockets (`dungeon_pocket`); gates are client balls (`dungeon_gate`).
  Both are placed while the dungeon runs and taken out when it ends (`SpaceContainers.ReconcileDeadspacePocketsAsync`).
- Ids: gates are `8e15 + instance * 256 + gate index` (below 2^53, because the transit command carries the id as a double).

## Walk (keeper.ActivateAccelerationGate)

`SpaceAccelerationGates.ActivateAsync`, checks in the reference's order:
1. the gate id belongs to the pilot's current dungeon (the keeper's current-dungeon answer);
2. the site is running and the gate is in its layout;
3. the ship: the gate's race list if it names one (`DunShipCannotWarp`), else the capital blacklist (`DunBlacklistCannotWarp`);
4. the simulation (`AccelerationGateTransit`, command 34): reach (gate radius + ship radius + 2 500 m), then the warp
   to the room's entry point, with modules stopped and locks cleared. Refusals map to `TargetTooFar`, `ShipInWarp`, `DeniedTargetAttemptFailed`.

## API for the FW complex director

- Place a pilot in a complex: the pilot must be in the complex's running dungeon instance; the gate is
  `SpaceContainers.AccelerationGateDeployableId(instanceId, gateIndex)`; the room is `DungeonLayout.Room(key)`.
- A complex with a single room needs no gate: the entry is the site itself (`DungeonLayout.Rooms[0].EntryPoint`).

## Limits (see DEVIATIONS)

No room locks, no objective gating, no room tracking for `GetCurrentDungeonForCharacter`, gate radius 0,
the 35776 gates are not placed. Sources: EveJS `keeperService.js`, `dungeonUniverseSiteService.js`; owner data in
`dotnet/src/Eve.Dungeons/Data/catalog/v1`.
