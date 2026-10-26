using System.Text.Json.Nodes;
using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;
using Eve.Services.Core;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class InsuranceRpcHostWireTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
        "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA",
        "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA",
        "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA",
        "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string _prefix = "irw" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];
    private HostRuntime? _host;

    private string Schema(string variable) =>
        _prefix + "_" + variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant();

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
        if (_host is not null)
        {
            await _host.ShutdownAsync();
            _host = null;
        }
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { } url)
        {
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
            foreach (var schema in _schemas)
            {
                await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet) { }
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private static PyValue Field(PyObject row, string name) =>
        Assert.IsType<PyDict>(row.Args).Entries.Single(entry => JsSemantics.PlainText(entry.Key) == name).Value;

    private static long Integer(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected integer wire type: {value.GetType().Name}"),
    };

    private static long Cents(PyValue amount) => amount switch
    {
        PyInt value => checked(value.Value * 100),
        PyLong value => checked(value.Value * 100),
        PyReal value => checked((long)decimal.Round((decimal)value.Value * 100m, 0, MidpointRounding.AwayFromZero)),
        _ => throw new Xunit.Sdk.XunitException($"Unexpected insurance price type: {amount.GetType().Name}"),
    };

    [PgFact]
    public async Task Insurance_rpc_purchase_lookup_list_and_cancellation_follow_the_persisted_host_state()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        const int rifterTypeId = 587;
        const long fullInsurancePriceCents = 100_000_000;
        var gameStoreSchema = Schema("EVE_GAMESTORE_SCHEMA");
        await using (var seed = source.CreateCommand($"""
            CREATE SCHEMA IF NOT EXISTS "{gameStoreSchema}";
            CREATE TABLE IF NOT EXISTS "{gameStoreSchema}"."shipInsurancePrices"
                (key text PRIMARY KEY, doc jsonb NOT NULL, rev bigint NOT NULL DEFAULT 1);
            INSERT INTO "{gameStoreSchema}"."shipInsurancePrices" AS prices (key, doc)
            VALUES ('byTypeID', @doc)
            ON CONFLICT (key) DO UPDATE SET doc = EXCLUDED.doc, rev = prices.rev + 1;
            """))
        {
            var typePrices = new JsonObject
            {
                [rifterTypeId.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new JsonObject { ["fullInsurancePriceCents"] = fullInsurancePriceCents },
            };
            seed.Parameters.AddWithValue("doc", NpgsqlTypes.NpgsqlDbType.Jsonb, typePrices.ToJsonString());
            await seed.ExecuteNonQueryAsync();
        }
        _host = await HostComposition.BuildAsync(Settings());        var insurance = _host.Registry.Lookup("insuranceSvc") ?? throw new InvalidOperationException("insuranceSvc is not registered");
        foreach (var method in new[] { "GetContractForShip", "GetContracts", "GetInsurancePrice", "GetInsurancePrices", "InsureShip", "UnInsureShip" })
            Assert.True(insurance.HasMethod(method), method);

        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        long characterId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            characterId = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(connection, transaction,
                new NewCharacter(1, "Insurance Wire Pilot " + Guid.NewGuid().ToString("N")[..8], 0, 1, 1, 1, 11, 1376,
                    1000044, null, station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
            await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, characterId, 1_000_000m, 0);
            await transaction.CommitAsync();
        }

        long shipId;
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            shipId = await new PgItemStore(source, Schema("EVE_INVENTORY_SCHEMA")).CreateAsync(
                connection, transaction, new NewItem(rifterTypeId, characterId, station.Id, ItemFlags.Hangar, 1));
            await transaction.CommitAsync();
        }

        var session = new ClientSession(new Sink()) { SessionId = 4103, CharacterId = characterId };
        session.Set("charid", new PyInt(characterId));
        session.Set("stationid", new PyLong(station.Id));
        session.Set("solarsystemid2", new PyInt(station.SolarSystemId));

        var price = await insurance.CallAsync(Call(session, "GetInsurancePrice", 1, new PyInt(rifterTypeId)));
        var prices = Assert.IsType<PyDict>(await insurance.CallAsync(
            Call(session, "GetInsurancePrices", 2, new PyList([new PyInt(rifterTypeId)]))));
        Assert.Equal(Cents(price), Cents(prices.Entries.Single().Value));
        Assert.Equal(new PyInt(1_000_000), price);
        Assert.Equal(fullInsurancePriceCents, Cents(price));
        Assert.Same(PyNone.Instance, await insurance.CallAsync(Call(session, "GetContractForShip", 3, new PyLong(shipId))));
        Assert.Empty(Assert.IsType<PyList>(await insurance.CallAsync(Call(session, "GetContracts", 4))).Items);

        var quote = Assert.Single(InsuranceQuotes.ForFullPayoutValue(Cents(price)), item => item.FractionPercent == 50);
        Assert.Same(PyNone.Instance, await insurance.CallAsync(Call(session, "InsureShip", 5, new PyLong(shipId),
            new PyReal(quote.PremiumCents / 100d), PyBool.False)));
        var contract = Assert.IsType<PyObject>(await insurance.CallAsync(Call(session, "GetContractForShip", 6, new PyLong(shipId))));
        Assert.Equal(shipId, Integer(Field(contract, "shipID")));
        Assert.Equal(new PyInt(rifterTypeId), Field(contract, "typeID"));
        Assert.Equal(new PyInt(characterId), Field(contract, "ownerID"));
        Assert.Equal(new PyReal(0.5), Field(contract, "fraction"));
        Assert.Equal(
            new[] { "contractID", "shipID", "typeID", "ownerID", "fraction", "startDate", "endDate" },
            Assert.IsType<PyDict>(contract.Args).Entries.Select(entry => JsSemantics.PlainText(entry.Key)));
        var contracts = Assert.IsType<PyList>(await insurance.CallAsync(Call(session, "GetContracts", 7)));
        var listedContract = Assert.IsType<PyObject>(Assert.Single(contracts.Items));
        Assert.Equal(Field(contract, "contractID"), Field(listedContract, "contractID"));
        Assert.Equal(Field(contract, "shipID"), Field(listedContract, "shipID"));

        var duplicate = await Assert.ThrowsAsync<MachoWrappedException>(() =>
            insurance.CallAsync(Call(session, "InsureShip", 8, new PyLong(shipId),
                new PyReal(quote.PremiumCents / 100d), PyBool.False)).AsTask());
        Assert.Equal("InsureShipFailedSingleContract", duplicate.Error.UserErrorMessage);

        Assert.Same(PyNone.Instance, await insurance.CallAsync(Call(session, "UnInsureShip", 9, new PyLong(shipId))));
        Assert.Same(PyNone.Instance, await insurance.CallAsync(Call(session, "GetContractForShip", 10, new PyLong(shipId))));
        Assert.Empty(Assert.IsType<PyList>(await insurance.CallAsync(Call(session, "GetContracts", 11))).Items);
        Assert.Equal(1_000_000m - quote.PremiumCents / 100m,
            (await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).GetAsync(characterId))!.Isk);
    }
}
