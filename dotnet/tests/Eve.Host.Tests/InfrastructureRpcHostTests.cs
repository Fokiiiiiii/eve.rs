using System.Net;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class InfrastructureRpcHostTests : IAsyncLifetime
{
    private const long CharacterId = 90_000_001;
    private const long SystemId = 30_000_142;
    private const long OtherSystemId = 30_000_143;
    private const long StationId = 60_015_249;
    private const int RifterTypeId = 587;

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_ACCESS_GROUPS_SCHEMA", "EVE_TRADE_SCHEMA",
        "EVE_BOOKMARKS_SCHEMA", "EVE_ESS_SCHEMA", "EVE_AGENTS_SCHEMA", "EVE_XMPP_SCHEMA",
    ];

    private readonly string _prefix = "ir" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];
    private long ItemId { get; set; }
    private long OtherSystemItemId { get; set; }
    private long NestedItemId { get; set; }
    private long StationItemId { get; set; }

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";
    private string InventorySchema => Schema("EVE_INVENTORY_SCHEMA");

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[]
        {
            ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off"),
            ("EVE_CLUSTER_NAME", "e2e-Cluster-Xyz"),
        })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions(), new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static SessionOpen Open() => new(77_001, 77_002, 77, 0, "127.0.0.1", "EN", "GB");

    private static string? Text(PyValue? value) => JsSemantics.StringValue(value);

    private static ClientSession Session(Sink sink)
    {
        var session = new ClientSession(sink) { SessionId = 77_001, ClientId = 77_002 };
        session.UserId = 77;
        session.CharacterId = CharacterId;
        session.Set("solarsystemid", new PyLong(SystemId));
        return session;
    }

    [Fact]
    public void Cluster_name_fallback_matches_the_V24_preferences_default()
    {
        var names = new[] { "EVE_CLUSTER_NAME", "COMPUTERNAME", "USERDOMAIN" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("EVE_CLUSTER_NAME", null);
            Environment.SetEnvironmentVariable("COMPUTERNAME", "fixture-host");
            Environment.SetEnvironmentVariable("USERDOMAIN", "fixture-zone");
            Assert.Equal("FIXTURE-HOST@FIXTURE-ZONE", new ClusterOptions().ClusterName);
        }
        finally
        {
            foreach (var (name, value) in names) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static async Task<PyValue> CallAsync(HostRuntime host, ClientSession session, Sink sink,
        long callId, string service, string method, params PyValue[] args)
    {
        return await DispatchAsync(host, session, sink, Packets.Call(Open(), callId, service, method, args));
    }

    private static async Task<PyValue> BoundCallAsync(HostRuntime host, ClientSession session, Sink sink,
        long callId, string oid, string method, params PyValue[] args)
    {
        return await DispatchAsync(host, session, sink,
            Packets.BoundCall(Open(), callId, oid, method, Py.Tuple(args), new PyDict([])));
    }

    private static async Task<PyValue> DispatchAsync(HostRuntime host, ClientSession session, Sink sink, PyValue packet)
    {
        var raw = MarshalCodec.Decode(MarshalCodec.Encode(packet));
        Assert.True(await new PacketDispatcher(host.Registry, () => 1_800_000_000_000).DispatchAsync(raw, session));
        var response = Assert.Single(sink.Packets);
        sink.Packets.Clear();
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(response));
        var state = Assert.IsType<PyTuple>(((PyObject)decoded).Args).Items;
        Assert.Equal(MachoMessage.CallRsp, Assert.IsType<PyInt>(state[0]).Value);
        var payload = Assert.IsType<PyTuple>(state[4]);
        return payload.Items[0] is PySubStream { Decoded: { } inner } ? inner : payload.Items[0];
    }

    [PgFact]
    public async Task Cluster_name_and_location_bound_i2_calls_use_the_real_host_registry_and_persisted_items()
    {
        var host = await HostComposition.BuildAsync(Settings());
        try
        {
            var sink = new Sink();
            var session = Session(sink);
            Assert.Equal("E2E-CLUSTER-XYZ", Text(await CallAsync(host, session, sink, 1,
                "machoNet", "GetConnectedClusterName")));

            await using (var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!)))
            {
                var store = new PgItemStore(source, InventorySchema);
                await using var connection = await source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                ItemId = await store.CreateAsync(connection, transaction,
                    new NewItem(RifterTypeId, CharacterId, SystemId, ItemFlags.InSpace, Singleton: true));
                OtherSystemItemId = await store.CreateAsync(connection, transaction,
                    new NewItem(RifterTypeId, CharacterId, OtherSystemId, ItemFlags.InSpace, Singleton: true));
                NestedItemId = await store.CreateAsync(connection, transaction,
                    new NewItem(RifterTypeId, CharacterId, ItemId, ItemFlags.Cargo, Singleton: true));
                StationItemId = await store.CreateAsync(connection, transaction,
                    new NewItem(RifterTypeId, CharacterId, StationId, ItemFlags.Hangar, Singleton: true));
                await transaction.CommitAsync();
            }

            var bindParams = Py.Tuple(new PyLong(SystemId), new PyInt(5));
            Assert.Equal(MachoMessage.ProxyNodeId, Assert.IsType<PyInt>(await CallAsync(host, session, sink, 2,
                "i2", "MachoResolveObject", bindParams)).Value);
            var nested = Py.Tuple(new PyStr("IsPrimed"), Py.Tuple(new PyLong(ItemId)), new PyDict([]));
            var bind = Assert.IsType<PyTuple>(await CallAsync(host, session, sink, 3,
                "i2", "MachoBindObject", bindParams, nested));
            Assert.Equal(2, bind.Items.Count);
            Assert.Same(PyBool.True, bind.Items[1]);
            var opened = Assert.IsType<PySubStruct>(bind.Items[0]);
            var openedValue = opened.Value is PySubStream stream ? stream.Decoded : opened.Value;
            var oid = Text(Assert.IsType<PyTuple>(openedValue).Items[0]);
            Assert.NotNull(oid);
            Assert.Equal("i2", host.Registry.BoundObjects[oid!].Service);

            var itemRow = Assert.IsType<PyObject>(await BoundCallAsync(host, session, sink, 4, oid!, "GetItem", new PyLong(ItemId)));
            Assert.Equal("util.Row", Text(itemRow.Name));
            var row = Assert.IsType<PyDict>(itemRow.Args);
            var header = Assert.IsType<PyTuple>(row.Entries.Single(entry => Text(entry.Key) == "header").Value);
            var line = Assert.IsType<PyTuple>(row.Entries.Single(entry => Text(entry.Key) == "line").Value);
            Assert.Equal("typeID", Text(header.Items[1]));
            Assert.Equal(RifterTypeId, Assert.IsType<PyInt>(line.Items[1]).Value);
            Assert.Equal(SystemId, Assert.IsType<PyInt>(line.Items[3]).Value);

            var missing = Assert.IsType<PyBool>(await BoundCallAsync(host, session, sink, 5, oid!, "IsPrimed", new PyLong(ItemId + 1)));
            Assert.Same(PyBool.False, missing);
            Assert.Same(PyNone.Instance, await BoundCallAsync(host, session, sink, 6, oid!, "GetItem", new PyLong(ItemId + 1)));
            Assert.Same(PyBool.False, await BoundCallAsync(host, session, sink, 7, oid!, "IsPrimed", new PyLong(OtherSystemItemId)));
            Assert.Same(PyBool.True, await BoundCallAsync(host, session, sink, 8, oid!, "IsPrimed", new PyLong(NestedItemId)));

            session.Set("solarsystemid", new PyLong(OtherSystemId));
            Assert.Same(PyBool.False, await BoundCallAsync(host, session, sink, 9, oid!, "IsPrimed", new PyLong(ItemId)));
            session.Set("solarsystemid", new PyLong(SystemId));

            var secondSink = new Sink();
            var secondSession = Session(secondSink);
            var secondBind = Assert.IsType<PyTuple>(await CallAsync(host, secondSession, secondSink, 10, "i2", "MachoBindObject",
                bindParams, Py.Tuple(new PyStr("IsPrimed"), Py.Tuple(new PyLong(ItemId)), new PyDict([]))));
            var secondOpened = Assert.IsType<PySubStruct>(secondBind.Items[0]);
            var secondOpenedValue = secondOpened.Value is PySubStream secondStream ? secondStream.Decoded : secondOpened.Value;
            var secondOid = Text(Assert.IsType<PyTuple>(secondOpenedValue).Items[0]);
            Assert.NotNull(secondOid);
            var concurrent = await Task.WhenAll(
                BoundCallAsync(host, session, sink, 11, oid!, "IsPrimed", new PyLong(ItemId)),
                BoundCallAsync(host, secondSession, secondSink, 12, secondOid!, "IsPrimed", new PyLong(ItemId)));
            Assert.All(concurrent, result => Assert.Same(PyBool.True, result));

            var stationSink = new Sink();
            var stationSession = Session(stationSink);
            stationSession.Set("stationid", new PyLong(StationId));
            var stationBind = Assert.IsType<PyTuple>(await CallAsync(host, stationSession, stationSink, 13, "i2", "MachoBindObject",
                Py.Tuple(new PyLong(StationId), new PyInt(15)),
                Py.Tuple(new PyStr("IsPrimed"), Py.Tuple(new PyLong(StationItemId)), new PyDict([]))));
            Assert.Same(PyBool.True, stationBind.Items[1]);
        }
        finally
        {
            await host.ShutdownAsync();
        }

        host = await HostComposition.BuildAsync(Settings());
        try
        {
            var sink = new Sink();
            var session = Session(sink);
            var bind = Assert.IsType<PyTuple>(await CallAsync(host, session, sink, 5, "i2", "MachoBindObject",
                Py.Tuple(new PyLong(SystemId), new PyInt(5)),
                Py.Tuple(new PyStr("GetItem"), Py.Tuple(new PyLong(ItemId)), new PyDict([]))));
            var itemRow = Assert.IsType<PyObject>(bind.Items[1]);
            Assert.Equal("util.Row", Text(itemRow.Name));
            var line = Assert.IsType<PyTuple>(Assert.IsType<PyDict>(itemRow.Args).Entries
                .Single(entry => Text(entry.Key) == "line").Value);
            Assert.Equal(RifterTypeId, Assert.IsType<PyInt>(line.Items[1]).Value);
        }
        finally
        {
            await host.ShutdownAsync();
        }
    }
}
