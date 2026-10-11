using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Macho;
using Eve.Insurgency;
using Eve.Marshal;
using Eve.Scheduler;
using Eve.Events;
using Eve.Services;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// The insurgency campaigns through the real host composition (HostComposition.BuildAsync, the path eve-host runs), on scratch PostgreSQL schemas. A campaign is
/// started through the domain service on the host's schemas; the calls go through the registry the dispatcher uses.
/// </summary>
[Collection("host-composition")]
public sealed class InsurgencyCompositionTests : IAsyncLifetime
{
    private const long Jita = 30_000_142;
    private const long Perimeter = 30_000_144;
    private const long Elsewhere = 30_009_999;
    private const int AngelFaction = 500_011;

    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA", "EVE_INSURGENCY_SCHEMA",
    ];

    private readonly string _prefix = "ic" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];

    private string Schema(string variable) => $"{_prefix}_{variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant()}";

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
            await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
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
        public void Send(PyValue packet) { }
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private static ClientSession PilotIn(long solarSystemId, int sessionId)
    {
        var session = new ClientSession(new Sink()) { SessionId = sessionId };
        session.Set("solarsystemid2", new PyInt(solarSystemId));
        return session;
    }

    /// <summary>The object's field by name, from the CampaignClientSnapshot's dict (the names carry the leading underscore on the wire).</summary>
    private static PyValue Field(PyObjectEx snapshot, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyTuple>(snapshot.Header).Items[1]).Entries.Single(entry => entry.Key is PyStr key && key.Value == name).Value;

    [PgFact]
    public async Task A_host_with_no_campaign_answers_the_empty_list_and_a_started_campaign_is_read_through_the_real_composition()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(url));
        var host = await HostComposition.BuildAsync(Settings());
        var campaigns = host.Registry.Lookup("insurgencySolarsystem") ?? throw new InvalidOperationException("insurgencySolarsystem is not registered");
        var pilot = PilotIn(Jita, 501);

        var empty = Assert.IsType<PyList>(await campaigns.CallAsync(Call(pilot, "GetAllVisibleCampaigns", 1)));
        Assert.Empty(empty.Items);
        var none = Assert.IsType<PyList>(await campaigns.CallAsync(Call(pilot, "GetLocalCampaignClientSnapshot", 2)));
        Assert.Equal(new PyInt(Jita), none.Items[0]);
        Assert.Equal(PyNone.Instance, none.Items[1]);

        // the domain starts a campaign on the host's schemas (its jobs and events are the host's, the host runs them)
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var scheduler = new DurableScheduler(source, () => now, schema: Schema("EVE_SCHEDULER_SCHEMA"));
        var bus = new EventBus(source, scheduler, schema: Schema("EVE_EVENTS_SCHEMA"));
        var store = new InsurgencyStore(source, Schema("EVE_INSURGENCY_SCHEMA"));
        var service = new InsurgencyCampaigns(store, scheduler, bus);
        await using (var connection = await source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await service.StartAsync(connection, transaction, new CampaignStart(AngelFaction, 1, Jita, [Jita, Perimeter]));
            await transaction.CommitAsync();
        }

        var visible = Assert.IsType<PyList>(await campaigns.CallAsync(Call(pilot, "GetAllVisibleCampaigns", 3)));
        var campaign = Assert.IsType<PyObjectEx>(Assert.Single(visible.Items));
        Assert.Equal(new PyInt(CampaignStates.Forecasting), Field(campaign, "_fsmState"));
        Assert.Equal(new PyInt(AngelFaction), Field(campaign, "_pirateFactionID"));
        Assert.Equal(new PyInt(Jita), Field(campaign, "_originSolarsystemID"));
        Assert.Equal(new PyInt(5), Field(campaign, "_piratePointsRequired"));
        Assert.Equal(PyNone.Instance, Field(campaign, "_structureID"));

        var local = Assert.IsType<PyList>(await campaigns.CallAsync(Call(pilot, "GetLocalCampaignClientSnapshot", 4)));
        Assert.Equal(new PyInt(Jita), local.Items[0]);
        Assert.IsType<PyObjectEx>(local.Items[1]);
        var outside = Assert.IsType<PyList>(await campaigns.CallAsync(Call(PilotIn(Elsewhere, 502), "GetLocalCampaignClientSnapshot", 5)));
        Assert.Equal(PyNone.Instance, outside.Items[1]);

        await host.ShutdownAsync();
    }
}
