using Eve.Characters;
using Eve.Data;
using Eve.Fitting;
using Eve.Host;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Sde;
using Eve.Structures;
using Eve.Universe;
using Eve.Wallet;
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
    private HostRuntime? host;

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
        foreach (var (name, value) in saved)
            Environment.SetEnvironmentVariable(name, value);
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
    public async Task Registered_repair_service_quotes_charges_tax_and_persists_or_refuses_atomically()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        host = await HostComposition.BuildAsync(Settings());
        var repair = host.Registry.Lookup("repairSvc") ?? throw new InvalidOperationException("repairSvc is not registered");
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
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
        long shipId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await items.CreateAsync(connection, transaction,
                new NewItem(587, character, structureId, ItemFlags.Hangar, 1, Singleton: true));
            await modules.SetShipConditionAsync(connection, transaction, shipId, new ShipCondition(0.5, 1, 0.5, 0.2, false));
            await transaction.CommitAsync();
        }

        var sink = new Sink();
        var session = new ClientSession(sink) { CharacterId = character, SessionId = 801 };
        session.Set("charid", new PyLong(character));
        session.Set("structureid", new PyLong(structureId));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));
        var boundParameters = Py.Tuple(new PyLong(structureId), new PyInt(station.SolarSystemId), new PyInt(6));
        var itemIds = new PyList([new PyLong(shipId)]);
        var quoteCall = Py.Tuple(new PyStr("GetRepairQuotes"), Py.Tuple(itemIds), new PyDict([]));
        var quoteReply = Assert.IsType<PyTuple>(await repair.CallAsync(Call(session, "MachoBindObject", 1, boundParameters, quoteCall)));
        var quotes = Assert.IsType<PyDict>(quoteReply.Items[1]);
        var quote = Assert.Single(Assert.IsType<PyList>(Assert.Single(quotes.Entries).Value).Items);
        Assert.Equal(490, Field(quote, "damage"), 6);
        var unitCost = Field(quote, "costToRepairOneUnitOfDamage");
        var baseUnitCost = Field(quote, "baseCostToRepairOneUnitOfDamage");
        Assert.Equal(0.15, Field(quote, "structureTaxRate"), 6);
        Assert.True(baseUnitCost > 0);
        Assert.Equal(baseUnitCost * 1.15, unitCost, 6);
        var expectedCharge = decimal.Round((decimal)(Math.Ceiling(Field(quote, "damage")) * unitCost), 2, MidpointRounding.AwayFromZero);
        var expectedTax = decimal.Round(expectedCharge * 0.15m / 1.15m, 2, MidpointRounding.AwayFromZero);

        var wallet = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        var beforeIsk = (await wallet.GetAsync(character))!.Isk;
        var repairCall = Py.Tuple(new PyStr("RepairItemsInStructure"), Py.Tuple(itemIds), new PyDict([]));
        var repairReply = Assert.IsType<PyTuple>(await repair.CallAsync(Call(session, "MachoBindObject", 2, boundParameters, repairCall)));
        Assert.Same(PyNone.Instance, repairReply.Items[1]);
        Assert.Equal(beforeIsk - expectedCharge, (await wallet.GetAsync(character))!.Isk);
        Assert.Equal(expectedTax, (await wallet.GetAsync(ownerCorporationId))!.Isk);
        Assert.Equal(-expectedCharge, await wallet.LastIskChangeAsync(character));
        Assert.Equal(expectedTax, await wallet.LastIskChangeAsync(ownerCorporationId));
        var repaired = await modules.ShipConditionAsync(shipId);
        Assert.Equal(0, repaired.Damage, 6);
        Assert.Equal(0, repaired.ArmorDamage, 6);
        Assert.Equal(1, repaired.ShieldCharge, 6);
        Assert.NotEmpty(sink.Packets);

        await host.ShutdownAsync();
        host = await HostComposition.BuildAsync(Settings());
        Assert.Equal(beforeIsk - expectedCharge, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(character))!.Isk);
        repaired = await new PgModuleStateStore(source, Schema("EVE_FITTING_SCHEMA")).ShipConditionAsync(shipId);
        Assert.Equal(0, repaired.Damage, 6);
        Assert.Equal(expectedTax, (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(ownerCorporationId))!.Isk);

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
        var reloadedRepair = host.Registry.Lookup("repairSvc") ?? throw new InvalidOperationException("repairSvc is not registered after restart");
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await reloadedRepair.CallAsync(Call(lowFundsSession, "MachoBindObject", 3, boundParameters, repairCall)));
        Assert.Equal("NotEnoughMoney", refusal.Error.UserErrorMessage);
        Assert.Equal(0, (await wallet.GetAsync(character))!.Isk);
        Assert.Equal(persistedOwnerBalance, (await wallet.GetAsync(ownerCorporationId))!.Isk);
        var unchanged = await modules.ShipConditionAsync(shipId);
        Assert.Equal(490, unchanged.Damage, 6);
        Assert.Empty(lowFundsSink.Packets);
    }
}
