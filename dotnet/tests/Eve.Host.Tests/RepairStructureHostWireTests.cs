using Eve.Characters;
using Eve.Data;
using Eve.Fitting;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Market;
using Eve.Services.Station;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class RepairStructureHostWireTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
        "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA",
        "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_TRADE_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA",
        "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string prefix = "rsw" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> saved = [];
    private readonly List<string> schemas = [];

    private string Schema(string variable) => $"{prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

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
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
    }

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private async Task<long> CreateCharacterAsync(NpgsqlDataSource source, Station station)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var store = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var character = await store.InsertAsync(connection, transaction,
            new NewCharacter(1, "Structure Repair Pilot", 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, character, 1_000_000_000m, 0);
        await transaction.CommitAsync();
        return character;
    }

    private static double Field(PyValue row, string name)
    {
        var entries = row switch
        {
            PyObjectEx ex => ex.Dict,
            PyObject { Args: PyDict dict } => dict.Entries,
            _ => throw new InvalidOperationException("the quote is not a named row"),
        };
        foreach (var (key, value) in entries)
        {
            if (key is PyStr text && text.Value == name)
                return value switch { PyReal real => real.Value, PyLong number => number.Value, PyInt number => number.Value, _ => throw new InvalidOperationException(name) };
        }
        throw new InvalidOperationException($"no field {name}");
    }

    [PgFact]
    public async Task Registered_repair_service_quotes_free_structure_repairs_and_preserves_paid_station_repairs()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        await PgCharacterStore.MigrateAsync(source, Schema("EVE_CHARACTERS_SCHEMA"));
        await PgItemStore.MigrateAsync(source, Schema("EVE_INVENTORY_SCHEMA"));
        await PgWalletStore.MigrateAsync(source, Schema("EVE_WALLET_SCHEMA"));
        await PgModuleStateStore.MigrateAsync(source, Schema("EVE_FITTING_SCHEMA"));
        await StructureStore.MigrateAsync(source, Schema("EVE_STRUCTURES_SCHEMA"));
        var sde = new SdeReader(source);
        var stationCatalog = await StationCatalog.LoadAsync(sde);
        var station = stationCatalog.All.First();
        var character = await CreateCharacterAsync(source, station);
        const long structureId = 9_000_000_000_000_001;
        const long ownerCorporationId = 99_000_000_001;
        var structures = new StructureStore(source, Schema("EVE_STRUCTURES_SCHEMA"));

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            Assert.True(await structures.CreateAsync(connection, transaction,
                new NewStructure(structureId, 35832, station.SolarSystemId, 1, 2, 3, 0, ownerCorporationId, 0, "Repair Test", "")));
            await structures.SaveAsync(connection, transaction, new StructureSnapshot
            {
                StructureId = structureId,
                State = StructureState.ShieldVulnerable,
                Upkeep = UpkeepState.FullPower,
                CoreInstalled = true,
            });
            await transaction.CommitAsync();
        }

        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, ownerCorporationId, 0, 0);
            await using var command = new NpgsqlCommand(
                $"UPDATE {Schema("EVE_STRUCTURES_SCHEMA")}.structures SET doc = jsonb_build_object('industryTaxRate', 0.15) WHERE structure_id = $1", connection, transaction);
            command.Parameters.AddWithValue(structureId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            await transaction.CommitAsync();
        }

        var items = new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA"));
        var modules = new PgModuleStateStore(source, Schema("EVE_FITTING_SCHEMA"));
        var walletSchema = Schema("EVE_WALLET_SCHEMA");
        var wallet = new PgWalletStore(source, walletSchema);
        var transactions = new WalletTransactions(source, walletSchema);
        var marketSchema = Schema("EVE_MARKET_SCHEMA");
        await MarketStore.MigrateAsync(source, marketSchema);
        var prices = new AdjustedPriceCache(new AdjustedPriceStore(source, marketSchema),
            new EsiAdjustedPrices(new HttpClient(), EsiAdjustedPrices.DefaultBase),
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var itemTypes = new ItemTypeCatalog(sde);
        var dogma = await DogmaStatic.LoadAsync(sde);
        RepairService NewRepairService() => new(source, items, itemTypes, stationCatalog, dogma, modules,
            prices, transactions, wallet, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), structures);
        var registry = new ServiceRegistry();
        registry.Register(NewRepairService());
        var repair = registry.Lookup("repairSvc") ?? throw new InvalidOperationException("repairSvc is not registered");
        long shipId;
        long stationShipId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await items.CreateAsync(connection, transaction,
                new NewItem(587, character, structureId, ItemFlags.Hangar, 1, Singleton: true));
            await modules.SetShipConditionAsync(connection, transaction, shipId, new ShipCondition(0.5, 1, 0.5, 0.2, false));
            stationShipId = await items.CreateAsync(connection, transaction,
                new NewItem(587, character, station.Id, ItemFlags.Hangar, 1, Singleton: true));
            await modules.SetShipConditionAsync(connection, transaction, stationShipId, new ShipCondition(0.5, 1, 0.5, 0.2, false));
            await transaction.CommitAsync();
        }

        var sink = new Sink();
        var session = new ClientSession(sink) { CharacterId = character, SessionId = 801 };
        session.Set("charid", new PyLong(character));
        session.Set("structureid", new PyLong(structureId));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        var dispatcher = new PacketDispatcher(registry) { StrictRpc = true };
        var open = new SessionOpen(801, 801, 1, 0, "127.0.0.1", "EN", "US");
        async Task<PyValue> WireCall(long callId, PyTuple boundParameters, PyTuple boundCall)
        {
            var packetsBefore = sink.Packets.Count;
            Assert.True(await dispatcher.DispatchAsync(
                Packets.Call(open, callId, "repairSvc", "MachoBindObject", boundParameters, boundCall), session));
            Assert.True(sink.Packets.Count > packetsBefore);
            var response = Assert.IsType<PyObject>(sink.Packets[^1]);
            var responseFields = Assert.IsType<PyTuple>(response.Args).Items;
            Assert.Equal(7, Assert.IsType<PyInt>(responseFields[0]).Value);
            var payload = Assert.IsType<PyTuple>(responseFields[4]);
            return payload.Items[0] is PySubStream { Decoded: { } result } ? result : payload.Items[0];
        }
        var boundParameters = Py.Tuple(new PyLong(structureId), new PyInt(station.SolarSystemId), new PyInt(6));
        var itemIds = new PyList([new PyLong(shipId)]);
        var quoteCall = Py.Tuple(new PyStr("GetRepairQuotes"), Py.Tuple(itemIds), new PyDict([]));
        var quoteReply = Assert.IsType<PyTuple>(await WireCall(1, boundParameters, quoteCall));
        var quotes = Assert.IsType<PyDict>(quoteReply.Items[1]);
        var quote = Assert.Single(Assert.IsType<PyList>(Assert.Single(quotes.Entries).Value).Items);
        var quotedDamage = Field(quote, "damage");
        Assert.True(quotedDamage > 0);
        var unitCost = Field(quote, "costToRepairOneUnitOfDamage");
        var baseUnitCost = Field(quote, "baseCostToRepairOneUnitOfDamage");
        Assert.Equal(0, Field(quote, "structureTaxRate"), 6);
        Assert.True(baseUnitCost > 0);
        Assert.Equal(0, unitCost, 6);
        var expectedCharge = 0m;
        var expectedTax = 0m;

        var beforeIsk = (await wallet.GetAsync(character))!.Isk;
        var repairCall = Py.Tuple(new PyStr("RepairItemsInStructure"), Py.Tuple(itemIds), new PyDict([]));
        var repairReply = Assert.IsType<PyTuple>(await WireCall(2, boundParameters, repairCall));
        Assert.Same(PyNone.Instance, repairReply.Items[1]);
        Assert.Equal(beforeIsk - expectedCharge, (await wallet.GetAsync(character))!.Isk);
        Assert.Equal(expectedTax, (await wallet.GetAsync(ownerCorporationId))!.Isk);
        Assert.Equal(-expectedCharge, await wallet.LastIskChangeAsync(character));
        Assert.Equal(expectedTax, await wallet.LastIskChangeAsync(ownerCorporationId));
        var repaired = await modules.ShipConditionAsync(shipId);
        Assert.Equal(0, repaired.Damage, 6);
        Assert.Equal(0, repaired.ArmorDamage, 6);
        Assert.Equal(1, repaired.ShieldCharge, 6);
        Assert.Contains(sink.Packets, packet => MachoPacket.Decode(packet) is
            { Type: MachoMessage.Notification, Dest.BroadcastId: "OnItemChange" });

        registry = new ServiceRegistry();
        registry.Register(NewRepairService());
        repair = registry.Lookup("repairSvc") ?? throw new InvalidOperationException("repairSvc is not registered after restart");
        dispatcher = new PacketDispatcher(registry) { StrictRpc = true };
        Assert.Equal(beforeIsk - expectedCharge, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(character))!.Isk);
        repaired = await new PgModuleStateStore(source, Schema("EVE_FITTING_SCHEMA")).ShipConditionAsync(shipId);
        Assert.Equal(0, repaired.Damage, 6);
        Assert.Equal(expectedTax, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(ownerCorporationId))!.Isk);

        var stationBound = Py.Tuple(new PyLong(station.Id), new PyInt(station.SolarSystemId), new PyInt(6));
        var stationItems = new PyList([new PyLong(stationShipId)]);
        session.Set("stationid", new PyLong(station.Id));
        var stationQuoteReply = Assert.IsType<PyTuple>(await WireCall(4, stationBound,
            Py.Tuple(new PyStr("GetRepairQuotes"), Py.Tuple(stationItems), new PyDict([]))));
        var stationQuotes = Assert.IsType<PyDict>(stationQuoteReply.Items[1]);
        var stationQuote = Assert.Single(Assert.IsType<PyList>(Assert.Single(stationQuotes.Entries).Value).Items);
        var stationDamage = Field(stationQuote, "damage");
        Assert.True(stationDamage > 0);
        var stationCost = decimal.Round((decimal)(Math.Ceiling(stationDamage) * Field(stationQuote, "costToRepairOneUnitOfDamage")), 2, MidpointRounding.AwayFromZero);
        var stationRepair = Assert.IsType<PyTuple>(await WireCall(5, stationBound,
            Py.Tuple(new PyStr("RepairItemsInStation"), Py.Tuple(stationItems, PyNone.Instance), new PyDict([]))));
        Assert.Same(PyNone.Instance, stationRepair.Items[1]);
        Assert.Equal(beforeIsk - expectedCharge - stationCost, (await wallet.GetAsync(character))!.Isk);
        Assert.Equal(0, (await modules.ShipConditionAsync(stationShipId)).Damage, 6);

        var lowFundsSink = new Sink();
        var lowFundsSession = new ClientSession(lowFundsSink) { CharacterId = character, SessionId = 802 };
        lowFundsSession.Set("charid", new PyLong(character));
        lowFundsSession.Set("structureid", new PyLong(structureId));
        lowFundsSession.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await modules.SetShipConditionAsync(connection, transaction, shipId, new ShipCondition(0.5, 1, 0.5, 0.2, false));
            await using var command = new NpgsqlCommand(
                $"UPDATE {Schema("EVE_WALLET_SCHEMA")}.balances SET isk = 0 WHERE owner_id = $1 AND account_key = 1000", connection, transaction);
            command.Parameters.AddWithValue(character);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            await transaction.CommitAsync();
        }
        var persistedOwnerBalance = (await wallet.GetAsync(ownerCorporationId))!.Isk;
        Assert.True(await dispatcher.DispatchAsync(
            Packets.Call(open, 3, "repairSvc", "MachoBindObject", boundParameters, repairCall), lowFundsSession));
        Assert.Contains(lowFundsSink.Packets, packet => MachoPacket.Decode(packet) is
            { Type: MachoMessage.Notification, Dest.BroadcastId: "OnItemChange" });
        Assert.Equal(0, (await wallet.GetAsync(character))!.Isk);
        Assert.Equal(persistedOwnerBalance, (await wallet.GetAsync(ownerCorporationId))!.Isk);
        var repairedWithoutFunds = await modules.ShipConditionAsync(shipId);
        Assert.Equal(ShipCondition.Full, repairedWithoutFunds);
    }
}
