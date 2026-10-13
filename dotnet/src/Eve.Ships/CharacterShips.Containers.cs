using Eve.Dogma;
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
    public const int CargoContainerTypeId = 23;

    /// <summary>The flag what is in a can (or a wreck) is kept under.</summary>
    public const int ContainerFlag = 0;

    /// <summary>The holds of a ship that can be emptied into a can in space: the cargo hold (the reference lists the specialised holds too).</summary>
    /// <summary>The item flags the pinned client allows a pilot to empty into a can in space.</summary>
    public static bool IsJettisonable(int flag) => flag is CargoFlag or 134 or 135 or 136 or 143 or 149 or 172 or 181 or 182 or 185 or 188;

    /// <summary>The can for cargo a pilot jettisons: an item of the character's, in the solar system, with nothing in it yet.</summary>
    public async Task<Item> CreateCanAsync(long characterId, int solarSystemId, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await items.CreateAsync(connection, transaction, new NewItem(CargoContainerTypeId, characterId, solarSystemId, InSpaceFlag, 1, Singleton: true), cancellationToken);
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


    private bool IsCargoContainerType(TypeDef type)
    {
        if (type.TypeId == CargoContainerTypeId) return true;
        var capacityAttribute = statics.AttributeId("capacity");
        return statics.GroupName(type.GroupId).Contains("container", StringComparison.OrdinalIgnoreCase)
            && capacityAttribute is { } attribute
            && type.Attributes.GetValueOrDefault(attribute, 0) > 0;
    }

    /// <summary>How big a can or a wreck is (its type's capacity) and how much is in it.</summary>
    public async Task<Room?> ContainerRoomAsync(long containerId, CancellationToken cancellationToken = default)
    {
        if (await items.GetAsync(containerId, cancellationToken) is not { Singleton: true, Quantity: 1 } container)
            return null;
        await statics.EnsureTypesAsync([container.TypeId], cancellationToken);
        if (statics.Type(container.TypeId) is not { } type || !IsCargoContainerType(type))
            return null;
        var capacity = statics.AttributeId("capacity") is { } attribute
            ? type.Attributes.GetValueOrDefault(attribute, 0)
            : 0;
        return new Room(capacity, await VolumeOfAsync(await items.InContainerAsync(containerId, cancellationToken), cancellationToken));
    }


    private async Task<bool> IsOwnedDockedInventoryAsync(long itemId, long ownerId, long stationId, CancellationToken cancellationToken)
    {
        var seen = new HashSet<long>();
        var current = itemId;
        for (var depth = 0; depth < 32; depth++)
        {
            if (current == stationId) return true;
            if (!seen.Add(current)
                || await items.GetAsync(current, cancellationToken) is not { OwnerId: var owner, LocationId: var parentLocation, TypeId: var typeId }
                || owner != ownerId)
                return false;
            await statics.EnsureTypesAsync([typeId], cancellationToken);
            if (statics.Type(typeId) is not { } type
                || (!string.Equals(statics.CategoryName(type.CategoryId), "Ship", StringComparison.OrdinalIgnoreCase)
                    && !IsCargoContainerType(type)))
                return false;
            current = parentLocation;
        }
        return false;
    }


    public async Task<bool> IsOwnedDockedContainerAsync(long containerId, long ownerId, long stationId, CancellationToken cancellationToken = default)
    {
        if (await items.GetAsync(containerId, cancellationToken) is not { OwnerId: var owner, Singleton: true, Quantity: 1, TypeId: var typeId }
            || owner != ownerId)
            return false;
        await statics.EnsureTypesAsync([typeId], cancellationToken);
        return statics.Type(typeId) is { } type && IsCargoContainerType(type)
            && await IsOwnedDockedInventoryAsync(containerId, ownerId, stationId, cancellationToken);
    }


    private async Task<bool> IsNestedUnderAsync(long itemId, long ancestorId, CancellationToken cancellationToken)
    {
        var current = itemId;
        for (var depth = 0; depth < 32; depth++)
        {
            if (current == ancestorId) return true;
            if (current <= 0 || await items.GetAsync(current, cancellationToken) is not { } item)
                return false;
            current = item.LocationId;
        }
        return false;
    }

    /// <summary>Moves an owned item between the docked hangar and an owned cargo container in one transaction.</summary>
    public async Task<ShipOutcome> MoveContainerAsync(
        long characterId, long itemId, long sourceLocationId, long destinationLocationId,
        long? quantity, int? requestedFlag = null, CancellationToken cancellationToken = default, long? sourceOwnerId = null, long? destinationOwnerId = null)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await characters.GetAsync(characterId, cancellationToken) is not { StationId: { } station }
            || station <= 0
            || await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item
            || (item.OwnerId != characterId && item.OwnerId != sourceOwnerId) || item.LocationId != sourceLocationId
            || sourceLocationId == destinationLocationId || item.Id == sourceLocationId || item.Id == destinationLocationId
            || sourceLocationId != station && !await IsOwnedDockedInventoryAsync(sourceLocationId, sourceOwnerId ?? characterId, station, cancellationToken))
            return new NothingHappened();

        var toHangar = destinationLocationId == station;
        var destinationItemOwnerId = destinationOwnerId ?? characterId;
        Item? container = null;
        var destinationFlag = ItemFlags.Hangar;
        if (toHangar)
        {
            if (requestedFlag is not null and not 0 and not ItemFlags.Hangar)
                return new NothingHappened();
        }
        else
        {
            if (requestedFlag is not null and not 0
                || await items.LockAndGetAsync(connection, transaction, destinationLocationId, cancellationToken) is not { } target
                || (target.OwnerId != characterId && target.OwnerId != destinationOwnerId)
                || !target.Singleton || target.Quantity != 1
                || !await IsOwnedDockedInventoryAsync(target.Id, destinationOwnerId ?? characterId, station, cancellationToken)
                || target.Id == item.Id)
                return new NothingHappened();
            await statics.EnsureTypesAsync([target.TypeId], cancellationToken);
            if (statics.Type(target.TypeId) is not { } targetType || !IsCargoContainerType(targetType)
                || await IsNestedUnderAsync(target.Id, item.Id, cancellationToken))
                return new NothingHappened();
            await items.LockContainerAsync(connection, transaction, target.Id, cancellationToken);
            container = target;
            destinationFlag = ContainerFlag;
        }

        if (quantity is { } asked && !item.Singleton && (asked <= 0 || asked > item.Quantity))
            return new NothingHappened();
        var splits = quantity is { } some && !item.Singleton && some < item.Quantity;
        var moving = splits ? quantity!.Value : item.Quantity;
        if (container is not null && await RefuseWhatDoesNotFitContainerAsync(container, item, moving, cancellationToken) is { } full)
            return full;

        Item? split = null;
        if (splits)
            split = await items.MoveStackAsync(connection, transaction, item, moving, destinationItemOwnerId, destinationLocationId, destinationFlag, cancellationToken);
        else if (item.OwnerId == destinationItemOwnerId)
            await items.MoveAsync(connection, transaction, item.Id, destinationLocationId, destinationFlag, cancellationToken);
        else
            await items.MoveToOwnerAsync(connection, transaction, item.Id, destinationItemOwnerId, destinationLocationId, destinationFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var stacks = split is { } created
            ? new List<ItemChanged> { new(item with { Quantity = item.Quantity - created.Quantity }, item.LocationId, item.Flag, item.Quantity), new(created, 0, created.Flag, 0) }
            : [new ItemChanged(item with { OwnerId = destinationItemOwnerId, LocationId = destinationLocationId, Flag = destinationFlag }, item.LocationId, item.Flag, item.Quantity)];
        return new ShipChanged(split?.Id ?? item.Id, 0, [], [], stacks);
    }

    /// <summary>
    /// Goods go between the ship the character flies, in space, and a can or a wreck beside it: taken from the container into the hold (they are the character's from then on), or
    /// put from the hold into the container (they stay whose they were). A quantity below the stack splits it into a stack of its own where it goes. What does not fit is refused
    /// with the room that is left (<c>NotEnoughCargoSpace</c> for the hold, <c>NoSpaceForThat</c> for a container), and what is not where the call says it is, or is not the
    /// character's to put, is nothing.
    /// </summary>
    public async Task<ShipOutcome> MoveInSpaceAsync(
        long characterId, long itemId, long sourceId, long destinationId, long? quantity, int? requestedFlag = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Flag != InSpaceFlag
            || await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } item)
            return new NothingHappened();
        var ship = snapshot.Ship;
        var toShip = destinationId == ship.Id;
        var fromShip = sourceId == ship.Id;
        if (toShip == fromShip || item.LocationId != sourceId || item.Id == ship.Id || item.Id == destinationId)
            return new NothingHappened();
        var containerId = toShip ? sourceId : destinationId;
        if (await items.GetAsync(containerId, cancellationToken) is not { } container || container.LocationId != ship.LocationId || container.Id == ship.Id)
            return new NothingHappened();
        if (fromShip && (item.OwnerId != characterId || !IsJettisonable(item.Flag)))
            return new NothingHappened();
        await statics.EnsureTypesAsync([item.TypeId], cancellationToken);
        if (quantity is { } asked && !item.Singleton && (asked <= 0 || asked > item.Quantity))
            return new NothingHappened();
        var splits = quantity is { } some && !item.Singleton && some < item.Quantity;
        var moving = splits ? quantity!.Value : item.Quantity;
        var destinationFlag = toShip ? requestedFlag ?? CargoFlag : ContainerFlag;
        if (toShip && !IsShipHoldFlag(destinationFlag))
            return new NothingHappened();
        if (toShip && statics.Type(item.TypeId) is { } type
            && !CanAddToShipHold(destinationFlag, type, item.Singleton, moving))
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);
        if (toShip && statics.Type(item.TypeId) is null && destinationFlag != CargoFlag)
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);
        if (toShip && (destinationFlag is ShipMaintenanceBayFlag or FrigateEscapeBayFlag)
            && !await ShipContentsAllowedInBayAsync(item, destinationFlag, snapshot.World, characterId, cancellationToken))
            return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);

        if (toShip)
        {
            if (await RefuseWhatDoesNotFitAsync(snapshot, item, moving, destinationFlag, cancellationToken) is { } full)
                return full;
        }
        else if (await RefuseWhatDoesNotFitContainerAsync(container, item, moving, cancellationToken) is { } crowded)
            return crowded;

        var owner = toShip ? characterId : item.OwnerId;
        Item? split = null;
        if (splits)
            split = await items.MoveStackAsync(connection, transaction, item, moving, owner, destinationId, destinationFlag, cancellationToken);
        else
            await items.MoveToOwnerAsync(connection, transaction, item.Id, owner, destinationId, destinationFlag, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var stacks = split is { } created
            ? new List<ItemChanged> { new(item with { Quantity = item.Quantity - created.Quantity }, item.LocationId, item.Flag, item.Quantity), new(created, 0, created.Flag, 0) }
            : [new ItemChanged(item with { OwnerId = owner, LocationId = destinationId, Flag = destinationFlag }, item.LocationId, item.Flag, item.Quantity)];
        return new ShipChanged(split?.Id ?? item.Id, ship.Id, [], [], stacks);
    }

    /// <summary>
    /// "Loot All": everything of <paramref name="itemIds"/> that is in a can or a wreck beside the ship goes into the hold of the ship the character flies, as stacks whole (they are the
    /// character's from then on). It is all or nothing for room: what the batch takes in all is checked against what is left of the hold, and when it does not fit nothing moves
    /// (<c>NotEnoughCargoSpace</c> with the room left and the room it takes). An id that is not in the container is left out of the batch; when none is, nothing happens.
    /// </summary>
    public async Task<ShipOutcome> MoveAllInSpaceAsync(long characterId, IReadOnlyList<long> itemIds, long containerId, int? requestedFlag = null,
        CancellationToken cancellationToken = default, long? quantity = null)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);

        if (quantity is <= 0 || await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Flag != InSpaceFlag
            || await items.GetAsync(containerId, cancellationToken) is not { } container || container.LocationId != snapshot.Ship.LocationId || container.Id == snapshot.Ship.Id)
            return new NothingHappened();
        var ship = snapshot.Ship;
        var destinationFlag = requestedFlag ?? CargoFlag;
        if (!IsShipHoldFlag(destinationFlag)) return new NothingHappened();
        var batch = new List<Item>();
        foreach (var id in itemIds.Distinct())
        {
            if (id != ship.Id && id != containerId && await items.LockAndGetAsync(connection, transaction, id, cancellationToken) is { } item
                && item.LocationId == containerId && (item.Singleton || quantity is null || quantity <= item.Quantity))
                batch.Add(item);
        }
        if (batch.Count == 0) return new NothingHappened();
        await statics.EnsureTypesAsync(batch.Select(i => i.TypeId), cancellationToken);
        long MovingQuantity(Item item) => item.Singleton ? item.Quantity : quantity ?? item.Quantity;
        foreach (var item in batch)
        {
            if (statics.Type(item.TypeId) is not { } type && destinationFlag != CargoFlag)
                return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);
            if (statics.Type(item.TypeId) is { } itemType
                && (!CanAddToShipHold(destinationFlag, itemType, item.Singleton, MovingQuantity(item))
                    || !await ShipContentsAllowedInBayAsync(item, destinationFlag, snapshot.World, characterId, cancellationToken)))
                return new ShipRefused(ShipRefusal.NotAllowedInHold, ModuleTypeId: item.TypeId, ShipTypeId: ship.TypeId);
        }
        // Capacity is checked once for the units that will actually move, before any stack is changed.
        var movingBatch = batch.Select(item => item with { Quantity = MovingQuantity(item) }).ToArray();
        if (await RefuseWhatDoesNotFitTogetherAsync(snapshot, movingBatch, destinationFlag, cancellationToken) is { } full)
            return full;
        var stacks = new List<ItemChanged>();
        long? firstMovedId = null;
        foreach (var item in batch)
        {
            var amount = MovingQuantity(item);
            Item moved;
            if (!item.Singleton && amount < item.Quantity)
            {
                moved = await items.MoveStackAsync(connection, transaction, item, amount, characterId, ship.Id, destinationFlag, cancellationToken);
                stacks.Add(new ItemChanged(item with { Quantity = item.Quantity - amount }, item.LocationId, item.Flag, item.Quantity));
                stacks.Add(new ItemChanged(moved, 0, moved.Flag, 0));
            }
            else
            {
                await items.MoveToOwnerAsync(connection, transaction, item.Id, characterId, ship.Id, destinationFlag, cancellationToken);
                moved = item with { OwnerId = characterId, LocationId = ship.Id, Flag = destinationFlag };
                stacks.Add(new ItemChanged(moved, item.LocationId, item.Flag, item.Quantity));
            }
            firstMovedId ??= moved.Id;
        }
        await transaction.CommitAsync(cancellationToken);
        return new ShipChanged(firstMovedId!.Value, ship.Id, [], [], stacks);
    }

    /// <summary>What a batch takes in all is more than is left of a hold: refused whole, with the room that is left and the room the batch takes.</summary>
    private async Task<ShipRefused?> RefuseWhatDoesNotFitTogetherAsync(ShipSnapshot snapshot, IReadOnlyList<Item> batch, int flag, CancellationToken cancellationToken)
    {
        if (CapacityAttributeOf(flag) is not { } attribute)
        {
            return null;
        }
        var contents = (await items.InContainerAsync(snapshot.Ship.Id, cancellationToken))
            .Where(i => i.Flag == flag)
            .ToArray();
        if (flag == FrigateEscapeBayFlag)
        {
            var capacity = snapshot.World.Get(snapshot.Ship.Id, attribute);
            var used = contents.Sum(item => Math.Max(1, item.Quantity));
            var requiredShips = batch.Sum(item => Math.Max(1, item.Quantity));
            var room = new Room(capacity, used);
            if (requiredShips <= room.Free)
                return null;
            return new ShipRefused(ShipRefusal.NotEnoughCargoSpace, ModuleTypeId: batch[0].TypeId, ShipTypeId: snapshot.Ship.TypeId,
                Available: Math.Max(0, room.Free), Volume: requiredShips);
        }
        var required = batch.Sum(item => statics.Type(item.TypeId) is { } type
            ? ItemVolumes.Unit(item.TypeId, type.GroupId, item.Singleton, ItemVolumeOf(type)) * Math.Max(1, item.Quantity)
            : 0);
        var roomByVolume = new Room(snapshot.World.Get(snapshot.Ship.Id, attribute), await VolumeOfAsync(contents, cancellationToken));
        if (required <= 0 || required <= roomByVolume.Free + RoomTolerance)
        {
            return null;
        }
        return new ShipRefused(ShipRefusal.NotEnoughCargoSpace, ModuleTypeId: batch[0].TypeId, ShipTypeId: snapshot.Ship.TypeId, Available: Math.Round(roomByVolume.Free, 6), Volume: Math.Round(required, 6));
    }

    /// <summary>A container takes what its capacity allows: what does not fit is refused as the reference does for a container (<c>NoSpaceForThat</c>, with the type and the room).</summary>
    private async Task<ShipRefused?> RefuseWhatDoesNotFitContainerAsync(Item container, Item item, long quantity, CancellationToken cancellationToken)
    {
        if (statics.Type(item.TypeId) is not { } type || await ContainerRoomAsync(container.Id, cancellationToken) is not { } room)
        {
            return null;
        }
        var required = ItemVolumes.Unit(item.TypeId, type.GroupId, item.Singleton, ItemVolumeOf(type)) * Math.Max(1, quantity);
        if (required <= 0 || required <= room.Free + RoomTolerance)
        {
            return null;
        }
        return new ShipRefused(ShipRefusal.NoSpaceForThat, ModuleTypeId: item.TypeId, Available: Math.Round(room.Free, 6), Volume: Math.Round(required, 6));
    }
}
