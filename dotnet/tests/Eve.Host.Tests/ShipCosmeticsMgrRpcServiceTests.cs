using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

public sealed class ShipCosmeticsMgrRpcServiceTests
{
    [Fact]
    public async Task Licensed_skin_queries_use_active_character_overrides_and_ship_type_catalog()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var catalog = await ShipCosmeticsCatalog.LoadAsync(sde);
        var skin = catalog.Skins.Values.First(value => value.ShipTypeIds.Count > 0);
        var license = catalog.LicenseTypes.Values.First(value => value.SkinId == skin.SkinId);
        var shipTypeId = skin.ShipTypeIds.Min();
        var now = 1_700_000_000_000L;
        var schema = "rpc_skin_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        try
        {
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World, "rpc-skin-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("shipCosmetics");
            cache.Write("shipCosmetics", $"/characters/20/skinOverridesBySkinID/{skin.SkinId}", new JsonObject
            {
                ["skinID"] = skin.SkinId,
                ["licenseTypeID"] = license.LicenseTypeId,
                ["skinMaterialID"] = skin.SkinMaterialId,
                ["expiresAtFileTime"] = Py.FileTimeFromUnixMs(now + 86_400_000),
                ["isSingleUse"] = false,
                ["revoked"] = false,
            });
            var service = new ShipCosmeticsMgrRpcService(cache, catalog: catalog, nowUnixMs: () => now);
            var session = new ClientSession();
            session.Set("charid", new PyInt(20));

            var all = Assert.IsType<PyList>(await service.CallAsync(Call("GetLicencedSkins", session)));
            var record = Assert.IsType<PyObject>(Assert.Single(all.Items));
            Assert.Equal(new PyInt(skin.SkinId), Field(record, "skinID"));
            Assert.Equal(new PyInt(license.LicenseTypeId), Field(record, "licenseTypeID"));
            Assert.Equal(PyBool.False, Field(record, "isSingleUse"));
            Assert.Equal(new PyInt(skin.SkinMaterialId), Field(record, "skinMaterialID"));
            Assert.Equal(new PyInt(skin.SkinMaterialId), Field(record, "materialID"));
            Assert.IsType<PyLong>(Field(record, "expires"));

            var explicitOwner = Assert.IsType<PyList>(await service.CallAsync(Call("GetLicencedSkinsForShipType", session,
                new PyInt(20), new PyInt(shipTypeId))));
            Assert.Single(explicitOwner.Items);
            var currentOwner = Assert.IsType<PyList>(await service.CallAsync(Call("GetLicencedSkinsForShipType", session,
                new PyInt(shipTypeId))));
            Assert.Single(currentOwner.Items);
            var unknownType = Assert.IsType<PyList>(await service.CallAsync(Call("GetLicencedSkinsForShipType", session,
                new PyInt(0))));
            Assert.Empty(unknownType.Items);
            Assert.True(((IService)service).HasMethod("GetLicencedSkins"));
            Assert.True(((IService)service).HasMethod("GetLicencedSkinsForShipType"));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task License_activation_refuses_more_than_500_raw_item_ids_before_store_access()
    {
        var service = new ShipCosmeticsMgrRpcService();
        var session = new ClientSession();
        session.Set("charid", new PyInt(20));
        var itemIds = new PyList([.. Enumerable.Range(1, 501).Select(id => (PyValue)new PyInt(id))]);

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await service.CallAsync(Call("ActivateSkinLicense", session, itemIds)));

        Assert.Equal("SHIP_SKIN_LICENSE_BULK_LIMIT_EXCEEDED", refusal.Error.UserErrorMessage);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("false_payload")]
    [InlineData("nonexistent")]
    [InlineData("foreign_owner")]
    [InlineData("no_character")]
    [InlineData("connection_failure")]
    public async Task Activation_callbacks_use_existing_ids_after_commit_and_do_not_notify_on_failure(string scenario)
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl!));
        await using var transactionSource = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var catalog = await ShipCosmeticsCatalog.LoadAsync(sde);
        var license = catalog.LicenseTypes.Values.First(entry => catalog.Skins.ContainsKey(entry.SkinId));
        var schema = "rpc_activation_" + Guid.NewGuid().ToString("N");
        var inventorySchema = "rpc_activation_items_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        try
        {
            await store.EnsureSchemaAsync();
            await PgItemStore.MigrateAsync(source, inventorySchema);
            var items = new PgItemStore(source, inventorySchema);
            var itemOwner = scenario == "foreign_owner" ? 21L : 20L;
            long itemId;
            await using (var connection = await source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                itemId = await items.CreateAsync(connection, transaction,
                    new NewItem(license.LicenseTypeId, itemOwner, 600, 4, 2));
                await transaction.CommitAsync();
            }

            await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World,
                "rpc-activation-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("shipCosmetics");
            if (scenario == "connection_failure")
                await transactionSource.DisposeAsync();

            var notifications = new List<(string Kind, long Id, long Owner)>();
            var service = new ShipCosmeticsMgrRpcService(cache, cache, items, transactionSource, catalog,
                () => 1_700_000_000_000L,
                skinLicenseActivated: (characterId, skinId) =>
                {
                    Assert.Single(notifications);
                    notifications.Add(("license", skinId, characterId));
                },
                inventoryItemConsumed: async (characterId, before, remainingQuantity, ct) =>
                {
                    var committed = await new PgItemStore(source, inventorySchema).GetAsync(before.Id, ct);
                    Assert.NotNull(committed);
                    Assert.Equal(remainingQuantity, committed.Quantity);
                    notifications.Add(("item", before.Id, characterId));
                });
            var session = new ClientSession();
            if (scenario != "no_character")
                session.Set("charid", new PyInt(20));
            var requestedId = scenario == "nonexistent" ? long.MaxValue : itemId;
            var argument = scenario == "false_payload"
                ? (PyValue)PyBool.False
                : new PyList([new PyLong(requestedId)]);

            if (scenario is "nonexistent" or "foreign_owner" or "no_character")
            {
                var error = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                    await service.CallAsync(Call("ActivateSkinLicense", session, argument)));
                Assert.Equal("CustomInfo", error.Error.UserErrorMessage);
            }
            else if (scenario == "connection_failure")
            {
                await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
                    await service.CallAsync(Call("ActivateSkinLicense", session, argument)));
            }
            else
            {
                Assert.IsType<PyNone>(await service.CallAsync(Call("ActivateSkinLicense", session, argument)));
            }

            var remaining = await items.GetAsync(itemId);
            Assert.NotNull(remaining);
            Assert.Equal(itemOwner, remaining.OwnerId);
            Assert.Equal(scenario == "success" ? 1L : 2L, remaining.Quantity);
            var overridePath = $"/characters/20/skinOverridesBySkinID/{license.SkinId}";
            if (scenario == "success")
            {
                Assert.Collection(notifications,
                    notification => Assert.Equal(("item", itemId, 20L), notification),
                    notification => Assert.Equal(("license", (long)license.SkinId, 20L), notification));
                Assert.True(cache.Read("shipCosmetics", overridePath).Success);
            }
            else
            {
                Assert.Empty(notifications);
                Assert.False(cache.Read("shipCosmetics", overridePath).Success);
                Assert.False(cache.Read("shipCosmetics", $"/characters/21/skinOverridesBySkinID/{license.SkinId}").Success);
            }
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
            await using var inventoryCommand = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{inventorySchema}\" CASCADE", connection);
            await inventoryCommand.ExecuteNonQueryAsync();
        }
    }

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static PyValue Field(PyObject keyVal, string name) =>
        Assert.IsType<PyDict>(keyVal.Args).Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
}
