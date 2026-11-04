using Eve.Data;
using Eve.GameStore;
using Eve.Host;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;
using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

public sealed class OverviewPresetMgrRpcServiceTests
{
    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };

    private static PyValue Field(PyValue value, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries
            .Single(entry => entry.Key == new PyStr(name)).Value;

    [Fact]
    public async Task Overview_preset_links_round_trip_through_the_existing_gamestore_table()
    {
        var pgUrl = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(pgUrl));
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(pgUrl!));
        var schema = "rpc_overview_" + Guid.NewGuid().ToString("N");
        var store = new PersistenceStore(source, schema);
        OwnerSession? owner = null;
        GameStoreCache? cache = null;
        try
        {
            await store.EnsureSchemaAsync();
            owner = await OwnerSession.StartAsync(
                store, PersistenceRoles.World, "overview-rpc-" + Guid.NewGuid().ToString("N"));
            cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("overviewSharedPresets");

            IService service = new Eve.Services.Core.OverviewPresetMgrService(
                () => cache.Read("overviewSharedPresets", "/").Data as JsonObject,
                state => cache.Write("overviewSharedPresets", "/", state).Success);
            var session = new ClientSession();
            session.Set("charid", new PyInt(20));
            var payload = Py.KeyVal(
                ("presetName", new PyStr("Default")),
                ("tabOrder", new PyList([new PyInt(2), new PyInt(1)])));

            var key = await service.CallAsync(Call("StoreLinkAndGetID", session, payload));
            Assert.True(service.HasMethod("GetStoredPreset"));
            Assert.IsType<PyStr>(Field(key, "hashvalue"));
            Assert.Equal(new PyInt(1), Field(key, "sqID"));
            Assert.Equal(new PyStr("[[\"presetName\",\"Default\"],[\"tabOrder\",[2,1]]]"),
                await service.CallAsync(Call("GetStoredPreset", session, key)));
            Assert.True(await cache.FlushTableAsync("overviewSharedPresets") > 0);

            var repeated = await service.CallAsync(Call("StoreLinkAndGetID", session, payload));
            Assert.Equal(Field(key, "hashvalue"), Field(repeated, "hashvalue"));
            Assert.Equal(Field(key, "sqID"), Field(repeated, "sqID"));

            await using var reader = new GameStoreCache(store, null,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.Reader });
            await reader.LoadAsync("overviewSharedPresets");
            var hash = Assert.IsType<PyStr>(Field(key, "hashvalue")).Value;
            var persisted = Assert.IsType<JsonObject>(reader.Read("overviewSharedPresets", $"/entries/{hash}::1").Data);
            Assert.Equal("[[\"presetName\",\"Default\"],[\"tabOrder\",[2,1]]]", persisted["payload"]!.GetValue<string>());
        }
        finally
        {
            if (cache is not null) await cache.DisposeAsync();
            if (owner is not null) await owner.DisposeAsync();
            await store.DisposeAsync();
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
