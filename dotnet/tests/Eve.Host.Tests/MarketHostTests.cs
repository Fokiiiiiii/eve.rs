using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Market;
using Eve.Services.Market;
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
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
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
        foreach (var (name, value) in new[] { ("EVE_SIM", "off"), ("EVE_MARKET_SEED", "skip"), ("EVE_WORMHOLES", "off") })
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
            await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
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

    /// <summary>A character with 10000 ISK docked at the station, in the scratch schemas.</summary>
    private async Task<long> CreateTraderAsync(NpgsqlDataSource source, Station station, string name)
    {
        long actor;
        await using var c = await source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(c, tx,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(c, tx, actor, 10000m, 0);
        await tx.CommitAsync();
        return actor;
    }

    /// <summary>The labelled refusal's sentence: a CustomInfo carries it in its "info" argument.</summary>
    private static string InfoText(MachoWrappedException error)
    {
        var header = Assert.IsType<PyTuple>(Assert.IsType<PyObjectEx>(error.Error.Payload).Header);
        var arguments = Assert.IsType<PyDict>(Assert.IsType<PyTuple>(header.Items[1]).Items[1]);
        return Assert.IsType<PyStr>(arguments.Entries.Single(e => JsSemantics.PlainText(e.Key) == "info").Value).Value;
    }

    [PgFact]
    public async Task The_host_registers_the_marketStoreservice_and_keeps_its_trades_across_a_restart()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));

        var host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        foreach (var method in MarketMethods) Assert.True(market.HasMethod(method), method);

        var stations = await StationCatalog.LoadAsync(new SdeReader(source));
        var station = stations.All.First();
        var actor = await CreateTraderAsync(source, station, "Host Trader");
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

    [PgFact]
    public async Task The_host_refuses_an_off_tick_price_a_fast_modification_a_stale_view_and_a_repeat_on_the_wire()
    {
        // M2 (Q12, Q3, Q15, Q14): each refusal is the labelled CustomInfo the client shows, and a refused call changes nothing.
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Tick Trader");
        var session = new ClientSession(new Sink()) { SessionId = 302 };
        session.Set("charid", new PyInt(actor));
        const int Pyerite = 35; // no NPC ask is seeded for it, so the bid rests
        var wallet = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        var marketStore = new MarketStore(source, Schema("EVE_MARKET_SCHEMA"));

        // Q12: five significant figures are refused on place, with the client's sentence, and no ISK is escrowed
        var tick = await Assert.ThrowsAsync<MachoWrappedException>(() => market.CallAsync(Call(session, "PlaceBuyOrder", 1, new PyInt(station.Id),
            new PyInt(Pyerite), new PyReal(12345), new PyInt(1), new PyInt(-1), new PyInt(1), new PyInt(90), PyBool.False, PyNone.Instance)).AsTask());
        Assert.Equal("CustomInfo", tick.Error.UserErrorMessage);
        Assert.Equal("Order prices have at most four significant figures.", InfoText(tick));
        Assert.Equal(10000m, (await wallet.GetAsync(actor))!.Isk);

        // a valid bid rests; a repeat of the same placement within five seconds is refused (Q14), and the balance shows one escrow only
        await market.CallAsync(Call(session, "PlaceBuyOrder", 2, new PyInt(station.Id), new PyInt(Pyerite), new PyReal(1234), new PyInt(1),
            new PyInt(-1), new PyInt(1), new PyInt(90), PyBool.False, PyNone.Instance));
        var repeat = await Assert.ThrowsAsync<MachoWrappedException>(() => market.CallAsync(Call(session, "PlaceBuyOrder", 3, new PyInt(station.Id),
            new PyInt(Pyerite), new PyReal(1234), new PyInt(1), new PyInt(-1), new PyInt(1), new PyInt(90), PyBool.False, PyNone.Instance)).AsTask());
        Assert.Equal("An identical order was placed a moment ago, so it was not placed again.", InfoText(repeat));
        var order = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order;
        var escrowed = (await wallet.GetAsync(actor))!.Isk;
        Assert.True(escrowed < 10000m && escrowed > 8000m, escrowed.ToString());

        // Q3: a modification inside the five-minute delay is refused with the seconds left
        var fileTime = Py.FileTimeFromUnixMs(order.IssuedMs);
        ServiceCall Modify(long id, double newPrice, double oldPrice, long issueFileTime) => Call(session, "ModifyCharOrder", id,
            new PyLong(order.Id), new PyReal(newPrice), PyBool.True, new PyInt(station.Id), new PyInt(station.SolarSystemId), new PyReal(oldPrice),
            new PyInt(-1), new PyInt(1), new PyLong(issueFileTime));
        var early = await Assert.ThrowsAsync<MachoWrappedException>(() => market.CallAsync(Modify(4, 1235, 1234, fileTime)).AsTask());
        Assert.StartsWith("That order was placed or modified less than five minutes ago; it can be modified in ", InfoText(early));

        // Q15: a stale view (an oldPrice the order no longer has) is refused first, with the stale sentence
        var stale = await Assert.ThrowsAsync<MachoWrappedException>(() => market.CallAsync(Modify(5, 1235, 1000, fileTime)).AsTask());
        Assert.Equal("That order has changed since it was shown. Reload the orders and try again.", InfoText(stale));

        // the refusals changed nothing: the order keeps its price and its escrow
        var kept = Assert.Single(await marketStore.OwnOrdersAsync(actor, actor, false)).Order;
        Assert.Equal(1234m, kept.Price);
        Assert.Equal(escrowed, (await wallet.GetAsync(actor))!.Isk);
        await host.ShutdownAsync();
    }

    [PgFact]
    public async Task The_host_gives_each_resting_order_a_durable_expiry_job_that_survives_a_restart()
    {
        // M3 (Q7): the job is due at the expiry plus the grace, is kept in the scheduler's schema, and the minute sweep is scheduled
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var scheduler = host.Scheduler ?? throw new InvalidOperationException("the host has no durable scheduler");
        Assert.Contains(MarketExpiryWorker.OrderKind, scheduler.Kinds);
        Assert.Contains(MarketExpiryWorker.SweepKind, scheduler.Kinds);
        Assert.NotNull(await scheduler.FindAsync(MarketExpiryWorker.SweepKind, "sweep"));
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var actor = await CreateTraderAsync(source, station, "Expiry Host Trader");
        var session = new ClientSession(new Sink()) { SessionId = 303 };
        session.Set("charid", new PyInt(actor));
        const int Pyerite = 35;
        await market.CallAsync(Call(session, "PlaceBuyOrder", 1, new PyInt(station.Id), new PyInt(Pyerite), new PyReal(1234), new PyInt(1),
            new PyInt(-1), new PyInt(1), new PyInt(1), PyBool.False, PyNone.Instance));
        var order = Assert.Single(await new MarketStore(source, Schema("EVE_MARKET_SCHEMA")).OwnOrdersAsync(actor, actor, false)).Order;
        var job = await scheduler.FindAsync(MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(order.Id));
        Assert.NotNull(job);
        Assert.Equal(order.IssuedMs + 86_400_000L + MarketExpiry.GraceMs, job!.RunAtMs);
        await host.ShutdownAsync();

        var restarted = await HostComposition.BuildAsync(Settings());
        var again = restarted.Scheduler ?? throw new InvalidOperationException("the host has no durable scheduler after the restart");
        var resumed = await again.FindAsync(MarketExpiryWorker.OrderKind, MarketExpiryWorker.OrderKey(order.Id));
        Assert.NotNull(resumed);
        Assert.Equal(job.RunAtMs, resumed!.RunAtMs);
        await restarted.ShutdownAsync();
    }
}
