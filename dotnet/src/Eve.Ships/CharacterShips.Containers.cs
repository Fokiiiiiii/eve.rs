using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// Cans and wrecks in space as the character's ship deals with them: cargo jettisoned into a can, and goods moved between the ship's hold and what a can or a wreck
/// holds. A can is an item of its own (it is in the solar system, and what is in it is in it); the simulation only has its ball (see <c>SpaceContainers</c>). What a pilot may
/// open, and from how far, is decided before these are called; here is what is moved, with the stacks read under their row locks as the market does.
/// </summary>
public sealed partial class CharacterShips
{
    /// <summary>The type of the can cargo is jettisoned into (<c>Cargo Container</c>).</summary>
    public const int CargoContainerType = 23;

    /// <summary>The flag what is in a can (or a wreck) is kept under.</summary>
    public const int ContainerFlag = 0;

    /// <summary>The holds of a ship that can be emptied into a can in space: the cargo hold (the reference lists the specialised holds too).</summary>
    public static bool IsJettisonable(int flag) => flag == CargoFlag;

    /// <summary>The can for cargo a pilot jettisons: an item of the character's, in the solar system, with nothing in it yet.</summary>
    public async Task<Item> CreateCanAsync(long characterId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await items.CreateAsync(connection, transaction, new NewItem(CargoContainerType, characterId, solarSystemId, InSpaceFlag, 1, Singleton: true), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (await items.GetAsync(id, cancellationToken))!;
    }

    /// <summary>
    /// Takes a can or a wreck out of the world (and what is in it, when <paramref name="withContents"/>): a can that could not be put in space, one that was emptied, one
    /// whose time ran out. Returns the item that was removed, or null when there was none.
    /// </summary>
    public async Task<Item?> RemoveContainerAsync(long containerId, bool withContents, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (await items.LockAndGetAsync(connection, transaction, containerId, cancellationToken) is not { } container)
        {
            return null;
        }
        if (withContents)
        {
            foreach (var inside in await items.InContainerAsync(containerId, cancellationToken))
            {
                await items.DeleteAsync(connection, transaction, inside.Id, cancellationToken);
            }
        }
        await items.DeleteAsync(connection, transaction, containerId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return container;
    }

    /// <summary>
    /// The stacks in the ship's hold go into the can, whole and with the ids they have, in the order asked. What is not the character's, not in the ship's hold or is not a thing
    /// that can be jettisoned stays where it is (it is not in the answer). Each stack is told as what it was before: where it was and the flag.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>> JettisonIntoAsync(long characterId, long shipId, long canId, IReadOnlyList<long> itemIds, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        var moved = new List<ItemChanged>();
        foreach (var id in itemIds.Distinct())
        {
            if (await items.LockAndGetAsync(connection, transaction, id, cancellationToken) is not { } item
                || item.OwnerId != characterId || item.LocationId != shipId || !IsJettisonable(item.Flag) || item.Id == canId)
            {
                continue;
            }
            await items.MoveAsync(connection, transaction, item.Id, canId, ContainerFlag, cancellationToken);
            moved.Add(new ItemChanged(item with { LocationId = canId, Flag = ContainerFlag }, shipId, item.Flag, item.Quantity));
        }
        await transaction.CommitAsync(cancellationToken);
        return moved;
    }

    /// <summary>How big a can or a wreck is (its type's capacity) and how much is in it.</summary>
    public async Task<Room?> ContainerRoomAsync(long containerId, CancellationToken cancellationToken = default)
    {
        if (await items.GetAsync(containerId, cancellationToken) is not { } container)
        {
            return null;
        }
        await statics.EnsureTypesAsync([container.TypeId], cancellationToken);
        var capacity = statics.Type(container.TypeId)?.Attributes.GetValueOrDefault(CapacityAttribute, 0) ?? 0;
        return new Room(capacity, await VolumeOfAsync(await items.InContainerAsync(containerId, cancellationToken), cancellationToken));
    }

    /// <summary>
    /// Goods go between the ship the character flies, in space, and a can or a wreck beside it: taken from the container into the hold (they are the character's from then on), or
    /// put from the hold into the container (they stay whose they were). A quantity below the stack splits it into a stack of its own where it goes. What does not fit is refused
    /// with the room that is left (<c>NotEnoughCargoSpace</c> for the hold, <c>NoSpaceForThat</c> for a container), and what is not where the call says it is, or is not the
    /// character's to put, is nothing.
    /// </summary>
    public async Task<ShipOutcome> MoveInSpaceAsync(
        long characterId, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Flag != InSpaceFlag
            || await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item)
        {
            return new NothingHappened();
        }
        var ship = snapshot.Ship;
        var toShip = destinationId == ship.Id;
        var fromShip = sourceId == ship.Id;
        if (toShip == fromShip || item.LocationId != sourceId || item.Id == ship.Id || item.Id == destinationId)
        {
            return new NothingHappened();
        }
        var containerId = toShip ? sourceId : destinationId;
        if (await items.GetAsync(containerId, cancellationToken) is not { } container || container.LocationId != ship.LocationId || container.Id == ship.Id)
        {
            return new NothingHappened();
        }
        if (fromShip && (item.OwnerId != characterId || item.Flag != CargoFlag))
        {
            return new NothingHappened(); // what is fitted or not the character's is not put in a can
        }
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (quantity is { } asked && !item.Singleton && (asked <= 0 || asked > item.Quantity))
        {
            return new NothingHappened();
        }
        var splits = quantity is { } some && !item.Singleton && some < item.Quantity;
        var moving = splits ? quantity!.Value : item.Quantity;

        if (toShip)
        {
            if (await RefuseWhatDoesNotFitAsync(snapshot, item, moving, CargoFlag, cancellationToken) is { } full)
            {
                return full;
            }
        }
        else if (await RefuseWhatDoesNotFitContainerAsync(container, item, moving, cancellationToken) is { } crowded)
        {
            return crowded;
        }

        var destinationFlag = toShip ? CargoFlag : ContainerFlag;
        var owner = toShip ? characterId : item.OwnerId;
        Item? split = null;
        if (splits)
        {
            split = await items.MoveStackAsync(connection, transaction, item, moving, owner, destinationId, destinationFlag, cancellationToken);
        }
        else
        {
            await items.MoveToOwnerAsync(connection, transaction, item.Id, owner, destinationId, destinationFlag, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);

        var stacks = split is { } created
            ? new List<ItemChanged> { new(item with { Quantity = item.Quantity - created.Quantity }, item.LocationId, item.Flag, item.Quantity), new(created, 0, created.Flag, 0) }
            : [new ItemChanged(item with { OwnerId = owner, LocationId = destinationId, Flag = destinationFlag }, item.LocationId, item.Flag, item.Quantity)];
        return new ShipChanged(split?.Id ?? item.Id, ship.Id, [], [], stacks);
    }

    /// <summary>A container takes what its capacity allows: what does not fit is refused as the reference does for a container (<c>NoSpaceForThat</c>, with the type and the room).</summary>
    private async Task<ShipRefused?> RefuseWhatDoesNotFitContainerAsync(Item container, Item item, long quantity, CancellationToken cancellationToken)
    {
        if (statics.Type(item.TypeId) is not { } type || await ContainerRoomAsync(container.Id, cancellationToken) is not { } room)
        {
            return null;
        }
        var required = ItemVolumes.Unit(item.TypeId, type.GroupId, item.Singleton, type.Attributes.GetValueOrDefault(VolumeAttribute, 0)) * Math.Max(1, quantity);
        if (required <= 0 || required <= room.Free + RoomTolerance)
        {
            return null;
        }
        return new ShipRefused(ShipRefusal.NoSpaceForThat, ModuleTypeId: item.TypeId, Available: Math.Round(room.Free, 6), Volume: Math.Round(required, 6));
    }
}
