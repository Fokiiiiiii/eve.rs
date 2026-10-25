using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Market;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class MarketRpcHostWireTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA",
        "EVE_WALLET_SCHEMA", "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA",
        "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA", "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA",
        "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA", "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA",
        "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string prefix = "mrw" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> saved = [];
    private readonly List<string> schemas = [];
    private Eve.Host.HostRuntime? host;

    private string Schema(string variable) =>
        prefix + "_" + variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant();

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
            await using var source = Npgsql.NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(url));
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
        public void Send(PyValue packet) { }
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private static PyValue Received(PyValue value)
    {
        var decoded = MarshalCodec.Decode(MarshalCodec.Encode(value));
        if (decoded is not PyObject { Name: var name } cached || JsSemantics.PlainText(name) != ObjectCaching.ResultClass)
            return decoded;
        var stream = Assert.IsType<PySubStream>(Assert.IsType<PyTuple>(cached.Args).Items[1]);
        return stream.Decoded ?? MarshalCodec.Decode(stream.Raw);
    }

    [PgFact]
    public async Task Market_read_rpc_rows_are_registered_on_the_production_host()
    {
        host = await HostComposition.BuildAsync(Settings());
        var market = host.Registry.Lookup("marketProxy") ?? throw new InvalidOperationException("marketProxy is not registered");
        var session = new ClientSession(new Sink()) { SessionId = 4102 };
        foreach (var method in new[]
        {
            "GetStationAsks", "GetSystemAsks", "CharGetTransactions", "CorpGetTransactions", "GetCharEscrow",
            "GetCharOrders", "GetCorporationOrders", "GetHistoryForManyTypeIDs", "GetPlexNewPriceHistory",
            "GetPlexOldPriceHistory",
        })
            Assert.True(market.HasMethod(method), method);

        Assert.IsType<PyDict>(Received(await market.CallAsync(Call(session, "GetStationAsks", 10))));
        Assert.IsType<PyDict>(Received(await market.CallAsync(Call(session, "GetSystemAsks", 11))));
        Assert.IsType<PyList>(await market.CallAsync(Call(session, "CharGetTransactions", 12, PyNone.Instance)));
        Assert.IsType<PyList>(await market.CallAsync(Call(session, "CorpGetTransactions", 13, PyNone.Instance)));
        Assert.IsType<PyObjectEx>(await market.CallAsync(Call(session, "GetCharEscrow", 14)));
        Assert.IsType<PyObject>(Received(await market.CallAsync(Call(session, "GetCharOrders", 15))));
        Assert.IsType<PyObject>(Received(await market.CallAsync(Call(session, "GetCorporationOrders", 16))));
        var histories = Assert.IsType<PyDict>(await market.CallAsync(
            Call(session, "GetHistoryForManyTypeIDs", 17, new PyList([new PyInt(34)]))));
        Assert.Single(histories.Entries);
        Assert.IsType<PyObject>(Received(await market.CallAsync(Call(session, "GetPlexOldPriceHistory", 18))));
        Assert.IsType<PyObject>(Received(await market.CallAsync(Call(session, "GetPlexNewPriceHistory", 19))));
    }
}
