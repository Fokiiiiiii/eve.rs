using Eve.Characters;
using Eve.Corporations;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

[Collection("host-composition")]
public sealed class CorporationMedalsHostTests : IAsyncLifetime
{
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA", "EVE_STRUCTURES_SCHEMA",
    ];

    private readonly string _prefix = "cm" + Guid.NewGuid().ToString("N")[..10];
    private readonly Dictionary<string, string?> _saved = [];
    private readonly List<string> _schemas = [];
    private HostRuntime? _host;

    private string Schema(string variable)
    {
        var name = variable["EVE_".Length..^"_SCHEMA".Length].ToLowerInvariant();
        return $"{_prefix}_{name}";
    }

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
        if (Environment.GetEnvironmentVariable("EVE_PG_URL") is { Length: > 0 } url)
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

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, params PyValue[] args) =>
        new() { Session = session, Method = method, Args = args, CallId = new PyInt(1) };

    private static PyValue Field(PyValue value, string key) =>
        Assert.IsType<PyDict>(Assert.IsType<PyObject>(value).Args).Entries.Single(entry => entry.Key == new PyStr(key)).Value;

    private static IReadOnlyList<PyValue> Lines(PyValue rowset) =>
        Assert.IsType<PyList>(Field(rowset, "lines")).Items;

    private static string? NotificationName(PyValue packet)
    {
        if (packet is not PyObject { Name: PyStr name, Args: PyTuple args } ||
            !name.Value.EndsWith("Notification", StringComparison.Ordinal)) return null;
        return MachoAddress.Decode(args.Items[2]).BroadcastId;
    }

    private async Task<long> CreateCharacterAsync(NpgsqlDataSource source, string name)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var id = await characters.InsertAsync(connection, transaction,
            new NewCharacter(1, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                60000001, 30000001, 20000001, 10000001, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(connection, transaction, id, 5_000_000m, 10);
        await transaction.CommitAsync();
        return id;
    }

    [PgFact]
    public async Task Registered_medal_rpcs_roundtrip_state_notifications_and_reject_nonmembers()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        _host = await HostComposition.BuildAsync(Settings());
        var service = _host.Registry.Lookup("corporationSvc") ?? throw new InvalidOperationException("corporationSvc is not registered");
        foreach (var method in new[] { "SetMedalStatus", "GetRecipientsOfMedal", "GiveMedalToCharacters" })
            Assert.True(service.HasMethod(method), method);

        var characters = new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA"));
        var wallets = new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA"));
        var skills = new PgSkillStore(source, Schema("EVE_SKILLS_SCHEMA"));
        var corporations = new CorporationStore(source, characters, wallets,
            new WalletTransactions(source, Schema("EVE_WALLET_SCHEMA")), skills,
            () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Schema("EVE_CORPORATIONS_SCHEMA"));
        var founder = await CreateCharacterAsync(source, "Medal Host Founder");
        var member = await CreateCharacterAsync(source, "Medal Host Member");
        var outsider = await CreateCharacterAsync(source, "Medal Host Outsider");
        var corporation = await corporations.CreateAsync(founder,
            new NewCorporation("Medal Host Corp", "MHCP", "", "", 0.05m), "medal-host-corp");
        var application = await corporations.ApplyAsync(member, corporation.Id, "Join");
        await corporations.RespondAsync(founder, application, 6, "Join", "");
        await corporations.RespondAsync(member, application, 2, "Join", "");

        var memberSink = new Sink();
        var memberSession = new ClientSession(memberSink) { CharacterId = member };
        memberSession.Set("corpid", new PyInt(corporation.Id));
        _host.CharacterSessions!.Enter(member, memberSession);
        var founderSession = new ClientSession(new Sink()) { CharacterId = founder };
        founderSession.Set("corpid", new PyInt(corporation.Id));
        var graphics = new PyList([Py.Tuple(new PyInt(1), new PyWString("emblem.42"), new PyInt(7))]);
        var medalId = Assert.IsType<PyInt>(await service.CallAsync(Call(founderSession, "CreateMedal",
            new PyWString("Service"), new PyWString("For service"), graphics))).Value;

        Assert.IsType<PyNone>(await service.CallAsync(Call(founderSession, "GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyInt(member)]), new PyWString("Service award"), PyBool.True)));
        Assert.Empty(memberSink.Packets);
        founderSession.RunAfterResponse();
        var notice = Assert.Single(memberSink.Packets, packet => NotificationName(packet) == "OnMedalIssued");
        Assert.Equal("clientID", MachoAddress.Decode(Assert.IsType<PyTuple>(Assert.IsType<PyObject>(notice).Args).Items[2]).IdType);

        Assert.IsType<PyNone>(await service.CallAsync(Call(founderSession, "GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyInt(member)]), new PyWString("Repeat award"))));
        founderSession.RunAfterResponse();
        Assert.Equal(2, memberSink.Packets.Count(packet => NotificationName(packet) == "OnMedalIssued"));

        await Assert.ThrowsAsync<CorporationError>(() => service.CallAsync(Call(founderSession, "GiveMedalToCharacters",
            new PyInt(medalId), new PyList([new PyInt(member), new PyInt(outsider)]), new PyWString("Partial award"))).AsTask());
        var recipients = Lines(await service.CallAsync(Call(founderSession, "GetRecipientsOfMedal", new PyInt(medalId))));
        Assert.Equal(2, recipients.Count);
        var recipient = Assert.IsType<PyList>(recipients[0]);
        Assert.Equal(new PyInt(medalId), recipient.Items[0]);
        Assert.Equal(new PyInt(member), recipient.Items[1]);
        Assert.Equal(new PyInt(founder), recipient.Items[2]);
        Assert.Equal(new PyWString("Service award"), recipient.Items[3]);
        Assert.IsType<PyLong>(recipient.Items[4]);
        Assert.Equal(new PyWString("Repeat award"), Assert.IsType<PyList>(recipients[1]).Items[3]);

        Assert.IsType<PyNone>(await service.CallAsync(Call(founderSession, "SetMedalStatus",
            new PyDict([new(new PyInt(medalId), new PyInt(2))]))));
        var received = Assert.IsType<PyTuple>(await service.CallAsync(Call(memberSession, "GetMedalsReceived")));
        var receivedRows = Lines(received.Items[0]).Select(row => Assert.IsType<PyList>(row)).ToArray();
        Assert.Equal(2, receivedRows.Length);
        Assert.All(receivedRows, row => Assert.Equal(new PyInt(2), row.Items[3]));

        await _host.ShutdownAsync();
        _host = await HostComposition.BuildAsync(Settings());
        service = _host.Registry.Lookup("corporationSvc") ?? throw new InvalidOperationException("corporationSvc is not registered after restart");
        recipients = Lines(await service.CallAsync(Call(founderSession, "GetRecipientsOfMedal", new PyInt(medalId))));
        Assert.Equal(2, recipients.Count);
        received = Assert.IsType<PyTuple>(await service.CallAsync(Call(memberSession, "GetMedalsReceived")));
        receivedRows = Lines(received.Items[0]).Select(row => Assert.IsType<PyList>(row)).ToArray();
        Assert.Equal(2, receivedRows.Length);
        Assert.All(receivedRows, row => Assert.Equal(new PyInt(2), row.Items[3]));
    }
}
