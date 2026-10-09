using Eve.Dogma;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Npgsql;

namespace Eve.Ships;

/// <summary>A drone that has left the bay: the item it is in space (a singleton in the solar system) and what the client is told changed, in order.</summary>
public sealed record DroneLaunch(Item Drone, IReadOnlyList<ItemChanged> Changes, ItemMutationRecord? Mutation = null);

/// <summary>What became of a drone that was to go back into the bay.</summary>
public abstract record DroneScoop;

/// <summary>The drone is in the bay again (the item as the client is told it changed).</summary>
public sealed record DroneScooped(ItemChanged Change) : DroneScoop;

/// <summary>The bay has no room for it (what is left of it, and what the drone takes).</summary>
public sealed record DroneBayFull(long TypeId, double Available, double Volume) : DroneScoop;

/// <summary>The drone is not an item of the character's in the solar system.</summary>
public sealed record DroneNotThere : DroneScoop;

/// <summary>
/// Drones as the ship's character deals with them: an item leaves the drone bay for the solar system when it is launched (one unit of a stack becomes a singleton of its own) and
/// goes back to the bay when it is scooped. The drone in space is the simulation's ball; the item is what the database has of it.
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>An item as the database has it (what a launch is asked about is read before it is moved).</summary>
    public Task<Item?> ItemAsync(long itemId, CancellationToken cancellationToken = default) => items.GetAsync(itemId, cancellationToken);

    /// <summary>
    /// One drone of <paramref name="itemId"/> (a stack, or a drone of its own, in the drone bay of the ship) goes out into the solar system: a stack gives one unit that becomes an item of
    /// its own, a single drone is the item that moves. Null when the item is not a drone of the character's in that bay.
    /// </summary>
    public async Task<DroneLaunch?> LaunchDroneAsync(long characterId, long shipId, long itemId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item
            || item.OwnerId != characterId || item.LocationId != shipId || item.Flag != DroneBayFlag)
        {
            return null;
        }
        var provenance = await items.Mutations.GetAsync(item.Id, cancellationToken);
        await statics.EnsureTypesAsync(ItemDogma.TypeIdsOf(item, provenance), cancellationToken);
        if (statics.Type(item.TypeId)?.CategoryId != DroneCategory)
        {
            return null;
        }

        DroneLaunch launched;
        if (!item.Singleton && item.Quantity > 1)
        {
            var created = await items.MoveStackAsync(connection, transaction, item, 1, characterId, solarSystemId, InSpaceFlag, cancellationToken);
            await items.MakeSingletonAsync(connection, transaction, created.Id, cancellationToken);
            var drone = created with { Singleton = true };
            launched = new DroneLaunch(drone, [new ItemChanged(item with { Quantity = item.Quantity - 1 }, shipId, DroneBayFlag, item.Quantity), new ItemChanged(drone, 0, InSpaceFlag, 0)]);
        }
        else
        {
            await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, solarSystemId, InSpaceFlag, cancellationToken);
            await items.MakeSingletonAsync(connection, transaction, item.Id, cancellationToken);
            var drone = item with { LocationId = solarSystemId, Flag = InSpaceFlag, Singleton = true, Quantity = 1 };
            launched = new DroneLaunch(drone, [new ItemChanged(drone, shipId, DroneBayFlag, item.Quantity)], provenance);
        }
        await transaction.CommitAsync(cancellationToken);
        return launched;
    }

    /// <summary>
    /// The drone, in the solar system, goes into the drone bay of the character's ship. The bay keeps room for the drones that are still out (<paramref name="stillOut"/>, their item ids),
    /// as the reference's does, so a drone that was launched always has a berth waiting; one that was not (a drone found, one let go) must find real room.
    /// </summary>
    internal async Task<DroneScoop> ScoopDroneAsync(long characterId, long shipId, long droneId, int solarSystemId, IReadOnlyCollection<long> stillOut, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await items.LockAndGetAsync(connection, transaction, droneId, cancellationToken) is not { } drone
            || drone.OwnerId != characterId || drone.LocationId != solarSystemId || drone.Flag != InSpaceFlag
            || await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return new DroneNotThere();
        }
        await statics.EnsureTypesAsync([drone.TypeId], cancellationToken);
        if (statics.Type(drone.TypeId) is not { } type || type.CategoryId != DroneCategory)
        {
            return new DroneNotThere();
        }
        var bay = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => i.Flag == DroneBayFlag).ToList();
        var reserved = new List<Item>();
        foreach (var id in stillOut.Where(id => id != droneId))
        {
            if (await items.GetAsync(id, cancellationToken) is { } other && other.OwnerId == characterId && other.LocationId == solarSystemId && other.Flag == InSpaceFlag)
            {
                reserved.Add(other);
            }
        }
        var required = ItemVolumes.Of(drone, type.GroupId, ItemVolumeOf(type));
        var room = new Room(CapacityAttributeOf(DroneBayFlag) is { } capacityAttribute ? snapshot.World.Get(shipId, capacityAttribute) : 0,
            await VolumeOfAsync([.. bay, .. reserved], cancellationToken));
        if (required > room.Free + RoomTolerance)
        {
            return new DroneBayFull(drone.TypeId, Math.Round(room.Free, 6), Math.Round(required, 6));
        }
        await items.MoveAsync(connection, transaction, drone.Id, shipId, DroneBayFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DroneScooped(new ItemChanged(drone with { LocationId = shipId, Flag = DroneBayFlag }, drone.LocationId, InSpaceFlag, drone.Quantity));
    }

    /// <summary>
    /// The attributes a drone has on its own: its base (the input it was made from, when it was mutated), its rolled values and its result's skill
    /// requirements. The ship's modifiers are not in this world; a launch takes the drone's numbers from the ship's world once the drone is in it.
    /// Used before the drone leaves the bay, to refuse a drone that has no operation and to count its bandwidth.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, double>> DroneAttributesAsync(Item drone, CancellationToken cancellationToken = default)
    {
        var provenance = await items.Mutations.GetAsync(drone.Id, cancellationToken);
        await statics.EnsureTypesAsync(ItemDogma.TypeIdsOf(drone, provenance), cancellationToken);
        var world = new DogmaWorld(statics);
        world.Add(ItemDogma.Of(drone, drone.OwnerId, drone.LocationId, drone.Flag, mutation: provenance));
        return world.Attributes(drone.Id);
    }

    /// <summary>A drone is gone (it was destroyed, or let go): its item is deleted. Returns the item as it was, or null when it was not there.</summary>
    public async Task<Item?> RemoveDroneAsync(long characterId, long droneId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, droneId, cancellationToken) is not { } drone || drone.OwnerId != characterId || drone.Flag != InSpaceFlag)
        {
            return null;
        }
        await items.DeleteAsync(connection, transaction, drone.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return drone;
    }

    /// <summary>
    /// The drones a ship left in space when the simulation was lost are taken into the drone bay of the ship that was recovered (what the bay has no room for stays where it is: it is the
    /// character's, in the solar system). Returns how many went back.
    /// </summary>
    private async Task<int> RecoverDronesAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, int solarSystemId, long shipId, CancellationToken cancellationToken)
    {
        var strays = (await items.InLocationAsync(solarSystemId, characterId, cancellationToken)).Where(i => i.Flag == InSpaceFlag && i.Id != shipId && i.Singleton).ToList();
        if (strays.Count == 0)
        {
            return 0;
        }
        await statics.EnsureTypesAsync(strays.Select(i => i.TypeId), cancellationToken);
        var returned = 0;
        foreach (var drone in strays.Where(i => statics.Type(i.TypeId)?.CategoryId == DroneCategory))
        {
            await items.MoveAsync(connection, transaction, drone.Id, shipId, DroneBayFlag, cancellationToken);
            returned++;
        }
        return returned;
    }


    /// <summary>The in-space <c>invbroker.GetCapacity(87)</c> answer: the active ship's drone capacity and the volume physically in its bay.</summary>
    public async Task<Room?> DroneBayRoomInSpaceAsync(long characterId, long shipId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        if (await items.GetAsync(shipId, cancellationToken) is not { } ship
            || ship.OwnerId != characterId || ship.LocationId != solarSystemId || ship.Flag != InSpaceFlag
            || await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return null;
        }

        var contents = (await items.InContainerAsync(shipId, cancellationToken)).Where(i => i.Flag == DroneBayFlag);
        return new Room(CapacityAttributeOf(DroneBayFlag) is { } capacityAttribute ? snapshot.World.Get(shipId, capacityAttribute) : 0,
            await VolumeOfAsync(contents, cancellationToken));
    }

    /// <summary>The in-space <c>invbroker.Add</c> destination for a drone bay. A valid Add moves one owned drone item into the active ship's bay.</summary>
}
