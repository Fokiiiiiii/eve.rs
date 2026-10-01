using Eve.Inventory;

namespace Eve.Ships;

/// <summary>What is put in a wreck that is made: a type, how many of it, and whether it is a unit of its own (a module, a ship) rather than a stack.</summary>
public sealed record WreckContent(int TypeId, long Quantity, bool Singleton);

/// <summary>A wreck that was made in space with what it holds (as it is in the wreck).</summary>
public sealed record MadeWreck(Item Wreck, IReadOnlyList<Item> Contents);

public sealed partial class CharacterShips
{
    /// <summary>
    /// A wreck is made in a solar system with what it holds, in one transaction: the wreck an item of <paramref name="ownerId"/> in space and what it holds items of the same owner in it
    /// (at the hangar flag, as what a destroyed ship leaves is). A ship that was destroyed leaves what the rules of its death decide (<see cref="DestroyShipAsync"/>); this is for the wreck of
    /// something that was never a character's, an NPC, with what its loot table gave.
    /// </summary>
    public async Task<MadeWreck> LeaveWreckAsync(int wreckTypeId, long ownerId, int solarSystemId, IReadOnlyList<WreckContent> contents, CancellationToken cancellationToken = default)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var id = await items.CreateAsync(connection, transaction, new NewItem(wreckTypeId, ownerId, solarSystemId, InSpaceFlag, 1, true), cancellationToken);
        var held = new List<Item>(contents.Count);
        foreach (var content in contents)
        {
            var quantity = content.Singleton ? 1 : Math.Max(1, content.Quantity);
            var inside = await items.CreateAsync(connection, transaction, new NewItem(content.TypeId, ownerId, id, ItemFlags.Hangar, quantity, content.Singleton), cancellationToken);
            held.Add(new Item(inside, content.TypeId, ownerId, id, ItemFlags.Hangar, quantity, content.Singleton, null));
        }
        await transaction.CommitAsync(cancellationToken);
        return new MadeWreck(new Item(id, wreckTypeId, ownerId, solarSystemId, InSpaceFlag, 1, true, null), held);
    }
}
