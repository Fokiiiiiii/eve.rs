using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Host;
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
        await using var source = NpgsqlDataSource.Create(pgUrl!);
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

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) =>
        new() { Method = method, Args = args, Session = session };

    private static PyValue Field(PyObject keyVal, string name) =>
        Assert.IsType<PyDict>(keyVal.Args).Entries.Single(pair => JsSemantics.StringValue(pair.Key) == name).Value;
}
