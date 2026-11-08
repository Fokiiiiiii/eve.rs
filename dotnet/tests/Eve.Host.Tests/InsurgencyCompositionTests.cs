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
            await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
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

    private sealed record ClientNotice(string Name, string IdType, PyTuple Args);

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long id, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(id) };

    private static ClientSession PilotIn(long solarSystemId, int sessionId, Sink? sink = null)
    {
        var session = new ClientSession(sink ?? new Sink()) { SessionId = sessionId, CharacterId = sessionId };
        session.Set("solarsystemid2", new PyInt(solarSystemId));
        return session;
    }

    private static List<ClientNotice> ClientNotices(Sink sink)
    {
        var notices = new List<ClientNotice>();
        foreach (var packet in sink.Packets.OfType<PyObject>())
        {
            var fields = ((PyTuple)packet.Args).Items;
            if (fields[0] is not PyInt { Value: MachoMessage.Notification })
                continue;
            var address = ((PyTuple)((PyObject)fields[2]).Args).Items;
            var envelope = (PyTuple)((PyTuple)fields[4]).Items[0];
            var call = (PyTuple)MarshalCodec.Decode(((PyBuffer)envelope.Items[1]).Value);
            var args = (PyTuple)((PyTuple)call.Items[1]).Items[1];
            notices.Add(new(
                JsSemantics.StringValue(address[1]) ?? "",
                JsSemantics.StringValue(address[3]) ?? "",
                args));
        }
        return notices;
    }

    private static async Task PumpAsync(EventBus events)
    {
        for (var i = 0; i < 20; i++)
            if ((await events.PumpAsync()).Delivered == 0)
                break;
    }

    /// <summary>The object's field by name, from the CampaignClientSnapshot's dict (the names carry the leading underscore on the wire).</summary>
    private static PyValue Field(PyObjectEx snapshot, string name) =>
        Assert.IsType<PyDict>(Assert.IsType<PyTuple>(snapshot.Header).Items[1]).Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    [PgFact]
    public async Task A_host_with_no_campaign_answers_the_empty_list_and_a_started_campaign_is_read_through_the_real_composition()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
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

    [PgFact]
    public async Task A_delayed_campaign_deadline_sends_each_transition_with_its_own_client_snapshot()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var host = await HostComposition.BuildAsync(Settings());
        var schedulerSchema = "ins_phase_" + Guid.NewGuid().ToString("N")[..12];
        DurableScheduler? scheduler = null;
        try
        {
            Assert.NotNull(host.Events);
            await PumpAsync(host.Events!); // establish the Host subscriber cursor before the campaign is published
            await DurableScheduler.MigrateAsync(source, schedulerSchema);
            long now = 1_700_000_000_000;
            scheduler = new DurableScheduler(source, () => now, schema: schedulerSchema);
            var store = new InsurgencyStore(source, Schema("EVE_INSURGENCY_SCHEMA"));
            var service = new InsurgencyCampaigns(store, scheduler, host.Events!);
            service.Register();
            var campaign = await StartOnHostAsync(source, service);
            await PumpAsync(host.Events!);

            var sink = new Sink();
            var pilot = PilotIn(Jita, 505, sink);
            host.CharacterSessions!.Enter(pilot.CharacterId!.Value, pilot);
            sink.Packets.Clear();

            now = campaign.MaxEndsMs + 1; // skip both the forecast and maximum-length scheduler runs
            await scheduler.RunDueAsync();
            await PumpAsync(host.Events!);

            var updates = ClientNotices(sink)
                .Where(notice => notice.Name == "OnInsurgencyCampaignUpdatedForLocation")
                .ToList();
            Assert.Equal(2, updates.Count);
            Assert.Equal(
                new long[] { CampaignStates.Active, CampaignStates.NoWinner },
                updates.Select(notice => Assert.IsType<PyInt>(Field(Assert.IsType<PyObjectEx>(notice.Args.Items[0]), "_fsmState")).Value));

            var campaignRpc = host.Registry.Lookup("insurgencySolarsystem")!;
            var visible = Assert.IsType<PyList>(await campaignRpc.CallAsync(Call(pilot, "GetAllVisibleCampaigns", 6)));
            var visibleSnapshot = Assert.Single(visible.Items.OfType<PyObjectEx>(), snapshot =>
                Field(snapshot, "_campaignID") switch
                {
                    PyInt id => id.Value == campaign.CampaignId,
                    PyLong id => id.Value == campaign.CampaignId,
                    _ => false,
                });
            Assert.Equal(new PyInt(CampaignStates.NoWinner), Field(visibleSnapshot, "_fsmState"));
            await using var checkConnection = await source.OpenConnectionAsync();
            Assert.Equal(CampaignStates.NoWinner, (await store.GetAsync(checkConnection, null, campaign.CampaignId, false))!.State);
        }
        finally
        {
            if (scheduler is not null)
                await scheduler.StopAsync();
            await using (var connection = await source.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{schedulerSchema}\" CASCADE", connection))
                await command.ExecuteNonQueryAsync();
            await host.ShutdownAsync();
        }
    }

    private static async Task<Campaign> StartOnHostAsync(NpgsqlDataSource source, InsurgencyCampaigns service)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var campaign = await service.StartAsync(connection, transaction, new CampaignStart(AngelFaction, 1, Jita, [Jita, Perimeter]));
        await transaction.CommitAsync();
        return campaign;
    }
}
