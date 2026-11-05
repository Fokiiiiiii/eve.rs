using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Fitting;
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
    private sealed class FittingHost(long characterId) : IFittingHost
    {
        public Task<IReadOnlyDictionary<int, int>> CategoriesAsync(IReadOnlyCollection<int> typeIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fitting list read does not request type categories.");
        public Task<bool> HasFittingManagerRoleAsync(long requestedCharacterId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fitting list read does not check mutation roles.");
        public Task<bool> CanAccessOwnerAsync(long requestedCharacterId, long ownerId, FittingOwnerScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(requestedCharacterId == characterId && ownerId == characterId && scope == FittingOwnerScope.Character);
        public Task<long?> AllianceExecutorAsync(long allianceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The fitting list read does not request alliance ownership.");
        public IReadOnlyList<ClientSession> SessionsOf(FittingOwnerScope scope, long ownerId) =>
            throw new NotSupportedException("The fitting list read does not send notifications.");
    }

    [Fact]
    public async Task Opening_and_claiming_a_crate_updates_the_inventory_store()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(connectionString!));
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
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(connectionString!));
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
        var fittingSchema = "crate_fit_sql_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await PgItemStore.MigrateAsync(source, itemSchema);
            await Eve.Fitting.PgModuleStateStore.MigrateAsync(source, fittingSchema);
            var savedFittings = new Eve.Fitting.PgSavedFittingStore(source, fittingSchema);
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

            var backend = new PersistenceCrateServiceBackend(source, items, itemTypes, gameStoreCache: cache, savedFittings: savedFittings);
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

            var claimed = await service.CallAsync(Call("ClaimFitting", new PyInt(crateId), new PyInt(sourceFittingId)));
            var claimedId = claimed switch { PyInt id => id.Value, PyLong id => id.Value, _ => 0 };
            Assert.True(claimedId > 0);
            IService fittingService = new CharFittingMgrService(new FittingMgrCalls(savedFittings, new FittingHost(characterId), () => 0));
            var fittingList = Assert.IsType<PyDict>(await fittingService.CallAsync(Call("GetFittings")));
            var fitting = Assert.Single(fittingList.Entries);
            Assert.Equal(new PyLong(claimedId), fitting.Key);
            var data = Assert.IsType<PyDict>(Assert.IsType<PyObject>(fitting.Value).Args);
            Assert.Equal(new PyStr("Crate source fitting"), data.Entries.Single(e => e.Key == new PyStr("name")).Value);
            var restarted = new Eve.Fitting.PgSavedFittingStore(source, fittingSchema);
            var saved = Assert.Single(await restarted.ListAsync(Eve.Fitting.FittingOwnerScope.Character, characterId));
            Assert.Equal(claimedId, saved.FittingId);
            Assert.Equal(shipTypeId, saved.Draft.ShipTypeId);
            Assert.Equal([new Eve.Fitting.FittingItem(34, 27, 1)], saved.Draft.FitData);
            Assert.Equal(PyNone.Instance, await service.CallAsync(Call("ClaimFitting", new PyInt(crateId), new PyInt(sourceFittingId))));
            Assert.Single(await restarted.ListAsync(Eve.Fitting.FittingOwnerScope.Character, characterId));
            Assert.Null(cache.Read("savedFittings", $"/owners/{characterId}").Data);
            var catalog = cache.Read("savedFittings", "/").Data!.ToJsonString();
            var unconfigured = new PersistenceCrateServiceBackend(source, items, itemTypes, gameStoreCache: cache);
            Assert.Null(await unconfigured.ClaimFittingAsync(characterId, sourceFittingId, CancellationToken.None));
            Assert.Null(await backend.ClaimFittingAsync(characterId, sourceFittingId + 99, CancellationToken.None));
            var fitPath = $"/owners/{fittingOwnerId}/fittings/{sourceFittingId}/fitData";
            Assert.True(cache.Write("savedFittings", fitPath, new JsonArray { new JsonArray { 34, 27, -1 } }).Success);
            Assert.Null(await backend.ClaimFittingAsync(characterId, sourceFittingId, CancellationToken.None));
            Assert.True(cache.Write("savedFittings", fitPath, new JsonArray { new JsonArray { 34, 27, 1 } }).Success);
            Assert.NotNull(await savedFittings.SaveManyAsync(Eve.Fitting.FittingOwnerScope.Character, characterId,
                Enumerable.Repeat(saved.Draft, 499).ToArray(), 500, saved.SavedAtFileTime));
            Assert.Null(await backend.ClaimFittingAsync(characterId, sourceFittingId, CancellationToken.None));
            Assert.Equal(500, (await restarted.ListAsync(Eve.Fitting.FittingOwnerScope.Character, characterId)).Count);
            Assert.Equal(catalog, cache.Read("savedFittings", "/").Data!.ToJsonString());
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {itemSchema} CASCADE; DROP SCHEMA IF EXISTS {storeSchema} CASCADE; DROP SCHEMA IF EXISTS {fittingSchema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
