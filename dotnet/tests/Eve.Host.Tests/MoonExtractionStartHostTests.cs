using Eve.Macho;
using Eve.Scheduler;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Structures;
using Npgsql;
using System.Text.Json.Nodes;

namespace Eve.Host.Tests;

/// <summary>
/// <c>StartNewExtraction</c> through the moon service and the Host's authority, on the real stores and the pinned SDE (skipped without EVE_PG_URL): the start and its
/// notice to the pilot, each named refusal (the gate of the structure and the drill, the moon, the duration, Pochven), the running extraction answered as it is, and the
/// record read back by the moon service in the client's shape.
/// </summary>
public sealed class MoonExtractionStartHostTests
{
    private const long Corporation = 98_000_001, OtherCorporation = 98_000_002, Structure = 1_000_000_000_301, Character = 20;
    private const long SolarSystem = 30_000_142, PochvenSystem = 30_000_021;
    private const int Athanor = 35835;

    private sealed class Route : IAsyncDisposable
    {
        private readonly string _schema;

        private Route(string schema, NpgsqlDataSource source, StructureStore structures, MoonExtractionStore store, MoonExtractionsRpcService moon, Sink packets)
        {
            _schema = schema;
            Source = source;
            Structures = structures;
            Store = store;
            Moon = moon;
            Packets = packets;
            Session = new ClientSession(packets);
            Session.Set("charid", new PyInt(Character));
            Session.Set("corpid", new PyInt(Corporation));
            Session.Set("structureid", new PyLong(Structure));
        }

        public NpgsqlDataSource Source { get; }
        public StructureStore Structures { get; }
        public MoonExtractionStore Store { get; }
        public MoonExtractionsRpcService Moon { get; }
        public Sink Packets { get; }
        public ClientSession Session { get; }

        public static async Task<Route> CreateAsync(string pgUrl)
        {
            var source = NpgsqlDataSource.Create(pgUrl);
            var sde = new SdeReader(source);
            Assert.NotNull(await sde.BuildAsync());
            var rules = await MoonRulesSource.LoadAsync(sde);
            var schema = "moon_host_" + Guid.NewGuid().ToString("N")[..12];
            await StructureStore.MigrateAsync(source, schema);
            var structures = new StructureStore(source, schema);
            var store = new MoonExtractionStore(source, schema);
            var scheduler = new DurableScheduler(source, () => Now, new SchedulerOptions { Concurrency = 1 }, schema + "_sch", _ => { });
            var moons = new MoonExtractions(structures, store, scheduler, rules);
            var authority = new StructureMoonAuthority(source, structures, moons, rules);
            var sink = new Sink();
            var route = new Route(schema, source, structures, store, new MoonExtractionsRpcService(mutationBackend: authority.CallAsync), sink);
            return route;
        }

        public static long Now => 1_700_006_400_000;

        /// <summary>A refinery of the corporation with its moon drill online (or not), and the moon's resources (or none).</summary>
        public async Task RefineryAsync(bool drillOnline = true, bool withMoon = true, long structure = Structure, long owner = Corporation, long solarSystem = SolarSystem)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Structures.CreateAsync(connection, transaction, new NewStructure(structure, Athanor, (int)solarSystem, 0, 0, 0, 0, owner, 0, "Refinery", ""));
            await Structures.SetServiceAsync(connection, transaction, structure, StructureServiceIds.MoonMining, drillOnline ? ServiceState.Online : ServiceState.Offline, 0);
            if (withMoon)
            {
                await Store.ImportAsync(connection, transaction, new JsonObject
                {
                    ["resourcesByStructureID"] = new JsonObject
                    {
                        [structure.ToString()] = new JsonObject { ["moonID"] = 40_000_001, ["solarSystemID"] = solarSystem, ["ownerID"] = owner, ["resources"] = new JsonObject { ["34"] = 0.75, ["20"] = 0.25 } },
                    },
                });
            }
            await transaction.CommitAsync();
        }

        /// <summary>A pilot of the corporation docked at the structure (the gate of the start).</summary>
        public ClientSession DockedAt(long structure)
        {
            var session = new ClientSession(Packets);
            session.Set("charid", new PyInt(Character));
            session.Set("corpid", new PyInt(Corporation));
            session.Set("structureid", new PyLong(structure));
            return session;
        }

        public ServiceCall Start(ClientSession? session = null, PyValue? structure = null, double duration = 518_460, bool calendar = false) => new()
        {
            Method = "StartNewExtraction",
            Args = [structure ?? new PyLong(Structure), new PyReal(duration), calendar ? PyBool.True : PyBool.False],
            Session = session ?? Session,
        };

        public async ValueTask DisposeAsync()
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection)) await command.ExecuteNonQueryAsync();
            await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}_sch\" CASCADE", connection)) await command.ExecuteNonQueryAsync();
            await Source.DisposeAsync();
        }
    }

    /// <summary>The packets a session was sent, in order.</summary>
    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Sent { get; } = [];
        public void Send(PyValue packet) => Sent.Add(packet);
    }

    /// <summary>The names of the notifications among the packets (<c>OnMoonExtractionUpdate</c> and the rest).</summary>
    private static List<string> NotificationNames(Sink sink)
    {
        var names = new List<string>();
        foreach (var packet in sink.Sent.OfType<PyObject>())
        {
            var state = ((PyTuple)packet.Args).Items;
            if (state[0] is not PyInt { Value: MachoMessage.Notification }) continue;
            var address = ((PyTuple)((PyObject)state[2]).Args).Items;
            names.Add(JsSemantics.StringValue(address[1]) ?? "");
        }
        return names;
    }

    /// <summary>The text of a named <c>CustomNotify</c> refusal.</summary>
    private static string Notice(Exception exception)
    {
        var error = Assert.IsType<MachoWrappedException>(exception).Error;
        Assert.Equal("CustomNotify", error.UserErrorMessage);
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Payload).Header).Items[1]).Items;
        return ((PyStr)((PyDict)arguments[1]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value).Value;
    }

    private static string PgUrl() => Environment.GetEnvironmentVariable("EVE_PG_URL") ?? throw new InvalidOperationException("EVE_PG_URL is not set");

    private static PyValue Field(PyValue payload, string name) => Assert.IsType<PyDict>(Assert.IsType<PyObject>(payload).Args).Entries
        .Single(entry => Assert.IsType<PyStr>(entry.Key).Value == name).Value;

    [PgFact]
    public async Task Starting_a_chunk_answers_the_extraction_and_tells_the_pilot()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.RefineryAsync();

        var payload = await route.Moon.CallAsync(route.Start());

        Assert.Equal(new PyInt(518_460), Field(payload, "durationSeconds"));
        Assert.Equal(new PyLong(Structure), Field(payload, "structureID"));
        Assert.Equal(new PyInt(Corporation), Field(payload, "ownerID"));
        Assert.Equal(new[] { "OnMoonExtractionUpdate" }, NotificationNames(route.Packets));
    }

    [PgFact]
    public async Task Each_refusal_is_a_named_notice_and_changes_nothing()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.RefineryAsync(drillOnline: false);
        await route.RefineryAsync(withMoon: false, structure: Structure + 1);
        await route.RefineryAsync(structure: Structure + 2, solarSystem: PochvenSystem);

        var notDocked = new ClientSession(route.Packets);
        notDocked.Set("corpid", new PyInt(Corporation));
        Assert.Equal("StructureDefenseDenied", Assert.IsType<MachoWrappedException>(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Moon.CallAsync(route.Start(notDocked)).AsTask())).Error.UserErrorMessage);

        var otherCorp = new ClientSession(route.Packets);
        otherCorp.Set("corpid", new PyInt(OtherCorporation));
        otherCorp.Set("structureid", new PyLong(Structure));
        Assert.Equal("StructureDefenseDenied", Assert.IsType<MachoWrappedException>(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Moon.CallAsync(route.Start(otherCorp)).AsTask())).Error.UserErrorMessage);

        Assert.Equal("An online Standup Moon Drill is required.", Notice(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Moon.CallAsync(route.Start()).AsTask())));
        Assert.Equal("This structure has no moon to mine.", Notice(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Moon.CallAsync(route.Start(route.DockedAt(Structure + 1), new PyLong(Structure + 1))).AsTask())));
        Assert.Equal("Moon mining is not available in Pochven.", Notice(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Moon.CallAsync(route.Start(route.DockedAt(Structure + 2), new PyLong(Structure + 2))).AsTask())));
        Assert.Equal("MoonminingEventTooShort", Assert.IsType<MachoWrappedException>(await Assert.ThrowsAsync<MachoWrappedException>(() =>
            route.Moon.CallAsync(route.Start(duration: 0.4)).AsTask())).Error.UserErrorMessage);

        Assert.Empty(NotificationNames(route.Packets));
        var legacy = await route.Store.ReadLegacyAsync();
        Assert.Empty(legacy["extractions"]!.AsObject());
    }

    [PgFact]
    public async Task A_second_start_answers_the_running_extraction_without_a_new_notice()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.RefineryAsync();
        var first = await route.Moon.CallAsync(route.Start());
        Assert.Single(NotificationNames(route.Packets));

        var second = await route.Moon.CallAsync(route.Start(duration: 4_838_400));

        Assert.Equal(Field(first, "extractionID"), Field(second, "extractionID"));
        Assert.Equal(new PyInt(518_460), Field(second, "durationSeconds"));
        Assert.Single(NotificationNames(route.Packets));
    }

    [PgFact]
    public async Task The_started_chunk_is_read_back_by_the_moon_service_in_the_client_shape()
    {
        await using var route = await Route.CreateAsync(PgUrl());
        await route.RefineryAsync();
        var started = await route.Moon.CallAsync(route.Start(calendar: true));

        async Task<JsonObject?> ReadLegacy() => await route.Store.ReadLegacyAsync();
        var reader = new MoonExtractionsRpcService(ReadLegacy);
        var pair = Assert.IsType<PyTuple>(await reader.CallAsync(new ServiceCall
        {
            Method = "GetExtractionAndEventIDFromStructureID",
            Args = [],
            Session = route.Session,
        }));

        Assert.Equal(Field(started, "extractionID"), Field(pair.Items[0], "extractionID"));
        Assert.Equal(new PyInt(1), pair.Items[1]);
    }
}
