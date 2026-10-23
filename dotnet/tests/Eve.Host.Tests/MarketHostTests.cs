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
using Eve.Structures;
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
    private Func<Task>? _shutdownHost;

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
        if (_shutdownHost is { } shutdownHost)
        {
            await shutdownHost();
            _shutdownHost = null;
        }
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

    /// <summary>A character with 10000 ISK docked at the station or supplied location, in the scratch schemas.</summary>
    private async Task<long> CreateTraderAsync(NpgsqlDataSource source, Station station, string name, long corporationId = 1000044, long? currentStationId = null)
    {
        long actor;
        await using var c = await source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(c, tx,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, corporationId, null,
                currentStationId ?? station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(c, tx, actor, 10000m, 0);
        await using (var membership = new NpgsqlCommand($"""
            INSERT INTO {Schema("EVE_RELATIONS_SCHEMA")}.relations
                (kind, subject, object, value, attrs, since_ms, until_ms, updated_ms)
            VALUES ($1, $2, $3, 1, $4::jsonb, $5, NULL, $5)
            """, c, tx))
        {
            membership.Parameters.Add(new NpgsqlParameter { Value = Eve.Relations.RelationKinds.CorporationMember.Name });
            membership.Parameters.Add(new NpgsqlParameter { Value = actor });
            membership.Parameters.Add(new NpgsqlParameter { Value = corporationId });
            membership.Parameters.Add(new NpgsqlParameter { Value = "{}" });
            membership.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            await membership.ExecuteNonQueryAsync();
        }
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

        // the multi-buy fills the ask at its limit: no order is created (the answer is the rowset of created orders, empty), the wallet charged, and the fill in the transaction tab
        var bought = await market.CallAsync(Call(session, "BuyMultipleItems", 1, new PyInt(station.Id),
            new PyList([Line(Tritanium, 5, 10)]), PyBool.False));
        Assert.IsType<PyObject>(bought);
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

    [PgFact]
    public async Task The_host_dispatches_accessible_structure_market_orders_and_recovers_partial_settlement_after_restart()
    {
        var url=Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source=NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
        var host=await HostComposition.BuildAsync(Settings());
        _shutdownHost=()=>host.ShutdownAsync();
        var market=host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var sde=new SdeReader(source);
        var station=(await StationCatalog.LoadAsync(sde)).All.First();
        var fortizar=checked((int)(await sde.ReadAsync("types")).Single(row =>
            row.Doc.TryGetProperty("name",out var name) && SdeReader.English(name)=="Fortizar").Key);
        const long structureId=9_000_000_001_001;
        const long ownerCorporationId=1000044;
        var seller=await CreateTraderAsync(source,station,"Structure Seller",ownerCorporationId,structureId);
        var buyer=await CreateTraderAsync(source,station,"Structure Buyer",ownerCorporationId,structureId);
        var contenderA=await CreateTraderAsync(source,station,"Structure Contender A",ownerCorporationId,structureId);
        var contenderB=await CreateTraderAsync(source,station,"Structure Contender B",ownerCorporationId,structureId);
        var denied=await CreateTraderAsync(source,station,"Structure Denied",1000015,structureId);

        var structures=new StructureStore(source,Schema("EVE_STRUCTURES_SCHEMA"));
        await using (var connection=await source.OpenConnectionAsync())
        await using (var transaction=await connection.BeginTransactionAsync())
        {
            Assert.True(await structures.CreateAsync(connection,transaction,
                new NewStructure(structureId,fortizar,station.SolarSystemId,0,0,0,0,ownerCorporationId,0,"Market Test Fortizar","")));
            await structures.SaveAsync(connection,transaction,
                new StructureSnapshot { StructureId=structureId,State=StructureState.ShieldVulnerable,CoreInstalled=true,Upkeep=UpkeepState.FullPower });
            await structures.SetServiceAsync(connection,transaction,structureId,StructureServiceIds.Market,ServiceState.Online,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await transaction.CommitAsync();
        }

        var sellerSession=new ClientSession(new Sink()) { SessionId=302 };
        sellerSession.Set("charid",new PyInt(seller));
        sellerSession.Set("structureid",new PyLong(structureId));
        var buyerSession=new ClientSession(new Sink()) { SessionId=303 };
        buyerSession.Set("charid",new PyInt(buyer));
        buyerSession.Set("structureid",new PyLong(structureId));
        var deniedSession=new ClientSession(new Sink()) { SessionId=304 };
        deniedSession.Set("charid",new PyInt(denied));
        deniedSession.Set("structureid",new PyLong(structureId));
        var contenderASession=new ClientSession(new Sink()) { SessionId=305 };
        contenderASession.Set("charid",new PyInt(contenderA));
        contenderASession.Set("structureid",new PyLong(structureId));
        var contenderBSession=new ClientSession(new Sink()) { SessionId=306 };
        contenderBSession.Set("charid",new PyInt(contenderB));
        contenderBSession.Set("structureid",new PyLong(structureId));

        static PyValue Received(PyValue value)
        {
            var decoded=MarshalCodec.Decode(MarshalCodec.Encode(value));
            if (decoded is not PyObject { Name:var name } cached || JsSemantics.PlainText(name)!=ObjectCaching.ResultClass)
                return decoded;
            var stream=Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]);
            return stream.Decoded ?? MarshalCodec.Decode(stream.Raw);
        }
        static PyValue Field(PyDict fields,string name)=>
            fields.Entries.Single(entry=>JsSemantics.PlainText(entry.Key)==name).Value;

        var readDenied=await Assert.ThrowsAsync<MachoWrappedException>(() =>
            market.CallAsync(Call(deniedSession,"GetStationAsks",1)).AsTask());
        Assert.Equal("StructureMarketDenied",readDenied.Error.UserErrorMessage);
        var placeDenied=await Assert.ThrowsAsync<MachoWrappedException>(() =>
            market.CallAsync(Call(deniedSession,"PlaceBuyOrder",2,new PyLong(structureId),new PyInt(Tritanium),
                new PyReal(10),new PyInt(1),new PyInt(-1),new PyInt(1),new PyInt(30),PyBool.False,new PyReal(0.01))).AsTask());
        Assert.Equal("StructureMarketDenied",placeDenied.Error.UserErrorMessage);

        long itemId;
        await using (var connection=await source.OpenConnectionAsync())
        await using (var transaction=await connection.BeginTransactionAsync())
        {
            itemId=await new PgItemStore(source,Schema("EVE_INVENTORY_SCHEMA")).CreateAsync(connection,transaction,
                new NewItem(Tritanium,seller,structureId,ItemFlags.Hangar,4));
            await transaction.CommitAsync();
        }
        try
        {
            await market.CallAsync(Call(sellerSession,"PlaceMultiSellOrder",3,
                new PyList([Py.KeyVal(("typeID",new PyInt(Tritanium)),("itemID",new PyLong(itemId)),
                    ("price",new PyReal(10)),("quantity",new PyInt(4)),("stationID",new PyLong(structureId)))]),
                PyBool.False,new PyInt(90),new PyReal(0.01)));
        }
        catch (MachoWrappedException error)
        {
           var detail=error.Error.UserErrorMessage=="CustomInfo" ? InfoText(error) : error.Error.UserErrorMessage;
            Assert.Fail($"The first structure sell order was rejected: {detail}");

        }

        // A 40 ISK Upwell order pays the combined 100 ISK broker minimum.
        Assert.Equal(9900m, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(seller))!.Isk);

        await new MarketStore(source,Schema("EVE_MARKET_SCHEMA")).SeedNpcAsync(
            [new MarketNpcSeed("public-tritanium",Tritanium,false,12,10,MarketRules.RangeRegion,
                MarketLocation.From(station),DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())]);
        var regional=Assert.IsType<PyDict>(Received(await market.CallAsync(Call(deniedSession,"GetRegionBest",4))));
        var publicAsk=Assert.IsType<PyObject>(regional.Entries.Single(entry=>entry.Key==new PyInt(Tritanium)).Value);
        Assert.Equal(new PyLong(station.Id),Field(Assert.IsType<PyDict>(publicAsk.Args),"stationID"));
        var systemAsks=Assert.IsType<PyDict>(Received(await market.CallAsync(Call(deniedSession,"GetSystemAsks",5))));
        var systemAsk=Assert.IsType<PyTuple>(systemAsks.Entries.Single(entry=>entry.Key==new PyInt(Tritanium)).Value);
        Assert.Equal(4,systemAsk.Items.Count);
        Assert.Equal(new PyInt(12),systemAsk.Items[0]);
        Assert.Equal(new PyInt(10),systemAsk.Items[1]);
        Assert.Equal(new PyInt(Tritanium),systemAsk.Items[2]);
        Assert.Equal(new PyLong(station.Id),systemAsk.Items[3]);

        var askResult=await market.CallAsync(Call(buyerSession,"GetStationAsks",4));
        var askDetails=Assert.IsType<PyDict>(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(askResult).Args).Items[0]);
        Assert.Equal(new PyBytes("stationid"u8.ToArray()),Field(askDetails,"sessionInfo"));
        var asks=Assert.IsType<PyDict>(Received(askResult));
        Assert.Contains(asks.Entries,entry=>entry.Key==new PyInt(Tritanium));
        var askSummary=Assert.IsType<PyTuple>(asks.Entries.Single(entry=>entry.Key==new PyInt(Tritanium)).Value);
        Assert.Equal(4,askSummary.Items.Count);
        Assert.Equal(new PyInt(10),askSummary.Items[0]);
        Assert.Equal(new PyInt(4),askSummary.Items[1]);
        Assert.Equal(new PyInt(Tritanium),askSummary.Items[2]);
        Assert.Equal(new PyLong(structureId),askSummary.Items[3]);
        static IReadOnlyList<PyValue> Lines(PyValue value)
        {
            var fields=Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args);
            return Assert.IsType<PyList>(fields.Entries.Single(entry=>JsSemantics.PlainText(entry.Key)=="lines").Value).Items;
        }
        static async Task<(long Owner,long Location,int Flag,long Quantity)> ItemState(
            NpgsqlDataSource database,string inventorySchema,long id)
        {
            await using var command=database.CreateCommand(
                $"SELECT owner_id,location_id,flag,quantity FROM {inventorySchema}.items WHERE id=$1");
            command.Parameters.AddWithValue(id);
            await using var reader=await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return (reader.GetInt64(0),reader.GetInt64(1),reader.GetInt32(2),reader.GetInt64(3));
        }
        var book=Assert.IsType<PyTuple>(Received(await market.CallAsync(Call(buyerSession,"GetOrders",5,new PyInt(Tritanium)))));
        var initialLine=Assert.IsType<PyTuple>(Assert.Single(Lines(book.Items[0]),
            row=>Assert.IsType<PyTuple>(row).Items[10]==new PyLong(structureId)));
        Assert.Equal(new PyInt(4),initialLine.Items[1]);

        var itemBeforeFailure=await ItemState(source,Schema("EVE_INVENTORY_SCHEMA"),itemId);
        var wallet=new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA"));
        var buyerBalanceBeforeFailure=(await wallet.GetAsync(buyer))!.Isk;
        var sellerBalanceBeforeFailure=(await wallet.GetAsync(seller))!.Isk;
        await using (var reject=source.CreateCommand(
            $"ALTER TABLE {Schema("EVE_MARKET_SCHEMA")}.fills ADD CONSTRAINT reject_structure_fill CHECK(quantity=0)"))
            await reject.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<PostgresException>(() => market.CallAsync(Call(buyerSession,"BuyMultipleItems",6,new PyLong(structureId),
            new PyList([Line(Tritanium,2,10)]),PyBool.False)).AsTask());
        Assert.Equal(buyerBalanceBeforeFailure,(await wallet.GetAsync(buyer))!.Isk);
        Assert.Equal(sellerBalanceBeforeFailure,(await wallet.GetAsync(seller))!.Isk);
        Assert.Equal(itemBeforeFailure,await ItemState(source,Schema("EVE_INVENTORY_SCHEMA"),itemId));
        Assert.Empty(Assert.IsType<PyList>(await market.CallAsync(Call(buyerSession,"CharGetTransactions",17,PyNone.Instance))).Items);
        await using (var allow=source.CreateCommand(
            $"ALTER TABLE {Schema("EVE_MARKET_SCHEMA")}.fills DROP CONSTRAINT reject_structure_fill"))
            await allow.ExecuteNonQueryAsync();

        // The failed operation left no receipt; repeating its call ID commits exactly one partial fill.
        await market.CallAsync(Call(buyerSession,"BuyMultipleItems",6,new PyLong(structureId),
            new PyList([Line(Tritanium,2,10)]),PyBool.False));
        Assert.Equal(9980m,(await new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA")).GetAsync(buyer))!.Isk);
        var transactions=Assert.IsType<PyList>(await market.CallAsync(Call(buyerSession,"CharGetTransactions",7,PyNone.Instance)));
        var fill=Assert.IsType<PyObject>(Assert.Single(transactions.Items));
        var location=Assert.IsType<PyDict>(fill.Args).Entries.Single(entry=>JsSemantics.PlainText(entry.Key)=="locationID").Value;
        Assert.Equal(new PyLong(structureId),location);

        await using (var command=source.CreateCommand($"""
            SELECT o.vol_remaining,i.owner_id,i.location_id,i.flag,i.quantity
            FROM {Schema("EVE_MARKET_SCHEMA")}.orders o
            JOIN {Schema("EVE_INVENTORY_SCHEMA")}.items i ON i.id=o.escrow_item_id
            WHERE o.actor_id=$1 AND o.station_id=$2 AND o.type_id=$3 AND NOT o.bid AND o.state=0
            """))
        {
            command.Parameters.AddWithValue(seller);
            command.Parameters.AddWithValue(structureId);
            command.Parameters.AddWithValue(Tritanium);
            await using var reader=await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2L,reader.GetInt64(0));
            Assert.Equal(seller,reader.GetInt64(1));
            Assert.NotEqual(structureId,reader.GetInt64(2));
            Assert.Equal(ItemFlags.Hangar,reader.GetInt32(3));
            Assert.Equal(2L,reader.GetInt64(4));
            Assert.False(await reader.ReadAsync());
        }
        book=Assert.IsType<PyTuple>(Received(await market.CallAsync(Call(buyerSession,"GetOrders",8,new PyInt(Tritanium)))));
        var partialLine=Assert.IsType<PyTuple>(Assert.Single(Lines(book.Items[0]),
            row=>Assert.IsType<PyTuple>(row).Items[10]==new PyLong(structureId)));
        Assert.Equal(new PyInt(2),partialLine.Items[1]);
        var ownOrders=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(sellerSession,"GetCharOrders",18))));
        var ownAsk=Assert.IsType<PyTuple>(Assert.Single(Lines(ownOrders)));
        Assert.Equal(new PyLong(structureId),ownAsk.Items[4]);

        await Task.WhenAll(
            market.CallAsync(Call(contenderASession,"BuyMultipleItems",11,new PyLong(structureId),
                new PyList([Line(Tritanium,2,10)]),PyBool.False)).AsTask(),
            market.CallAsync(Call(contenderBSession,"BuyMultipleItems",12,new PyLong(structureId),
                new PyList([Line(Tritanium,2,10)]),PyBool.False)).AsTask());
        var contenderATransactions=Assert.IsType<PyList>(await market.CallAsync(Call(contenderASession,"CharGetTransactions",13,PyNone.Instance)));
        var contenderBTransactions=Assert.IsType<PyList>(await market.CallAsync(Call(contenderBSession,"CharGetTransactions",14,PyNone.Instance)));
        Assert.Equal(1,contenderATransactions.Items.Count+contenderBTransactions.Items.Count);
        var contenderABalance=(await new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA")).GetAsync(contenderA))!.Isk;
        var contenderBBalance=(await new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA")).GetAsync(contenderB))!.Isk;
        // Only the winner's two-unit immediate fill changes balances; no remainder order is posted.
        Assert.Equal(19_980m,contenderABalance+contenderBBalance);
        Assert.Contains(new[]{contenderABalance,contenderBBalance},balance=>balance==9_980m);
        Assert.Contains(new[]{contenderABalance,contenderBBalance},balance=>balance==10_000m);
        var contenderAOrders=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(contenderASession,"GetCharOrders",21))));
        var contenderBOrders=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(contenderBSession,"GetCharOrders",22))));
        Assert.Empty(Lines(contenderAOrders));
        Assert.Empty(Lines(contenderBOrders));
        await using (var command=source.CreateCommand($"SELECT COALESCE(sum(quantity),0)::bigint FROM {Schema("EVE_INVENTORY_SCHEMA")}.items WHERE owner_id=ANY($1) AND location_id=$2 AND flag=$3 AND type_id=$4"))
        {
            command.Parameters.AddWithValue(new[]{buyer,contenderA,contenderB});
            command.Parameters.AddWithValue(structureId);
            command.Parameters.AddWithValue(ItemFlags.Hangar);
            command.Parameters.AddWithValue(Tritanium);
            Assert.Equal(4L,(long)(await command.ExecuteScalarAsync())!);
        }
        book=Assert.IsType<PyTuple>(Received(await market.CallAsync(Call(buyerSession,"GetOrders",15,new PyInt(Tritanium)))));
        Assert.DoesNotContain(Lines(book.Items[0]),row=>
            Assert.IsType<PyTuple>(row).Items[10]==new PyLong(structureId));

        await market.CallAsync(Call(buyerSession,"PlaceBuyOrder",19,new PyLong(structureId),new PyInt(Tritanium),
            new PyReal(5),new PyInt(1),new PyInt(-1),new PyInt(1),new PyInt(90),PyBool.False,PyNone.Instance));
        var placedBidRows=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(buyerSession,"GetCharOrders",20))));
        var placedBid=Assert.IsType<PyTuple>(Assert.Single(Lines(placedBidRows)));
        var structureBidId=Assert.IsType<PyLong>(placedBid.Items[0]).Value;
        Assert.Equal(new PyLong(structureId),placedBid.Items[4]);
        await using (var backdate=source.CreateCommand(
            $"UPDATE {Schema("EVE_MARKET_SCHEMA")}.orders SET issued_ms=$2 WHERE id=$1"))
        {
            backdate.Parameters.AddWithValue(structureBidId);
            backdate.Parameters.AddWithValue(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-MarketRules.ModificationDelayMs-1000);
            Assert.Equal(1,await backdate.ExecuteNonQueryAsync());
        }
        await market.CallAsync(Call(buyerSession,"ModifyCharOrder",21,new PyLong(structureBidId),new PyReal(4),PyBool.True,
            new PyLong(structureId),new PyInt(station.SolarSystemId),new PyReal(5),new PyInt(-1),new PyInt(1),PyNone.Instance));
        var modifiedBidRows=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(buyerSession,"GetCharOrders",22))));
        var modifiedBid=Assert.IsType<PyTuple>(Assert.Single(Lines(modifiedBidRows)));
        Assert.Equal(new PyLong(structureId),modifiedBid.Items[4]);
        async Task SetMarketServiceAsync(int state)
        {
            await using var connection=await source.OpenConnectionAsync();
            await using var transaction=await connection.BeginTransactionAsync();
            await structures.SetServiceAsync(connection,transaction,structureId,StructureServiceIds.Market,state,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await transaction.CommitAsync();
        }
        var balanceBeforeDeniedCancel=(await new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA")).GetAsync(buyer))!.Isk;
        await SetMarketServiceAsync(ServiceState.Offline);
        var deniedCancel=await Assert.ThrowsAsync<MachoWrappedException>(() =>
            market.CallAsync(Call(buyerSession,"CancelCharOrder",23,new PyLong(structureBidId))).AsTask());
        Assert.Equal("StructureMarketDenied",deniedCancel.Error.UserErrorMessage);
        var stillOpenBidRows=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(buyerSession,"GetCharOrders",24))));
        Assert.Equal(structureBidId,Assert.IsType<PyLong>(Assert.IsType<PyTuple>(Assert.Single(Lines(stillOpenBidRows))).Items[0]).Value);
        Assert.Equal(balanceBeforeDeniedCancel,(await new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA")).GetAsync(buyer))!.Isk);
        await SetMarketServiceAsync(ServiceState.Online);
        await market.CallAsync(Call(buyerSession,"CancelCharOrder",23,new PyLong(structureBidId)));
        var cancelledBids=Assert.IsType<PyObject>(Received(await market.CallAsync(Call(buyerSession,"GetCharOrders",25))));
        Assert.Empty(Lines(cancelledBids));

        await host.ShutdownAsync();
        _shutdownHost=null;
        var restarted=await HostComposition.BuildAsync(Settings());
        _shutdownHost=()=>restarted.ShutdownAsync();
        var recovered=restarted.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered after restart");
        book=Assert.IsType<PyTuple>(Received(await recovered.CallAsync(Call(buyerSession,"GetOrders",16,new PyInt(Tritanium)))));
        Assert.DoesNotContain(Lines(book.Items[0]),row=>
            Assert.IsType<PyTuple>(row).Items[10]==new PyLong(structureId));
        // Canceling the bid refunds escrow, but the two 100 ISK minimum Upwell fees from placing and modifying it remain spent.
        Assert.Equal(9_780m,(await new PgWalletStore(source,Schema("EVE_WALLET_SCHEMA")).GetAsync(buyer))!.Isk);
        Assert.Single(Assert.IsType<PyList>(await recovered.CallAsync(Call(buyerSession,"CharGetTransactions",10,PyNone.Instance))).Items);
        await restarted.ShutdownAsync();
        _shutdownHost=null;
    }
}
