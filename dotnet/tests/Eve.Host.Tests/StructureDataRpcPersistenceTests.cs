using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Npgsql;

namespace Eve.Host.Tests;

public sealed class StructureDataRpcPersistenceTests
{
    [Fact]
    public async Task Moon_extractions_and_overview_presets_use_the_existing_game_store_tables()
    {
        var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(connectionString!));
        var schema = "structure_rpc_" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            await using var store = new PersistenceStore(source, schema);
            await store.EnsureSchemaAsync();
            await using var owner = await OwnerSession.StartAsync(
                store, PersistenceRoles.World, "structure-rpc-test-" + Guid.NewGuid().ToString("N"));
            await using var cache = new GameStoreCache(store, owner,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.World, FlushDelay = TimeSpan.FromHours(1) });
            await cache.RecoverAsync();
            await cache.LoadAsync("moonExtractions");
            await cache.LoadAsync("overviewSharedPresets");

            const long structureId = 30_000_142;
            const long startMs = 1_700_000_000_000;
            Assert.True(cache.Write("moonExtractions", $"/resourcesByStructureID/{structureId}", new JsonObject
            {
                ["resources"] = new JsonObject { ["34"] = 0.75 },
            }).Success);
            Assert.True(cache.Write("moonExtractions", "/extractions/1", new JsonObject
            {
                ["extractionID"] = 1,
                ["structureID"] = structureId,
                ["moonID"] = 40_000_001,
                ["solarSystemID"] = 30_000_142,
                ["ownerID"] = 980001,
                ["startMoveTimeMs"] = startMs,
                ["chunkAvailableTimeMs"] = startMs + 518_460_000,
                ["naturalDecayTimeMs"] = startMs + 529_260_000,
                ["durationSeconds"] = 518_460,
                ["yieldMultiplier"] = 1.0,
                ["calendarEventID"] = 42,
                ["state"] = "active",
            }).Success);

            JsonObject ReadTable(string table) => cache.Read(table, "/").Data as JsonObject ?? new JsonObject();
            var presetService = new OverviewPresetMgrService(
                () => ReadTable("overviewSharedPresets"),
                state => cache.Write("overviewSharedPresets", "/", state).Success);
            var payload = Py.Dict(("name", new PyStr("Shared overview")));
            var key = Assert.IsType<PyObject>(await presetService.CallAsync(Call("StoreLinkAndGetID", payload)));

            await cache.FlushTableAsync("moonExtractions");
            await cache.FlushTableAsync("overviewSharedPresets");

            await using var reader = new GameStoreCache(store, null,
                new GameStoreCacheOptions { ProcessRole = PersistenceRoles.Reader });
            await reader.LoadAsync("moonExtractions");
            await reader.LoadAsync("overviewSharedPresets");
            JsonObject ReadPersisted(string table) => reader.Read(table, "/").Data as JsonObject ?? new JsonObject();

            var storedKey = new PyTuple([Field(key, "hashvalue"), Field(key, "sqID")]);
            var fetchedPreset = await new OverviewPresetMgrService(
                () => ReadPersisted("overviewSharedPresets"))
                .CallAsync(Call("GetStoredPreset", storedKey));
            Assert.Equal(new PyStr("[[\"name\",\"Shared overview\"]]"), fetchedPreset);

            var session = new ClientSession();
            session.Set("structureid", new PyInt(structureId));
            var moonService = new MoonExtractionsRpcService(() => ReadPersisted("moonExtractions"));
            var extractionPair = Assert.IsType<PyTuple>(await moonService.CallAsync(
                Call("GetExtractionAndEventIDFromStructureID", session)));
            Assert.Equal(new PyInt(42), extractionPair.Items[1]);
            var resources = Assert.IsType<PyDict>(await moonService.CallAsync(
                Call("GetMoonResourcesForStructure", session)));
            Assert.Equal(new PyInt(34), Assert.Single(resources.Entries).Key);
        }
        finally
        {
            await using var connection = await source.OpenConnectionAsync();
            await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static PyValue Field(PyObject value, string name) => Assert.IsType<PyDict>(value.Args).Entries
        .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    private static ServiceCall Call(string method, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = new ClientSession(),
    };

    private static ServiceCall Call(string method, ClientSession session, params PyValue[] args) => new()
    {
        Method = method,
        Args = args,
        Session = session,
    };
}
