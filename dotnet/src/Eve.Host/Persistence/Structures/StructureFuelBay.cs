using Eve.Dogma;
using Eve.Events;
using Eve.Ships;
using Eve.Inventory;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The fuel bay of a structure over the item store: the fuel blocks (SDE group 1136) at the structure in its fuel bay (flag 172, EveJS
/// <c>STRUCTURE_FUEL_BAY_FLAG</c>). A burn takes the oldest stacks first, inside the caller's transaction: a stack is deleted or shrunk.
/// </summary>
public sealed class InventoryStructureFuelBay(PgItemStore items, DogmaStatic dogma, EventBus events) : IStructureFuelBay
{
    private const int FuelBlockGroup = 1136;
    private const int StructureFuelBayFlag = 172;

    public async Task<long> QuantityAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken) =>
        FuelBlocks(await items.InContainerAsync(connection, transaction, structureId, cancellationToken)).Sum(stack => stack.Quantity);

    public async Task<StructureFuelTake> TakeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long structureId,
        long quantity,
        bool allowPartial,
        CancellationToken cancellationToken)
    {
        if (quantity <= 0)
            return new StructureFuelTake(0, []);

        var lockedStacks = new List<Item>();
        foreach (var stack in FuelBlocks(await items.InContainerAsync(connection, transaction, structureId, cancellationToken)))
        {
            if (await items.LockAndGetAsync(connection, transaction, stack.Id, cancellationToken) is { Quantity: > 0 } locked)
                lockedStacks.Add(locked);
        }

        var available = lockedStacks.Sum(stack => stack.Quantity);
        if (!allowPartial && available < quantity)
            return new StructureFuelTake(0, []);

        var taken = 0L;
        var remaining = Math.Min(quantity, available);
        var itemChanges = new List<ItemChanged>();
        var fuelChanges = new List<StructureFuelItemChange>();
        var left = lockedStacks.ToDictionary(stack => stack.Id, stack => stack.Quantity);
        foreach (var locked in lockedStacks)
        {
            if (remaining <= 0)
                break;
            var part = Math.Min(remaining, locked.Quantity);
            Item after;
            if (part == locked.Quantity)
            {
                await items.DeleteAsync(connection, transaction, locked.Id, cancellationToken);
                after = locked with { LocationId = Eve.Ships.CharacterShips.JunkLocation };
            }
            else
            {
                after = locked with { Quantity = locked.Quantity - part };
                await items.SetQuantityAsync(connection, transaction, locked.Id, after.Quantity, cancellationToken);
            }
            itemChanges.Add(new ItemChanged(after, structureId, locked.Flag, locked.Quantity));
            left[locked.Id] = locked.Quantity - part;
            fuelChanges.Add(new StructureFuelItemChange(
                after.Id,
                after.TypeId,
                after.OwnerId,
                after.LocationId,
                after.Flag,
                after.Quantity,
                after.Singleton,
                structureId,
                locked.Flag,
                locked.Quantity));
            taken += part;
            remaining -= part;
        }
        if (itemChanges.Count > 0)
        {
            await events.PublishInTransactionAsync(
                connection,
                transaction,
                StructureFuelBayNotifications.Topic,
                new StructureFuelBayItemsChanged(structureId, itemChanges, lockedStacks
                    .GroupBy(stack => stack.TypeId)
                    .Select(group => new StructureFuelRemaining(group.Key, group.Sum(stack => left[stack.Id])))
                    .Where(entry => entry.Quantity > 0)
                    .ToArray()),
                $"structure:{structureId}",
                cancellationToken);
        }
        return new StructureFuelTake(taken, fuelChanges);
    }

    private IEnumerable<Item> FuelBlocks(IEnumerable<Item> contents) =>
        contents.Where(stack => stack.Flag == StructureFuelBayFlag && dogma.Type(stack.TypeId)?.GroupId == FuelBlockGroup);
}
