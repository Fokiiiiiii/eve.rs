using Eve.Inventory;

namespace Eve.Characters.Tests;

public class InventoryTests
{
    private static async Task<long> Create(PgSchemas s, NewItem item)
    {
        await using var connection = await s.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await s.Items.CreateAsync(connection, transaction, item);
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task An_item_round_trips_and_ids_start_where_the_reference_started_them()
    {
        await using var s = await PgSchemas.CreateAsync();
        var id = await Create(s, new NewItem(TypeId: 601, OwnerId: 140_000_001, LocationId: 60000610, Flag: ItemFlags.Hangar, Singleton: true));
        Assert.Equal(1_990_000_000, id);
        Assert.Equal(new Item(id, 601, 140_000_001, 60000610, 4, 1, true, null), await s.Items.GetAsync(id));
        var stack = await Create(s, new NewItem(34, 140_000_001, 60000610, ItemFlags.Hangar, Quantity: 5000, CustomName: "Tritanium"));
        Assert.Equal(new Item(stack, 34, 140_000_001, 60000610, 4, 5000, false, "Tritanium"), await s.Items.GetAsync(stack));
        Assert.Null(await s.Items.GetAsync(1));
    }

    [PgFact]
    public async Task Items_are_listed_by_owner()
    {
        await using var s = await PgSchemas.CreateAsync();
        var a = await Create(s, new NewItem(601, 1, 10, 4));
        await Create(s, new NewItem(602, 2, 10, 4));
        var b = await Create(s, new NewItem(603, 1, 10, 4));
        Assert.Equal([a, b], (await s.Items.OwnedByAsync(1)).Select(i => i.Id));
        Assert.Empty(await s.Items.OwnedByAsync(3));
    }

    [PgFact]
    public async Task A_rolled_back_item_does_not_exist()
    {
        await using var s = await PgSchemas.CreateAsync();
        await using (var connection = await s.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await s.Items.CreateAsync(connection, transaction, new NewItem(601, 1, 10, 4));
            await transaction.RollbackAsync();
        }
        Assert.Empty(await s.Items.OwnedByAsync(1));
    }
}
