using System.Text.Json.Nodes;
using Eve.Data;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The moon extraction table on PostgreSQL (ST-6a): the game store's <c>moonExtractions</c> rows are copied in once, the moon service reads them from the store in the
/// client's shape, completed history stays out of the reads, and a second import keeps the rows already there (skipped without EVE_PG_URL).
/// </summary>
public sealed class MoonExtractionStoreTests
{
    private const long Structure = 30_000_142, Owner = 980001, Start = 1_700_000_000_000;

    /// <summary>The game store's table as EVE.js wrote it: a fitted profile, an active and a fracturing extraction, a completed one and a row with no structure.</summary>
    private static JsonObject LegacyTable() => new()
    {
        ["resourcesByStructureID"] = new JsonObject
        {
            [Structure.ToString()] = new JsonObject
            {
                ["moonID"] = 40_000_001,
                ["solarSystemID"] = 30_000_142,
                ["ownerID"] = Owner,
                ["resources"] = new JsonObject { ["34"] = 0.75, ["20"] = 0.25, ["0"] = 9.0 },
            },
        },
        ["extractions"] = new JsonObject
        {
            ["100"] = Legacy(100, Structure, "active", Start + 1_000, 7654),
            ["101"] = Legacy(101, Structure + 1, "fracturing", Start + 2_000, 0),
            ["102"] = Legacy(102, Structure + 2, "completed", Start + 3_000, 9),
            ["103"] = new JsonObject { ["extractionID"] = 103, ["chunkAvailableTimeMs"] = Start + 4_000, ["startMoveTimeMs"] = Start, ["state"] = "active" },
        },
    };

    private static JsonObject Legacy(long id, long structureId, string state, long chunkMs, long eventId) => new()
    {
        ["extractionID"] = id,
        ["structureID"] = structureId,
        ["moonID"] = 40_000_001,
        ["solarSystemID"] = 30_000_142,
        ["ownerCorpID"] = Owner,
        ["startMoveTimeMs"] = Start,
        ["chunkAvailableTimeMs"] = chunkMs,
        ["naturalDecayTimeMs"] = chunkMs + 10_800_000,
        ["durationSeconds"] = 518_460,
        ["yieldMultiplier"] = 1.25,
        ["calendarEventID"] = eventId,
        ["state"] = state,
    };

    [PgFact]
    public async Task The_moon_service_reads_the_imported_rows_from_the_store_in_the_client_shape()
    {
        await using var fixture = await Fixture.CreateAsync();
        // three extractions and the profile; the row with no structure is skipped
        Assert.Equal(4, await fixture.ImportAsync(LegacyTable()));

        var moon = new MoonExtractionsRpcService(fixture.ReadAsync);
        var session = new ClientSession();
        session.Set("structureid", new PyLong(Structure));
        session.Set("corpid", new PyInt(Owner));

        var pair = Assert.IsType<PyTuple>(await moon.CallAsync(Call("GetExtractionAndEventIDFromStructureID", session)));
        Assert.Equal(new PyInt(7654), pair.Items[1]);
        var extraction = Assert.IsType<PyObject>(pair.Items[0]);
        Assert.Equal(new PyInt(100), Field(extraction, "extractionID"));
        Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Start)), Field(extraction, "startMoveTime"));
        Assert.Equal(new PyInt(518_460), Field(extraction, "durationSeconds"));
        Assert.Equal(new PyReal(1.25), Field(extraction, "yieldMultiplier"));

        var rows = Assert.IsType<PyList>(await moon.CallAsync(Call("GetExtractionsForCorp", session)));
        Assert.Equal(new PyInt(100), Field(Assert.IsType<PyObject>(rows.Items[0]), "extractionID"));
        Assert.Equal(new PyInt(101), Field(Assert.IsType<PyObject>(rows.Items[1]), "extractionID"));
        Assert.Equal(2, rows.Items.Count);

        var resources = Assert.IsType<PyDict>(await moon.CallAsync(Call("GetMoonResourcesForStructure", session)));
        Assert.Equal(new PyValue[] { new PyInt(20), new PyInt(34) }, resources.Entries.Select(entry => entry.Key).ToArray());
        Assert.Equal(new PyReal(0.25), resources.Entries[0].Value);
    }

    [PgFact]
    public async Task A_second_import_adds_nothing_and_keeps_the_row_already_there()
    {
        await using var fixture = await Fixture.CreateAsync();
        // three extractions and the profile; the row with no structure is skipped
        Assert.Equal(4, await fixture.ImportAsync(LegacyTable()));

        // the game store still holds the old row; the copy in the store has moved on (here: it is completed) and must not be overwritten
        var changed = LegacyTable();
        ((JsonObject)((JsonObject)changed["extractions"]!)["100"]!)["state"] = "completed";
        Assert.Equal(0, await fixture.ImportAsync(changed));

        var moon = new MoonExtractionsRpcService(fixture.ReadAsync);
        var session = new ClientSession();
        session.Set("structureid", new PyLong(Structure));
        var pair = Assert.IsType<PyTuple>(await moon.CallAsync(Call("GetExtractionAndEventIDFromStructureID", session)));
        Assert.Equal(new PyInt(7654), pair.Items[1]);
    }

    /// <summary>A scratch schema of the structures (the tables the import writes) and the store over it; the schema is dropped on disposal.</summary>
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _schema;

        private Fixture(NpgsqlDataSource source, string schema, MoonExtractionStore store)
        {
            Source = source;
            _schema = schema;
            Store = store;
        }

        public NpgsqlDataSource Source { get; }
        public MoonExtractionStore Store { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("EVE_PG_URL");
            Assert.False(string.IsNullOrWhiteSpace(connectionString));
            var source = NpgsqlDataSource.Create(PgConnection.ForTest(connectionString!));
            var schema = "moon_store_" + Guid.NewGuid().ToString("N")[..12];
            await StructureStore.MigrateAsync(source, schema);
            return new Fixture(source, schema, new MoonExtractionStore(source, schema));
        }

        public async Task<JsonObject?> ReadAsync() => await Store.ReadLegacyAsync();

        public async Task<int> ImportAsync(JsonObject table)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            var added = await Store.ImportAsync(connection, transaction, table);
            await transaction.CommitAsync();
            return added;
        }

        public async ValueTask DisposeAsync()
        {
            await using (var connection = await Source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE", connection))
            {
                await command.ExecuteNonQueryAsync();
            }
            await Source.DisposeAsync();
        }
    }

    private static PyValue Field(PyObject value, string name) => Assert.IsType<PyDict>(value.Args).Entries
        .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    private static ServiceCall Call(string method, ClientSession session) => new()
    {
        Method = method,
        Args = [],
        Session = session,
    };
}
