using Eve.Dogma;
using Eve.Inventory;
using Eve.Structures;
using Npgsql;

namespace Eve.Host;

/// <summary>
/// The fuel bay of a structure over the item store: the fuel blocks (SDE group 1136) at the structure in its fuel bay (flag 172, EveJS
/// <c>STRUCTURE_FUEL_BAY_FLAG</c>). A burn takes the oldest stacks first, inside the caller's transaction: a stack is deleted or shrunk.
/// </summary>
public sealed class InventoryStructureFuelBay(PgItemStore items, DogmaStatic dogma) : IStructureFuelBay
{
    private const int FuelBlockGroup = 1136;
    private const int StructureFuelBayFlag = 172;

    public async Task<long> QuantityAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, long structureId, CancellationToken cancellationToken) =>
        FuelBlocks(await items.InContainerAsync(connection, transaction, structureId, cancellationToken)).Sum(stack => stack.Quantity);

    public async Task<long> TakeAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, long structureId, long quantity, CancellationToken cancellationToken)
    {
        var taken = 0L;
        foreach (var stack in FuelBlocks(await items.InContainerAsync(connection, transaction, structureId, cancellationToken)))
        {
            if (taken >= quantity)
                break;
            var locked = await items.LockAndGetAsync(connection, transaction, stack.Id, cancellationToken);
            if (locked is null || locked.Quantity <= 0)
                continue;
            var part = Math.Min(quantity - taken, locked.Quantity);
            if (part == locked.Quantity)
                await items.DeleteAsync(connection, transaction, stack.Id, cancellationToken);
            else
                await items.SetQuantityAsync(connection, transaction, stack.Id, locked.Quantity - part, cancellationToken);
            taken += part;
        }
        return taken;
    }

    private IEnumerable<Item> FuelBlocks(IEnumerable<Item> contents) =>
        contents.Where(stack => stack.Flag == StructureFuelBayFlag && dogma.Type(stack.TypeId)?.GroupId == FuelBlockGroup);
}
