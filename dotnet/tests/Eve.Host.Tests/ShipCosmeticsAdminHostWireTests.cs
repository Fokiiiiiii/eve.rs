using Eve.GameStore;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Npgsql;

namespace Eve.Host.Tests;

public partial class HostServerTests
{
    [PgFact]
    public async Task Ship_cosmetics_gm_license_calls_persist_and_keep_client_shapes_over_host_wire()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(pgUrl!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var catalog = await ShipCosmeticsCatalog.LoadAsync(sde);
        var skinId = catalog.Skins.Values
            .First(skin => catalog.LicenseTypes.Values.Any(license => license.SkinId == skin.SkinId)).SkinId;
        var now = 1_700_000_000_000L;
        var schema = "rpc_skin_admin_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        try
        {
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(store, PersistenceRoles.World,
                "rpc-skin-admin-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("shipCosmetics");

            var service = new ShipCosmeticsMgrRpcService(cache, catalog: catalog, nowUnixMs: () => now);
            Start(
                configure: registry => registry.Register(service),
                sessionOpened: session =>
                {
                    session.Set("charid", new PyInt(20));
                    session.Set("accountRole", new PyLong(GameMasterRoles.Gml));
                });
            var (front, open) = await OpenFrontAsync();

            var timed = await NsHostRpcKwargsAsync(front, open, 1, "shipCosmeticsMgr", "GiveSkin",
                new PyTuple([new PyInt(skinId)]),
                Py.Dict(("machoVersion", new PyInt(1)), ("duration", new PyInt(30))));
            Assert.Same(PyNone.Instance, timed);
            var record = Assert.IsType<PyObject>(await HostRpcAsync(
                front, open, 2, "shipCosmeticsMgr", "GetFirstPartySkinData", new PyInt(20), new PyInt(skinId)));
            Assert.IsType<PyLong>(NsField(Assert.IsType<PyDict>(record.Args), "expires"));
            Assert.Equal(PyBool.False, NsField(Assert.IsType<PyDict>(record.Args), "isSingleUse"));

            var singleUse = await NsHostRpcKwargsAsync(front, open, 3, "shipCosmeticsMgr", "GiveSkin",
                new PyTuple([new PyInt(skinId)]),
                Py.Dict(("machoVersion", new PyInt(1)), ("isSingleUse", PyBool.True)));
            Assert.Same(PyNone.Instance, singleUse);
            record = Assert.IsType<PyObject>(await HostRpcAsync(
                front, open, 4, "shipCosmeticsMgr", "GetFirstPartySkinData", new PyInt(20), new PyInt(skinId)));
            Assert.Same(PyNone.Instance, NsField(Assert.IsType<PyDict>(record.Args), "expires"));
            Assert.Equal(PyBool.True, NsField(Assert.IsType<PyDict>(record.Args), "isSingleUse"));

            Assert.Same(PyNone.Instance, await HostRpcAsync(
                front, open, 5, "shipCosmeticsMgr", "GiveSkin", new PyInt(skinId)));
            record = Assert.IsType<PyObject>(await HostRpcAsync(
                front, open, 6, "shipCosmeticsMgr", "GetFirstPartySkinData", new PyInt(20), new PyInt(skinId)));
            Assert.Same(PyNone.Instance, NsField(Assert.IsType<PyDict>(record.Args), "expires"));
            Assert.Equal(PyBool.False, NsField(Assert.IsType<PyDict>(record.Args), "isSingleUse"));

            Assert.Same(PyNone.Instance, await HostRpcAsync(
                front, open, 7, "shipCosmeticsMgr", "GMExpireSkinLicense", new PyInt(skinId)));
            Assert.Same(PyNone.Instance, await HostRpcAsync(
                front, open, 8, "shipCosmeticsMgr", "GetFirstPartySkinData", new PyInt(20), new PyInt(skinId)));
            Assert.True(await cache.FlushAllAsync() > 0);

            var persisted = await store.LoadTableAsync("shipCosmetics");
            var reloadedRecord = Assert.IsType<System.Text.Json.Nodes.JsonObject>(
                persisted["characters"]?["20"]?["skinOverridesBySkinID"]?[skinId.ToString()]);
            Assert.True(Py.FileTimeFromUnixMs(now) > long.Parse(
                reloadedRecord["expiresAtFileTime"]!.ToString(), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
