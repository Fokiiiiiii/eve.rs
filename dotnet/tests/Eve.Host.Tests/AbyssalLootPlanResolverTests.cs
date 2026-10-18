using Eve.Content;
using Eve.Data;
using Eve.Inventory;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class AbyssalLootPlanResolverTests
{
    [PgFact]
    public async Task Sde_blueprint_classification_freezes_each_copy_run_count_and_reuses_it_on_retry()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")
            ?? throw new InvalidOperationException("EVE_PG_URL is not set");
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var sde = new SdeReader(source);
        var categoryId = await AbyssalLootMaterializer.LoadBlueprintCategoryIdAsync(sde);
        var itemTypes = new ItemTypeCatalog(sde);
        var blueprintTypeId = 48097; // Tetryon Exotic Plasma S Blueprint, verified in the matching SDE.
        var normalTypeId = 34;        // Tritanium, verified in the matching SDE.

        var plan = Plan(
            new AbyssalLootPlan(blueprintTypeId, 2),
            new AbyssalLootPlan(normalTypeId, 4));
        var draws = new Queue<int>([2, 5]);

        var frozen = await AbyssalLootPlanResolver.FreezeBlueprintCopiesAsync(
            plan,
            itemTypes,
            categoryId,
            draws.Dequeue);

        Assert.True(frozen.Rooms[0].Loot[0].IsBlueprintCopy);
        Assert.Equal(new[] { 2, 5 }, frozen.Rooms[0].Loot[0].BlueprintRunsPerCopy);
        Assert.False(frozen.Rooms[0].Loot[1].IsBlueprintCopy);
        Assert.Null(frozen.Rooms[0].Loot[1].BlueprintRunsPerCopy);
        Assert.Empty(draws);

        var retried = await AbyssalLootPlanResolver.FreezeBlueprintCopiesAsync(
            frozen,
            itemTypes,
            categoryId,
            () => throw new InvalidOperationException("a persisted plan must not draw new runs"));
        Assert.Equal(new[] { 2, 5 }, retried.Rooms[0].Loot[0].BlueprintRunsPerCopy);
    }

    private static AbyssalRunPlan Plan(params AbyssalLootPlan[] firstRoomLoot)
    {
        var rooms = new[]
        {
            new AbyssalRoomPlan(1, Array.Empty<AbyssalNpcSpawnPlan>(), firstRoomLoot, Array.Empty<AbyssalObjectivePlan>(), "environment-1")
                { NebulaGraphicId = 6001 },
            new AbyssalRoomPlan(2, Array.Empty<AbyssalNpcSpawnPlan>(), Array.Empty<AbyssalLootPlan>(), Array.Empty<AbyssalObjectivePlan>(), "environment-2")
                { NebulaGraphicId = 6002 },
            new AbyssalRoomPlan(3, Array.Empty<AbyssalNpcSpawnPlan>(), Array.Empty<AbyssalLootPlan>(), Array.Empty<AbyssalObjectivePlan>(), "environment-3")
                { NebulaGraphicId = 6003 },
        };
        return new AbyssalRunPlan(
            1,
            "dark",
            6001,
            1,
            Array.AsReadOnly(rooms))
        {
            SpaceLocationId = 30_000_142,
        };
    }
}
