using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// The holds of a ship: how much each takes and holds, and the stacks in them. What the reference answers for the cargo hold of a ship in a hangar, as recorded
/// (<c>oracle/fixtures/scenarios/cargo.json</c>): the capacity is the ship's own (dogma's capacity, skills and modules included), what is in the hold takes the volume of
/// each unit, and what does not fit is refused with the room that is left and the room that was asked for.
/// </summary>
public sealed record DroneMiningCommit(bool Accepted, string Reason, long UnitsRemaining, long DeliveredUnits, bool Depleted, ShipChanged? Change);
public sealed record DroneSalvageCommit(bool Accepted, string Reason, ShipChanged? Change, Item? Container, SpaceItem? ContainerSpaceItem);

public sealed partial class CharacterShips
{
    /// <summary>The drone bay of a ship: it holds drones (category 18) and nothing else, up to its drone capacity (<c>NotEnoughDroneBaySpace</c> when they do not fit).</summary>
    public const int DroneBayFlag = 87;

    private const int DroneCategory = 18;

    private const int CapacityAttribute = 38;
    private const int DroneCapacityAttribute = 283;
    private const int VolumeAttribute = 161;

    /// <summary>The room that is left in a hold is not worked out finer than this (the volumes are sums of floats).</summary>
    private const double RoomTolerance = 1e-7;

    /// <summary>How big a hold is, and how much is in it.</summary>
    public readonly record struct Room(double Capacity, double Used)
    {
        public double Free => Math.Max(0, Capacity - Used);
    }

    /// <summary>The attribute that says how big a bay is (a bay the ship has no word for holds nothing).</summary>
    private static int? CapacityAttributeOf(int flag) => flag switch
    {
        CargoFlag => CapacityAttribute,
        DroneBayFlag => DroneCapacityAttribute,
        _ => null,
    };

    /// <summary>The volume of what these items take.</summary>
    public async Task<double> VolumeOfAsync(IEnumerable<Item> contents, CancellationToken cancellationToken = default)
    {
        var list = contents.ToList();
        await statics.EnsureTypesAsync(list.Select(i => i.TypeId), cancellationToken);
        var used = 0d;
        foreach (var item in list)
        {
            if (statics.Type(item.TypeId) is { } type)
            {
                used += ItemVolumes.Of(item, type.GroupId, type.Attributes.GetValueOrDefault(VolumeAttribute, 0));
            }
        }
        return used;
    }

    /// <summary>
    /// How big a bay of a ship the character owns is and what is in it. The capacity of the ship the character flies is dogma's (its skills and modules act on it);
    /// the capacity of another ship in the hangar is its type's.
    /// </summary>
    public async Task<Room?> RoomAsync(long characterId, long shipId, int flag, CancellationToken cancellationToken = default)
    {
        if (await items.GetAsync(shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return null;
        }
        await statics.EnsureTypesAsync([ship.TypeId], cancellationToken);
        var capacity = 0d;
        if (CapacityAttributeOf(flag) is { } attribute)
        {
            capacity = await characters.GetAsync(characterId, cancellationToken) is { ShipId: { } active } && active == shipId && await LoadAsync(characterId, cancellationToken) is { } snapshot
                ? snapshot.World.Get(shipId, attribute)
                : statics.Type(ship.TypeId)?.Attributes.GetValueOrDefault(attribute, 0) ?? 0;
        }
        var contents = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => i.Flag == flag);
        return new Room(capacity, await VolumeOfAsync(contents, cancellationToken));
    }

    public async Task<(Item Item, SpaceItem SpaceItem)> CreateAsteroidAsync(
        int typeId, int solarSystemId, long beltId, long units, IReadOnlyList<double> position, IReadOnlyList<double> direction,
        double radius, PgSpaceItemStore spaceItems, CancellationToken cancellationToken = default)
    {
        await statics.EnsureTypesAsync([typeId], cancellationToken);
        if (statics.Type(typeId) is not { } type || statics.CategoryName(type.CategoryId) != "Asteroid" || units <= 0)
        {
            throw new ArgumentException("The SDE type is not a published asteroid ore type.", nameof(typeId));
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, 0, solarSystemId, InSpaceFlag, units, true), cancellationToken);
        var item = new Item(id, typeId, 0, solarSystemId, InSpaceFlag, units, true, null);
        var spaceItem = new SpaceItem(id, solarSystemId, position, direction, radius, type.GroupId, beltId, 0, 0, 0, 0, long.MaxValue);
        await spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (item, spaceItem);
    }

    public async Task<DroneMiningCommit> MineAsteroidAsync(
        long characterId, long shipId, int solarSystemId, long droneId, long asteroidId, double yieldM3,
        double wasteVolumeMultiplier, double wasteProbability, double roll, PgSpaceItemStore spaceItems,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(yieldM3) || yieldM3 <= 0 || !double.IsFinite(wasteVolumeMultiplier)
            || !double.IsFinite(wasteProbability) || !double.IsFinite(roll))
        {
            return new(false, "INVALID_PROFILE", 0, 0, false, null);
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return new(false, "NO_SHIP", 0, 0, false, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, droneId, cancellationToken) is not { } drone
            || drone.OwnerId != characterId || drone.LocationId != solarSystemId || drone.Flag != InSpaceFlag
            || statics.Type(drone.TypeId) is not { } droneType || statics.CategoryName(droneType.CategoryId) != "Drone")
        {
            return new(false, "NOT_CONTROLLED", 0, 0, false, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, asteroidId, cancellationToken) is not { } asteroid
            || asteroid.LocationId != solarSystemId || asteroid.Flag != InSpaceFlag || asteroid.Quantity <= 0)
        {
            return new(false, "NO_TARGET", 0, 0, false, null);
        }
        await statics.EnsureTypesAsync([asteroid.TypeId], cancellationToken);
        var type = statics.Type(asteroid.TypeId);
        var volumeAttribute = statics.AttributeId("volume");
        var unitVolume = type is null || volumeAttribute is not { } volumeId
            ? 0d
            : type.Attributes.GetValueOrDefault(volumeId, 0d);
        if (type is null || statics.CategoryName(type.CategoryId) != "Asteroid" || !double.IsFinite(unitVolume) || unitVolume <= 0)
        {
            return new(false, "NO_TARGET", asteroid.Quantity, 0, false, null);
        }
        var miningUnits = (long)Math.Floor(yieldM3 / unitVolume);
        if (miningUnits <= 0)
        {
            return new(false, "INVALID_PROFILE", asteroid.Quantity, 0, false, null);
        }
        var wasteUnits = roll * 100d < Math.Clamp(wasteProbability, 0d, 100d)
            ? (long)Math.Floor(yieldM3 * Math.Max(0d, wasteVolumeMultiplier) / unitVolume)
            : 0L;
        var wasted = Math.Min(asteroid.Quantity, Math.Max(0, wasteUnits));
        var delivered = Math.Min(miningUnits, Math.Max(0, asteroid.Quantity - wasted));
        if (delivered > 0)
        {
            var candidate = new Item(0, asteroid.TypeId, characterId, shipId, CargoFlag, delivered, false, null);
            if (await RefuseWhatDoesNotFitTogetherAsync(snapshot, [candidate], CargoFlag, cancellationToken) is not null)
            {
                return new(false, "CARGO_FULL", asteroid.Quantity, 0, false, null);
            }
        }
        var remaining = Math.Max(0, asteroid.Quantity - wasted - delivered);
        ItemChanged? change = null;
        if (delivered > 0)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(asteroid.TypeId, characterId, shipId, CargoFlag, delivered, false), cancellationToken);
            change = new ItemChanged(new Item(id, asteroid.TypeId, characterId, shipId, CargoFlag, delivered, false, null), solarSystemId, InSpaceFlag, 0);
        }
        await items.SetQuantityAsync(connection, transaction, asteroidId, remaining, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, "", remaining, delivered, remaining == 0, change is null ? null : new ShipChanged(change.After.Id, shipId, [], [], [change]));
    }

    public async Task<DroneSalvageCommit> SalvageWreckAsync(
        long characterId, long shipId, int solarSystemId, long droneId, long wreckId, int? productTypeId,
        SpaceItem wreckSpace, long containerExpiresAtMs, PgSpaceItemStore spaceItems,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return new(false, "NO_SHIP", null, null, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, droneId, cancellationToken) is not { } drone
            || drone.OwnerId != characterId || drone.LocationId != solarSystemId || drone.Flag != InSpaceFlag
            || statics.Type(drone.TypeId) is not { } droneType || statics.CategoryName(droneType.CategoryId) != "Drone")
        {
            return new(false, "NOT_CONTROLLED", null, null, null);
        }
        if (await items.LockAndGetAsync(connection, transaction, wreckId, cancellationToken) is not { } wreck
            || wreck.LocationId != solarSystemId || wreck.Flag != InSpaceFlag || wreckSpace.ItemId != wreckId
            || wreckSpace.SolarSystemId != solarSystemId)
        {
            return new(false, "NO_WRECK", null, null, null);
        }
        int[] typeIds = productTypeId is { } product ? [wreck.TypeId, product] : [wreck.TypeId];
        await statics.EnsureTypesAsync(typeIds, cancellationToken);
        var wreckType = statics.Type(wreck.TypeId);
        if (wreckType is null || !statics.GroupName(wreckType.GroupId).Contains("Wreck", StringComparison.OrdinalIgnoreCase))
        {
            return new(false, "NO_WRECK", null, null, null);
        }
        if (productTypeId is { } productTypeIdValue)
        {
            if (statics.Type(productTypeIdValue) is not { } productType
                || !statics.GroupName(productType.GroupId).Equals("Salvaged Materials", StringComparison.OrdinalIgnoreCase))
            {
                return new(false, "INVALID_SALVAGE_PRODUCT", null, null, null);
            }
            // ponytail: one salvage unit per successful wreck until SDE exposes an authoritative per-wreck amount table.
            var productCandidate = new Item(0, productTypeIdValue, characterId, shipId, CargoFlag, 1, false, null);
            if (await RefuseWhatDoesNotFitTogetherAsync(snapshot, [productCandidate], CargoFlag, cancellationToken) is not null)
            {
                return new(false, "CARGO_FULL", null, null, null);
            }
        }
        var contents = await items.LockContainerAsync(connection, transaction, wreckId, cancellationToken);
        var moved = new List<ItemChanged>(contents.Count + (productTypeId is null ? 0 : 1));
        Item? container = null;
        SpaceItem? containerSpace = null;
        if (contents.Count > 0)
        {
            await statics.EnsureTypesAsync([CargoContainerType], cancellationToken);
            if (statics.Type(CargoContainerType) is not { } canType)
            {
                return new(false, "NO_CONTAINER_TYPE", null, null, null);
            }
            var canId = await items.CreateAsync(connection, transaction, new NewItem(
                CargoContainerType, wreck.OwnerId, solarSystemId, InSpaceFlag, 1, true), cancellationToken);
            container = new Item(canId, CargoContainerType, wreck.OwnerId, solarSystemId, InSpaceFlag, 1, true, null);
            foreach (var content in contents)
            {
                await items.MoveAsync(connection, transaction, content.Id, canId, ItemFlags.Hangar, cancellationToken);
                moved.Add(new ItemChanged(content with { LocationId = canId }, content.LocationId, content.Flag, content.Quantity));
            }
            var radiusId = statics.AttributeId("radius");
            var radius = radiusId is { } radiusAttribute
                ? canType.Attributes.GetValueOrDefault(radiusAttribute, wreckSpace.Radius)
                : wreckSpace.Radius;
            containerSpace = new SpaceItem(
                canId, solarSystemId, wreckSpace.Position, wreckSpace.Direction, radius, canType.GroupId, wreckSpace.LauncherId,
                wreckSpace.CharacterId, wreckSpace.CorporationId, wreckSpace.AllianceId, wreckSpace.WarFactionId, containerExpiresAtMs);
            await spaceItems.SaveAsync(connection, transaction, containerSpace, cancellationToken);
        }
        if (productTypeId is { } salvageType)
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(salvageType, characterId, shipId, CargoFlag, 1, false), cancellationToken);
            moved.Add(new ItemChanged(new Item(id, salvageType, characterId, shipId, CargoFlag, 1, false, null), solarSystemId, InSpaceFlag, 0));
        }
        await items.DeleteAsync(connection, transaction, wreckId, cancellationToken);
        await spaceItems.RemoveAsync(connection, transaction, wreckId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        var change = moved.Count == 0 ? null : new ShipChanged(moved[0].After.Id, shipId, [], [], moved);
        return new(true, "", change, container, containerSpace);
    }

    /// <summary>What is moved into a hold of the ship the character flies when <paramref name="quantity"/> of the item goes there: refused when it does not fit.</summary>
    private async Task<ShipRefused?> RefuseWhatDoesNotFitAsync(ShipSnapshot snapshot, Item item, long quantity, int flag, CancellationToken cancellationToken)
    {
        if (statics.Type(item.TypeId) is not { } type || CapacityAttributeOf(flag) is not { } attribute)
        {
            return null;
        }
        var required = ItemVolumes.Unit(item.TypeId, type.GroupId, item.Singleton, type.Attributes.GetValueOrDefault(VolumeAttribute, 0)) * Math.Max(1, quantity);
        var contents = (await items.InContainerAsync(snapshot.Ship.Id, cancellationToken)).Where(i => i.Flag == flag);
        var room = new Room(snapshot.World.Get(snapshot.Ship.Id, attribute), await VolumeOfAsync(contents, cancellationToken));
        if (required <= 0 || required <= room.Free + RoomTolerance)
        {
            return null;
        }
        return new ShipRefused(flag == DroneBayFlag ? ShipRefusal.NotEnoughDroneBaySpace : ShipRefusal.NotEnoughCargoSpace, ModuleTypeId: item.TypeId, ShipTypeId: snapshot.Ship.TypeId, Available: Math.Round(room.Free, 6), Volume: Math.Round(required, 6));
    }

    /// <summary>
    /// <c>StackAll</c>: the stacks of one type in a place (a hangar, a ship's hold) become one, the oldest, and the others are gone. What is not stackable (an assembled
    /// item) and what sits in a slot is left alone. Answers what changed, in the order the client is told: the stack that grew, then the stack that went.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>> StackAllAsync(long characterId, long containerId, int? flag, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var stacks = (await items.InContainerAsync(containerId, cancellationToken))
            .Where(i => i.OwnerId == characterId && !i.Singleton && !Slots.IsSlotFlag(i.Flag) && (flag is null || i.Flag == flag))
            .GroupBy(i => (i.TypeId, i.Flag));
        var changes = new List<ItemChanged>();
        foreach (var group in stacks)
        {
            var survivor = group.First();
            foreach (var other in group.Skip(1))
            {
                // read again under the row locks: a stack that moved or went since the list was read is left as it is
                if (await items.LockAndGetAsync(connection, transaction, survivor.Id, cancellationToken) is not { } kept
                    || await items.LockAndGetAsync(connection, transaction, other.Id, cancellationToken) is not { } gone
                    || !Stackable(kept, containerId, group.Key.Flag, characterId) || !Stackable(gone, containerId, group.Key.Flag, characterId) || kept.TypeId != gone.TypeId)
                {
                    continue;
                }
                var merged = kept with { Quantity = kept.Quantity + gone.Quantity };
                await items.SetQuantityAsync(connection, transaction, kept.Id, merged.Quantity, cancellationToken);
                await items.DeleteAsync(connection, transaction, gone.Id, cancellationToken);
                changes.Add(new ItemChanged(merged, kept.LocationId, kept.Flag, kept.Quantity));
                changes.Add(new ItemChanged(gone with { LocationId = JunkLocation }, gone.LocationId, gone.Flag, gone.Quantity));
                survivor = merged;
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return changes;
    }

    private static bool Stackable(Item item, long containerId, int flag, long characterId) =>
        !item.Singleton && item.LocationId == containerId && item.Flag == flag && item.OwnerId == characterId;
}
