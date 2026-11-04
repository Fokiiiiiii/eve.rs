using Eve.Inventory;

namespace Eve.Ships;

/// <summary>
/// The answer to one in-space compression: the item that was compressed and what it became (the six values of the client's result), and the inventory changes the pilot
/// is told. <c>OutputQuantity</c> is what the compression made, not the size of the stack it joined.
/// </summary>
public sealed record CompressionCommit(
    bool Accepted, string Reason, ShipChanged? Change = null, long SourceItemId = 0, int SourceTypeId = 0, long SourceQuantity = 0,
    long OutputItemId = 0, int OutputTypeId = 0, long OutputQuantity = 0);

public sealed partial class CharacterShips
{
    /// <summary>
    /// Compresses a stack of the ship the character flies in space: the stack is replaced by the same number of units of <paramref name="compressedTypeId"/> in the same hold
    /// (compression keeps the count and makes each unit a hundredth, or a tenth for ice and gas, of the volume: the SDE's volumes), joining a stack of that type the hold
    /// already has. One transaction: the source is never gone without the output. A hold that does not take the output type refuses (the output is a smaller form of the
    /// same material, so this only happens for a hold that never took the source).
    /// </summary>
    public async Task<CompressionCommit> CompressAsync(
        long characterId, long shipId, int solarSystemId, long itemId, int compressedTypeId, CancellationToken cancellationToken = default)
    {
        await statics.EnsureTypesAsync([compressedTypeId], cancellationToken);
        if (statics.Type(compressedTypeId) is not { } outputType)
        {
            return new(false, "NO_OUTPUT_TYPE");
        }
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await LockAsync(connection, transaction, characterId, cancellationToken);
        if (await LoadAsync(characterId, cancellationToken) is not { } snapshot || snapshot.Ship.Id != shipId
            || snapshot.Ship.LocationId != solarSystemId || snapshot.Ship.Flag != InSpaceFlag)
        {
            return new(false, "NO_SHIP");
        }
        if (await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken) is not { } stack || stack.OwnerId != characterId
            || stack.LocationId != shipId || stack.Singleton || stack.Quantity <= 0)
        {
            return new(false, "NOT_IN_SHIP");
        }
        if (!CanAddToShipHold(stack.Flag, outputType, false, stack.Quantity))
        {
            return new(false, "NOT_A_HOLD");
        }
        var join = (await items.InContainerAsync(shipId, cancellationToken))
            .FirstOrDefault(other => other.Id != itemId && other.TypeId == compressedTypeId && other.Flag == stack.Flag && other.OwnerId == characterId && !other.Singleton);
        var changes = new List<ItemChanged>(2);
        Item output;
        if (join is not null && await items.LockAndGetAsync(connection, transaction, join.Id, cancellationToken) is { } joined
            && joined.OwnerId == characterId && joined.LocationId == shipId && joined.Flag == stack.Flag && !joined.Singleton)
        {
            output = joined with { Quantity = joined.Quantity + stack.Quantity };
            await items.SetQuantityAsync(connection, transaction, joined.Id, output.Quantity, cancellationToken);
            changes.Add(new ItemChanged(output, shipId, joined.Flag, joined.Quantity));
        }
        else
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(compressedTypeId, characterId, shipId, stack.Flag, stack.Quantity, false), cancellationToken);
            output = new Item(id, compressedTypeId, characterId, shipId, stack.Flag, stack.Quantity, false, null);
            changes.Add(new ItemChanged(output, solarSystemId, InSpaceFlag, 0));
        }
        await items.DeleteAsync(connection, transaction, itemId, cancellationToken);
        changes.Add(new ItemChanged(stack with { LocationId = JunkLocation }, shipId, stack.Flag, stack.Quantity));
        await transaction.CommitAsync(cancellationToken);
        return new(true, "", new ShipChanged(output.Id, shipId, [], [], changes), stack.Id, stack.TypeId, stack.Quantity, output.Id, compressedTypeId, stack.Quantity);
    }
}
