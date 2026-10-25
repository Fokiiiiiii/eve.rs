using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Market;
using Eve.Services;
using Eve.Services.Station;
using Eve.Sde;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class MarketTradeHostWireTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
        "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA",
        "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA",
        "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string prefix = "mtw" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> saved = [];
    private readonly List<string> schemas = [];
    private HostRuntime? host;

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{prefix}_{name}" : "";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (host is not null)
        {
            await host.ShutdownAsync();
            host = null;
        }
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private async Task<long> CreateTraderAsync(NpgsqlDataSource source, Station station, string name)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(connection, transaction,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, actor, 10000m, 0);
        await using (var membership = new NpgsqlCommand($"""
            INSERT INTO {Schema("EVE_RELATIONS_SCHEMA")}.relations
                (kind, subject, object, value, attrs, since_ms, until_ms, updated_ms)
            VALUES ($1, $2, $3, 1, $4::jsonb, $5, NULL, $5)
            """, connection, transaction))
        {
            membership.Parameters.Add(new NpgsqlParameter { Value = Eve.Relations.RelationKinds.CorporationMember.Name });
            membership.Parameters.Add(new NpgsqlParameter { Value = actor });
            membership.Parameters.Add(new NpgsqlParameter { Value = 1000044L });
            membership.Parameters.Add(new NpgsqlParameter { Value = "{}" });
            membership.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await membership.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return actor;
    }

    [PgFact]
    public async Task Registered_market_and_trade_calls_reply_and_update_persisted_state()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var trade = host.Registry.Lookup("trademgr") ?? throw new InvalidOperationException("trademgr is not registered");
        foreach (var method in new[] { "StartupCheck", "PlaceBuyOrder", "ModifyCharOrder", "PlacePlexSellOrder", "ModifyPlexCharOrder" })
            Assert.True(market.HasMethod(method), method);
        Assert.True(trade.HasMethod("InitiateTrade"));
        Assert.True(trade.HasMethod("MachoBindObject"));

        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Market Trade RPC Pilot");
        var targetId = await CreateTraderAsync(source, station, "Market Trade RPC Peer");
        var actorSink = new Sink();
        var targetSink = new Sink();
        var session = DockedSession(actor, 801, station, actorSink);
        var target = DockedSession(targetId, 802, station, targetSink);
        var sessions = host.CharacterSessions ?? throw new InvalidOperationException("character sessions are not registered");
        sessions.Enter(actor, session);
        sessions.Enter(targetId, target);

        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "StartupCheck", 1)));

        // Buy and modify a resting personal order using the same terms the client sends in marketsvc.py.
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "PlaceBuyOrder", 2,
            new PyInt(station.Id), new PyInt(35), new PyReal(1234), new PyInt(1), new PyInt(-1), new PyInt(1),
            new PyInt(30), PyBool.False, PyNone.Instance)));
        var marketStore = new MarketStore(source, Schema("EVE_MARKET_SCHEMA"));
        var bid = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order;
        Assert.True(bid.Bid);
        await BackdateAsync(source, bid.Id);
        bid = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order;
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "ModifyCharOrder", 3,
            new PyLong(bid.Id), new PyReal(1235), PyBool.True, new PyLong(station.Id), new PyInt(station.SolarSystemId),
            new PyReal((double)bid.Price), new PyInt(bid.Range), new PyInt(bid.VolumeRemaining),
            new PyLong(Py.FileTimeFromUnixMs(bid.IssuedMs)))));
        Assert.Equal(1235m, Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order.Price);

        // PLEX sell returns the boolean consumed by sellMulti.py; the dedicated modify RPC updates the same persisted order.
        long plexItem;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            plexItem = await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA")).CreateAsync(connection, transaction,
                new NewItem(44992, actor, station.Id, ItemFlags.Hangar, 1));
            await transaction.CommitAsync();
        }
        var sellEntry = Py.KeyVal(
            ("typeID", new PyInt(44992)), ("itemID", new PyLong(plexItem)), ("price", new PyReal(1234)),
            ("quantity", new PyInt(1)), ("stationID", new PyLong(station.Id)));
        Assert.Equal(PyBool.True, await market.CallAsync(Call(session, "PlacePlexSellOrder", 4,
            sellEntry, PyBool.False, new PyInt(30), PyNone.Instance)));
        var plexAsk = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false), owned => owned.Order.TypeId == 44992).Order;
        Assert.False(plexAsk.Bid);
        await BackdateAsync(source, plexAsk.Id);
        plexAsk = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false), owned => owned.Order.TypeId == 44992).Order;
        Assert.IsType<PyNone>(await market.CallAsync(Call(session, "ModifyPlexCharOrder", 5,
            new PyLong(plexAsk.Id), new PyReal(1233), PyBool.False, new PyLong(station.Id), new PyInt(station.SolarSystemId),
            new PyReal((double)plexAsk.Price), new PyInt(plexAsk.Range), new PyInt(plexAsk.VolumeRemaining),
            new PyLong(Py.FileTimeFromUnixMs(plexAsk.IssuedMs)))));
        Assert.Equal(1233m, Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false),
            owned => owned.Order.TypeId == 44992).Order.Price);
        Assert.NotEmpty(actorSink.Packets);

        // InitiateTrade returns a moniker, not the trade row. The client's next List call binds that moniker and reads the row.
        var moniker = Assert.IsType<PyObject>(await trade.CallAsync(Call(session, "InitiateTrade", 6, new PyLong(targetId))));
        var monikerArgs = Assert.IsType<PyTuple>(moniker.Args);
        Assert.Equal(new PyStr("trademgr"), monikerArgs.Items[0]);
        var bindParameters = Assert.IsType<PyTuple>(monikerArgs.Items[2]);
        var nestedList = Py.Tuple(new PyStr("List"), new PyTuple([]), new PyDict([]));
        var bindReply = Assert.IsType<PyTuple>(await trade.CallAsync(Call(session, "MachoBindObject", 7, bindParameters, nestedList)));
        var row = Assert.IsType<PyObject>(bindReply.Items[1]);
        var fields = Assert.IsType<PyDict>(row.Args);
        static PyValue Field(PyDict fields, string name) =>
            fields.Entries.Single(entry => JsSemantics.PlainText(entry.Key) == name).Value;
        var header = Assert.IsType<PyList>(Field(fields, "header")).Items.Select(JsSemantics.PlainText).ToArray();
        Assert.Equal(new[] { "tradeContainerID", "traders", "state", "money", "items" }, header);
        var line = Assert.IsType<PyList>(Field(fields, "line"));
        Assert.Equal(new PyList([new PyLong(actor), new PyLong(targetId)]), line.Items[1]);
        Assert.Single(targetSink.Packets);

        var tradeId = Assert.IsType<PyLong>(bindParameters.Items[0]).Value;
        await using (var command = source.CreateCommand($"SELECT status FROM {Schema("EVE_TRADE_SCHEMA")}.sessions WHERE trade_id=$1"))
        {
            command.Parameters.AddWithValue(tradeId);
            Assert.Equal("open", await command.ExecuteScalarAsync());
        }
    }

    private static ClientSession DockedSession(long characterId, long sessionId, Station station, Sink sink)
    {
        var session = new ClientSession(sink) { CharacterId = characterId, SessionId = sessionId };
        session.Set("charid", new PyInt(characterId));
        session.Set("stationid", new PyInt(station.Id));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        session.Set("regionid", new PyInt(station.RegionId));
        session.Set("corpid", new PyInt(1000044));
        return session;
    }

    private async Task BackdateAsync(NpgsqlDataSource source, long orderId)
    {
        await using var command = source.CreateCommand(
            $"UPDATE {Schema("EVE_MARKET_SCHEMA")}.orders SET issued_ms=$2 WHERE id=$1");
        command.Parameters.AddWithValue(orderId);
        command.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - MarketRules.ModificationDelayMs - 1_000);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }
}
