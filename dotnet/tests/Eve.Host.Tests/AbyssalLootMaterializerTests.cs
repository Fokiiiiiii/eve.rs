using Eve.Data;
using Eve.Content;
using Eve.Inventory;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalLootMaterializerTests
{
    private const long Owner = 8101;
    private const long SourceLocation = 8201;
    private const int TetryonExoticPlasmaSBlueprint = 48097;
    private const int Tritanium = 34;

    [PgFact]
    public async Task Blueprint_loot_creates_individual_copies_with_the_persisted_runs_and_zero_efficiencies()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "abyssal_loot_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source, schema);

        var items = new PgItemStore(source, schema);
        var sde = new SdeReader(source);
        var types = new ItemTypeCatalog(sde);
        var blueprintCategoryId = await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde);
        var blueprintType = await types.GetAsync(TetryonExoticPlasmaSBlueprint);
        Assert.NotNull(blueprintType);
        Assert.Equal(blueprintCategoryId, blueprintType.CategoryId);
        var materializer = new AbyssalLootMaterializer(items, types, blueprintCategoryId);
        var loot = new AbyssalLootPlan[]
        {
            new(TetryonExoticPlasmaSBlueprint, 2)
            {
                IsBlueprintCopy = true,
                BlueprintRunsPerCopy = [2, 5],
            },
            new(Tritanium, 7),
        };

        long containerId;
        IReadOnlyList<long> created;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            containerId = await items.CreateAsync(connection, transaction,
                new NewItem(Tritanium, Owner, SourceLocation, Flag: 0));
            created = await materializer.MaterializeAsync(
                connection, transaction, Owner, containerId, loot);
            Assert.Equal(3, created.Count);
            await transaction.CommitAsync();
        }

        var firstCopy = (await items.GetAsync(created[0]))!;
        var secondCopy = (await items.GetAsync(created[1]))!;
        var stack = (await items.GetAsync(created[2]))!;
        Assert.Equal(-2, firstCopy.Quantity);
        Assert.True(firstCopy.Singleton);
        Assert.True(firstCopy.BlueprintCopy);
        Assert.False(firstCopy.BlueprintLocked);
        Assert.Equal(2, firstCopy.BlueprintRuns);
        Assert.Equal(0, firstCopy.MaterialEfficiency);
        Assert.Equal(0, firstCopy.TimeEfficiency);
        Assert.Equal(-2, secondCopy.Quantity);
        Assert.True(secondCopy.Singleton);
        Assert.True(secondCopy.BlueprintCopy);
        Assert.Equal(5, secondCopy.BlueprintRuns);
        Assert.Equal(7, stack.Quantity);
        Assert.False(stack.Singleton);
        Assert.False(stack.BlueprintCopy);
        Assert.Null(stack.BlueprintRuns);
    }

    [PgFact]
    public async Task Invalid_blueprint_runs_are_rejected_before_any_inventory_row_is_created()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var schema = "abyssal_loot_invalid_" + Guid.NewGuid().ToString("N");
        await PgItemStore.MigrateAsync(source, schema);

        var items = new PgItemStore(source, schema);
        var sde = new SdeReader(source);
        var materializer = new AbyssalLootMaterializer(
            items,
            new ItemTypeCatalog(sde),
            await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde));
        long containerId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            containerId = await items.CreateAsync(connection, transaction,
                new NewItem(Tritanium, Owner, SourceLocation, Flag: 0));
            await transaction.CommitAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                materializer.MaterializeAsync(
                    connection,
                    transaction,
                    Owner,
                    containerId,
                    [new AbyssalLootPlan(TetryonExoticPlasmaSBlueprint, 2)
                    {
                        IsBlueprintCopy = true,
                        BlueprintRunsPerCopy = [3],
                    }]));
            await transaction.RollbackAsync();
        }

        Assert.Empty(await items.InLocationAsync(containerId, Owner));
    }
}
