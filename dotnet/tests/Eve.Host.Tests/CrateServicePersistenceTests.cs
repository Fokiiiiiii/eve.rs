using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class CrateServicePersistenceTests
{
    [Fact]
    public async Task Opening_and_claiming_a_crate_updates_the_inventory_store()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(connectionString!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());

        var typeRows = await sde.ReadAsync("types");
        var crateType = typeRows.First(row =>
            row.Doc.TryGetProperty("groupID", out var group) && group.GetInt32() is 1194 or 1818);
        const long characterId = 90000001;
        const long destinationId = 60000001;
        const int destinationFlag = 4;
        const string customInfo = "{\"evejsCrateLoot\":[[34,6]],\"evejsCrateFittings\":[699]}";
        var schema = "crate_test_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgItemStore.MigrateAsync(source, schema);
            var items = new PgItemStore(source, schema);
            var itemTypes = new ItemTypeCatalog(sde);
            var backend = new PersistenceCrateServiceBackend(source, items, itemTypes);
            var crateTypeId = checked((int)crateType.Key);
            long crateId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                crateId = await items.CreateAsync(connection, transaction,
                    new NewItem(crateTypeId, characterId, destinationId, destinationFlag,
                        Quantity: 3, CustomName: "Reward Crate", CustomInfo: customInfo));
                await transaction.CommitAsync();
            }

            var crate = Assert.IsType<CrateItem>(await backend.GetItemAsync(crateId, CancellationToken.None));
            Assert.Equal("Reward Crate", crate.Name);
            Assert.Equal(customInfo, crate.CustomInfo);
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                var split = await items.MoveStackAsync(connection, transaction,
                    Assert.IsType<Eve.Inventory.Item>(await items.GetAsync(crateId)),
                    1, characterId, destinationId + 1, destinationFlag, CancellationToken.None);
                Assert.Equal(customInfo, split.CustomInfo);
                await transaction.CommitAsync();
            }
            var opened = await backend.OpenAsync(
                crateId, characterId, 1, destinationId, destinationFlag,
                -9000000000001, 0,
                [new CrateLootEntry(34, 6), new CrateLootEntry(34, 3, Singleton: true)],
                CancellationToken.None);
            var rewards = Assert.IsType<CrateOpenResult>(opened).Items;
            Assert.Equal(4, rewards.Count);
            var reward = Assert.Single(rewards, item => item.Quantity == 6);
            var singletons = rewards.Where(item => item.ItemId != reward.ItemId).ToArray();
            Assert.Equal(3, singletons.Length);
            foreach (var singleton in singletons)
            {
                var stored = Assert.IsType<Eve.Inventory.Item>(await items.GetAsync(singleton.ItemId));
                Assert.True(stored.Singleton);
                Assert.Equal(1, stored.Quantity);
            }

            Assert.Equal(1, (await items.GetAsync(crateId))!.Quantity);
            var claimed = await backend.ClaimLootAsync(
                reward.ItemId, characterId, -9000000000001, destinationId, destinationFlag, CancellationToken.None);
            Assert.NotNull(claimed);
            Assert.Equal(destinationId, (await items.GetAsync(reward.ItemId))!.LocationId);
            Assert.Equal(destinationFlag, (await items.GetAsync(reward.ItemId))!.Flag);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Crate_custom_info_and_claim_fitting_round_trip_through_postgres_and_gamestore()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(connectionString!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());

        var typeRows = await sde.ReadAsync("types");
        var shipGroupIds = (await sde.ReadAsync("groups"))
            .Where(row => row.Doc.TryGetProperty("categoryID", out var category) && category.GetInt32() == 6)
            .Select(row => row.Key)
            .ToHashSet();
        var crateTypeId = checked((int)typeRows.First(row =>
            row.Doc.TryGetProperty("groupID", out var group) && group.GetInt32() is 1194 or 1818).Key);
        var shipTypeId = checked((int)typeRows.First(row =>
            row.Doc.TryGetProperty("groupID", out var group) && shipGroupIds.Contains(group.GetInt64())).Key);
        const long characterId = 90000011;
        const long fittingOwnerId = 90000012;
        const long destinationId = 60000011;
        const int destinationFlag = 4;
        const int sourceFittingId = 699;
        const int savedFittingId = 700;
        const string customInfo = "{\"evejsCrateLoot\":[[34,6]],\"evejsCrateFittings\":[699]}";
        var itemSchema = "crate_fit_inv_" + Guid.NewGuid().ToString("N")[..12];
        var storeSchema = "crate_fit_gs_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgItemStore.MigrateAsync(source, itemSchema);
            var items = new PgItemStore(source, itemSchema);
            var itemTypes = new ItemTypeCatalog(sde);
            await using var store = new PersistenceStore(source, storeSchema);
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(
                store, PersistenceRoles.World, "crate-fit-test-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await Assert.ThrowsAsync<PersistenceException>(
                () => store.LoadRowsAsync("savedFittings", createIfMissing: false));
            await cache.LoadAsync("savedFittings");
            Assert.Empty(await store.LoadRowsAsync("savedFittings", createIfMissing: false));
            Assert.True(cache.Write("savedFittings", "/_meta", new JsonObject
            {
                ["version"] = 1,
                ["nextFittingID"] = savedFittingId,
            }).Success);
            Assert.True(cache.Write("savedFittings", $"/owners/{fittingOwnerId}", new JsonObject
            {
                ["ownerID"] = fittingOwnerId,
                ["scope"] = "character",
                ["fittings"] = new JsonObject
                {
                    [sourceFittingId.ToString()] = new JsonObject
                    {
                        ["fittingID"] = sourceFittingId,
                        ["ownerID"] = fittingOwnerId,
                        ["shipTypeID"] = shipTypeId,
                        ["name"] = "Crate source fitting",
                        ["description"] = "seeded fitting",
                        ["fitData"] = new JsonArray { new JsonArray { 34, 27, 1 } },
                        ["savedDate"] = "133700000000000000",
                    },
                },
            }).Success);
            await cache.FlushTableAsync("savedFittings");

            var backend = new PersistenceCrateServiceBackend(source, items, itemTypes, gameStoreCache: cache);
            long crateId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                crateId = await items.CreateAsync(connection, transaction,
                    new NewItem(crateTypeId, characterId, destinationId, destinationFlag,
                        CustomName: "Reward Crate", CustomInfo: customInfo));
                await transaction.CommitAsync();
            }

            ServiceCall Call(string method, params PyValue[] args) => new()
            {
                Method = method,
                Args = args,
                Session = new ClientSession { CharacterId = characterId },
            };
            var service = new CrateService(backend);
            var preview = Assert.IsType<PyList>(await service.CallAsync(Call("PeekInsideFixedCrate", new PyInt(crateId))));
            Assert.Equal(PyTuple.Of(new PyInt(34), new PyInt(6), new PyStr("")), Assert.Single(preview.Items));
            var opened = Assert.IsType<PyList>(await service.CallAsync(Call("OpenCrate", new PyInt(crateId))));
            Assert.Single(opened.Items);

            var pendingLoot = await items.InLocationAsync(-9000000000001, characterId);
            var reward = Assert.Single(pendingLoot);
            Assert.Equal(6, reward.Quantity);
            await service.CallAsync(Call("ClaimLoot", new PyInt(reward.Id), new PyInt(destinationId), new PyInt(destinationFlag)));
            Assert.Equal(destinationId, (await items.GetAsync(reward.Id))!.LocationId);

            Assert.Equal(new PyInt(savedFittingId),
                await service.CallAsync(Call("ClaimFitting", new PyInt(crateId), new PyInt(sourceFittingId))));
            await using var reader = new GameStoreCache(store, null,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.Reader });
            await reader.LoadAsync("savedFittings");
            var saved = Assert.IsType<JsonObject>(reader.Read("savedFittings",
                $"/owners/{characterId}/fittings/{savedFittingId}").Data);
            Assert.Equal(characterId, saved["ownerID"]!.GetValue<long>());
            Assert.Equal(shipTypeId, saved["shipTypeID"]!.GetValue<int>());
            Assert.Equal("Crate source fitting", saved["name"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(new JsonArray { new JsonArray { 34, 27, 1 } }, saved["fitData"]));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {itemSchema} CASCADE; DROP SCHEMA IF EXISTS {storeSchema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
