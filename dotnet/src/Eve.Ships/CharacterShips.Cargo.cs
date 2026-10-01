using Eve.Fitting;
using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// The holds of a ship: how much each takes and holds, and the stacks in them. What the reference answers for the cargo hold of a ship in a hangar, as recorded
/// (<c>oracle/fixtures/scenarios/cargo.json</c>): the capacity is the ship's own (dogma's capacity, skills and modules included), what is in the hold takes the volume of
/// each unit, and what does not fit is refused with the room that is left and the room that was asked for.
/// </summary>
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
