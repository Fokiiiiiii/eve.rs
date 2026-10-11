using Eve.Inventory;
using Npgsql;

namespace Eve.Ships;

public sealed partial class CharacterShips
{
    /// <summary>
    /// Puts items in a ship's cargo hold, inside the caller's transaction (the rewards of a CRAB's completed scan): a stack joins the first stack of its kind there, and
    /// otherwise one is made. Answers what the client is told changed; null when the ship is not the character's, so nothing is put in.
    /// </summary>
    public async Task<IReadOnlyList<ItemChanged>?> GrantToShipCargoAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long characterId, long shipId, int typeId, long quantity, CancellationToken cancellationToken)
    {
        if (await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken) is not { } ship || ship.OwnerId != characterId)
        {
            return null;
        }
        var changes = new List<ItemChanged>();
        var existing = (await items.InLocationAsync(shipId, characterId, cancellationToken))
            .Where(i => i.Flag == CargoFlag && !i.Singleton && i.TypeId == typeId)
            .OrderBy(i => i.Id)
            .FirstOrDefault();
        if (existing is not null && await items.LockAndGetAsync(connection, transaction, existing.Id, cancellationToken) is { } stack
            && (stack.TypeId, stack.OwnerId, stack.LocationId, stack.Flag, stack.Singleton) == (typeId, characterId, shipId, CargoFlag, false))
        {
            // the stack is locked and grown from what the lock finds
            var grown = stack with { Quantity = stack.Quantity + quantity };
            await items.SetQuantityAsync(connection, transaction, stack.Id, grown.Quantity, cancellationToken);
            changes.Add(new ItemChanged(grown, stack.LocationId, stack.Flag, stack.Quantity));
        }
        else
        {
            var id = await items.CreateAsync(connection, transaction, new NewItem(typeId, characterId, shipId, CargoFlag, quantity), cancellationToken);
            changes.Add(new ItemChanged(new Item(id, typeId, characterId, shipId, CargoFlag, quantity, false, null), 0, 0, quantity));
        }
        return changes;
    }
}
