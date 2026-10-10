using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Market;
using Eve.Services;
using Eve.Sde;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The market service as the real host composition registers it (HostComposition.BuildAsync, the path eve-host runs), on scratch PostgreSQL
/// schemas. The calls go through the registry the dispatcher uses; the state is read back after a restart of the host (persistence).
/// Notifications to a live session are not asserted here: the composition attaches the session at character selection, which the live
/// headless walk covers.
/// </summary>
[Collection("host-composition")]
public sealed class MarketHostTests : IAsyncLifetime
{
    private const int Tritanium = 34;

    /// <summary>The 27 marketProxy methods: the 22 the client calls before M1, and the five M1 adds (BuyMultipleItems, PlacePlexSellOrder, ModifyPlexCharOrder, CharGetTransactions, CorpGetTransactions).</summary>
    private static readonly string[] MarketMethods =
    [
        "StartupCheck", "GetMarketGroups", "GetStationAsks", "GetSystemAsks", "GetRegionBest", "GetOldPriceHistory", "GetNewPriceHistory",
        "GetHistoryForManyTypeIDs", "GetOrders", "GetPlexOrders", "GetPlexBest", "GetPlexHistory", "GetPlexOldPriceHistory", "GetPlexNewPriceHistory",
        "GetCharOrders", "GetCorporationOrders", "GetCharEscrow", "GetMarketOrderHistory", "PlaceBuyOrder", "PlaceMultiSellOrder", "CancelCharOrder",
        "ModifyCharOrder", "BuyMultipleItems", "PlacePlexSellOrder", "ModifyPlexCharOrder", "CharGetTransactions", "CorpGetTransactions",
    ];

    /// <summary>The environment the host reads: every schema on its own scratch name, the live script's list, plus the no-simulation and no-ESI switches.</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA",
    ];

    private readonly string _prefix = "mh" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant() is var name ? $"{_prefix}_{name}" : "";

    public Task InitializeAsync()
    {
        foreach (var variable in SchemaVariables)
        {
            _saved[variable] = Environment.GetEnvironmentVariable(variable);
            var schema = Schema(variable);
            _schemas.Add(schema);
            Environment.SetEnvironmentVariable(variable, schema);
        }
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip") })
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL");
        if (url is not null)
        {
            await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
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

    private static PyValue Line(int typeId, long quantity, double price) => Py.KeyVal(
        ("typeID", new PyInt(typeId)), ("quantity", new PyInt(quantity)), ("price", new PyReal(price)), ("minVolume", new PyInt(1)));

    [PgFact]
    public async Task The_host_registers_the_market_service_and_keeps_its_trades_across_a_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(url));

        var host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        foreach (var method in MarketMethods) Assert.True(market.HasMethod(method), method);

        var stations = await StationCatalog.LoadAsync(new SdeReader(source));
        var station = stations.All.First();
        long actor;
        await using (var c = await source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(c, tx,
                new NewCharacter(1, "Host Trader", 0, 1, 1, 1, 11, 1376, 1000044, null,
                    station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
            await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(c, tx, actor, 10000m, 0);
            await tx.CommitAsync();
        }
        await new MarketStore(source, Schema("EVE_MARKET_SCHEMA")).SeedNpcAsync(
            [new("ask", Tritanium, false, 10, 100, 32767, MarketLocation.From(station), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
        var session = new ClientSession(new Sink()) { SessionId = 301 };
        session.Set("charid", new PyInt(actor));

        // the multi-buy fills the ask at its limit: an empty list back, the wallet charged, and the fill in the transaction tab
        var bought = await market.CallAsync(Call(session, "BuyMultipleItems", 1, new PyInt(station.Id),
            new PyList([Line(Tritanium, 5, 10)]), PyBool.False));
        Assert.Empty(Assert.IsType<PyList>(bought).Items);
        Assert.Equal(9950m, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(actor))!.Isk);
        var transactions = Assert.IsType<PyList>(await market.CallAsync(Call(session, "CharGetTransactions", 2, PyNone.Instance))).Items;
        var fill = Assert.IsType<PyObject>(Assert.Single(transactions));
        Assert.Equal(new PyInt(Tritanium), Assert.IsType<PyDict>(fill.Args).Entries.Single(e => JsSemantics.PlainText(e.Key) == "typeID").Value);

        // a fee quote that differs from the server's rate is the client's own label, with its arguments, on the wire
        await using (var c = await source.OpenConnectionAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            var item = await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA")).CreateAsync(c, tx,
                new NewItem(Tritanium, actor, station.Id, ItemFlags.Hangar, 4));
            await tx.CommitAsync();
            var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => market.CallAsync(Call(session, "PlaceMultiSellOrder", 3,
                new PyList([Py.KeyVal(("typeID", new PyInt(Tritanium)), ("itemID", new PyLong(item)), ("price", new PyReal(9)),
                    ("quantity", new PyInt(4)), ("stationID", new PyInt(station.Id)))]),
                PyBool.False, new PyInt(90), new PyReal(0.01))).AsTask());
            Assert.Equal("MktBrokersFeeUnexpected2", refusal.Error.UserErrorMessage);
        }

        // a restart of the host: the fill and the balance are still there, read by the new host's market service
        await host.ShutdownAsync();
        var restarted = await HostComposition.BuildAsync(Settings());
        var again = restarted.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered after the restart");
        Assert.Single(Assert.IsType<PyList>(await again.CallAsync(Call(session, "CharGetTransactions", 4, PyNone.Instance))).Items);
        Assert.Equal(9950m, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(actor))!.Isk);
        await restarted.ShutdownAsync();
    }
}
