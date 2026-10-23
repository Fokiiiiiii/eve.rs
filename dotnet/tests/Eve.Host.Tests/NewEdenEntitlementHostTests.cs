using Eve.Characters;
using Eve.Data;
using Eve.Identity;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;
using Eve.Sde;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>
/// Omega and Multiple Character Training bought from the New Eden Store, through the real host composition on scratch PostgreSQL schemas:
/// the stored end time and the training slots change as the offer says, the purchase costs nothing, a replayed operation grants nothing,
/// and the account's sessions hear about the change after it is committed.
/// </summary>
[Collection("host-composition")]
public sealed class NewEdenEntitlementHostTests : IAsyncLifetime
{
    private const int PlexTypeId = 44992;
    private static readonly DateTimeOffset Floor = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Lapsed = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The schema variables the host reads, each on its own scratch name (the same list the store host test uses).</summary>
    private static readonly string[] SchemaVariables =
    [
        "EVE_IDENTITY_SCHEMA", "EVE_GLOBAL_CONFIG_SCHEMA", "EVE_CHARACTERS_SCHEMA", "EVE_INVENTORY_SCHEMA", "EVE_SKILLS_SCHEMA", "EVE_WALLET_SCHEMA",
        "EVE_FITTING_SCHEMA", "EVE_NPC_SCHEMA", "EVE_SPACE_SCHEMA", "EVE_GAMESTORE_SCHEMA", "EVE_ASSET_SAFETY_SCHEMA", "EVE_CORPORATIONS_SCHEMA",
        "EVE_MARKET_SCHEMA", "EVE_FLEET_SCHEMA", "EVE_INDUSTRY_SCHEMA", "EVE_SCHEDULER_SCHEMA", "EVE_EVENTS_SCHEMA", "EVE_RELATIONS_SCHEMA",
        "EVE_CONTENT_SCHEMA", "EVE_CRIMEWATCH_SCHEMA",
    ];

    private readonly string _prefix = "ne" + Guid.NewGuid().ToString("N")[..10];
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
                // a background job of this run may still hold a lock on the scratch schema: the drop is retried when the server
                // picks it as the deadlock victim (SQLSTATE 40P01), and fails the test when it keeps failing
                for (var attempt = 1; ; attempt++)
                {
                    try
                    {
                        await using var command = source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                        await command.ExecuteNonQueryAsync();
                        break;
                    }
                    catch (PostgresException error) when (error.SqlState == "40P01" && attempt < 5)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt));
                    }
                }
            }
        }
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }

    /// <summary>Packets the session sends, as hex: a notification is found by its name.</summary>
    private sealed class Packets : IPacketSink
    {
        public List<string> Sent { get; } = [];

        public void Send(PyValue packet) => Sent.Add(Convert.ToHexStringLower(MarshalCodec.Encode(packet)));

        public int Count(string notification)
        {
            var name = Convert.ToHexStringLower(System.Text.Encoding.ASCII.GetBytes(notification));
            return Sent.Count(packet => packet.Contains(name, StringComparison.Ordinal));
        }
    }

    private static HostSettings Settings() => new(
        "", "disabled", new ClusterOptions { NewCharacterExperienceEnabled = false, NewCharacterExperienceMode = "disabled" },
        new AccountOptions { AutoCreate = true }, StrictRpc: true);

    private static ServiceCall Call(ClientSession session, string method, long callId, PyValue[] args, PyDict? kwargs = null) =>
        new() { Session = session, Method = method, Args = args, Kwargs = kwargs ?? new PyDict([]), CallId = new PyInt(callId) };

    private PgAccountDirectory Accounts(NpgsqlDataSource source) =>
        new(source, new AccountOptions { AutoCreate = true }, Schema("EVE_IDENTITY_SCHEMA"));

    /// <summary>A new account, as the login creates it (Omega until 2100).</summary>
    private async Task<uint> NewAccountAsync(PgAccountDirectory accounts, string name) =>
        Assert.IsType<LoggedIn>(await accounts.LoginAsync(new LoginAttempt(name, new byte[] { 9, 9, 9 }, "en", "127.0.0.1"), CancellationToken.None)).AccountId;

    private async Task<long> CreatePilotAsync(NpgsqlDataSource source, Station station, string name, uint accountId)
    {
        await using var c = await source.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var actor = await new PgCharacterStore(source, Schema("EVE_CHARACTERS_SCHEMA")).InsertAsync(c, tx,
            new NewCharacter((int)accountId, name, 0, 1, 1, 1, 11, 1376, 1000044, null,
                station.Id, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        await new PgWalletStore(source, Schema("EVE_WALLET_SCHEMA")).OpenAsync(c, tx, actor, 10000m, 0);
        await tx.CommitAsync();
        return actor;
    }

    private static ClientSession Enter(HostRuntime host, Packets sink, uint accountId, long characterId, int sessionId)
    {
        var session = new ClientSession(sink) { SessionId = sessionId, UserId = accountId, CharacterId = characterId };
        host.CharacterSessions?.Enter(characterId, session);
        return session;
    }

    private static PyValue Field(PyDict fields, string name) =>
        fields.Entries.Single(entry => JsSemantics.StringValue(entry.Key) == name).Value;

    private const long TicksPerDay = 864_000_000_000L;

    private static DateTimeOffset Plus(DateTimeOffset from, TimeSpan span) => from + span;

    private static async Task<long> PlexBalanceAsync(HostRuntime host, ClientSession session, long callId) =>
        Assert.IsType<PyLong>(await host.Registry.Lookup("vaultManager")!.CallAsync(Call(session, "get_account_balance", callId, [new PyStr("PLX")]))).Value;

    private static async Task<PyValue> BuyAsync(HostRuntime host, ClientSession session, long callId, string offer, string currency, long quantity, int? storeId = null)
    {
        var kwargs = storeId is { } store ? Py.Dict(("store_id", new PyInt(store))) : null;
        return await host.Registry.Lookup("storeManager")!.CallAsync(Call(session, "buy_offer", callId,
            [new PyStr(offer), new PyStr(currency), new PyInt(quantity)], kwargs));
    }

    private static async Task<DateTimeOffset> StoredEndAsync(PgAccountDirectory accounts, uint accountId) =>
        (await accounts.SubscriptionEndsAtAsync(accountId, CancellationToken.None)) ?? throw new InvalidOperationException("no account");

    /// <summary>The (slot, end time) pairs of the account's running queues, as the client reads them.</summary>
    private static async Task<Dictionary<long, long>> QueuesAsync(HostRuntime host, ClientSession session, long callId)
    {
        var list = Assert.IsType<PyList>(await host.Registry.Lookup("userSvc")!.CallAsync(Call(session, "GetUpdatedMultiCharacterTraining", callId, [PyBool.False])));
        return list.Items.Select(item => Assert.IsType<PyTuple>(item))
            .ToDictionary(tuple => Number(tuple.Items[0]), tuple => Number(tuple.Items[1]));
    }

    private static DateTimeOffset FromFileTime(long fileTime) => DateTimeOffset.FromFileTime(fileTime);

    private static long Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => throw new InvalidOperationException($"{value} is not a number"),
    };

    [PgFact]
    public async Task An_omega_offer_extends_the_end_time_announces_the_grade_and_a_replayed_purchase_extends_nothing()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await HostComposition.BuildAsync(Settings());
        var accounts = Accounts(source);
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var accountId = await NewAccountAsync(accounts, "omega-buyer");
        accounts.SetSubscriptionEndsAt(accountId, Lapsed);
        var pilot = await CreatePilotAsync(source, station, "Omega Pilot", accountId);
        var session = Enter(host, new Packets(), accountId, pilot, 501);
        var before = await StoredEndAsync(accounts, accountId);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await BuyAsync(host, session, 99, "omega_30_days", "PLX", 1));
        Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", error.Error.UserErrorMessage);
        Assert.Equal(before, await StoredEndAsync(accounts, accountId));
        Assert.Equal(0, await PlexBalanceAsync(host, session, 100));
    }

    [PgFact]
    public async Task An_omega_purchase_never_shortens_and_stops_at_the_2100_floor()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await HostComposition.BuildAsync(Settings());
        var accounts = Accounts(source);
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var accountId = await NewAccountAsync(accounts, "floor-account");
        accounts.SetSubscriptionEndsAt(accountId, Floor);
        var pilot = await CreatePilotAsync(source, station, "Floor Pilot", accountId);
        var session = Enter(host, new Packets(), accountId, pilot, 511);
        var before = await StoredEndAsync(accounts, accountId);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await BuyAsync(host, session, 99, "cash_omega_90_days", "USD", 1, storeId: 9));
        Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", error.Error.UserErrorMessage);
        Assert.Equal(before, await StoredEndAsync(accounts, accountId));
        Assert.Equal(0, await PlexBalanceAsync(host, session, 100));
    }

    [PgFact]
    public async Task A_multiple_character_training_offer_opens_a_free_slot_extends_the_one_that_ends_first_and_notifies_the_account()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await HostComposition.BuildAsync(Settings());
        var accounts = Accounts(source);
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var accountId = await NewAccountAsync(accounts, "training-buyer");
        accounts.SetSubscriptionEndsAt(accountId, Lapsed);
        var pilot = await CreatePilotAsync(source, station, "Training Pilot", accountId);
        var session = Enter(host, new Packets(), accountId, pilot, 521);
        var before = await StoredEndAsync(accounts, accountId);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await BuyAsync(host, session, 99, "mct_slot_30_days", "PLX", 1));
        Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", error.Error.UserErrorMessage);
        Assert.Equal(before, await StoredEndAsync(accounts, accountId));
        Assert.Equal(0, await PlexBalanceAsync(host, session, 100));
    }

    [PgFact]
    public async Task A_six_certificate_offer_adds_six_units_of_30_days_across_the_queues()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await HostComposition.BuildAsync(Settings());
        var accounts = Accounts(source);
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var accountId = await NewAccountAsync(accounts, "six-buyer");
        accounts.SetSubscriptionEndsAt(accountId, Lapsed);
        var pilot = await CreatePilotAsync(source, station, "Six Pilot", accountId);
        var session = Enter(host, new Packets(), accountId, pilot, 531);
        var before = await StoredEndAsync(accounts, accountId);
        var error = await Assert.ThrowsAsync<MachoWrappedException>(async () => await BuyAsync(host, session, 99, "cash_mct_6", "USD", 1, storeId: 9));
        Assert.Equal("STORE_CHECKOUT_UNAVAILABLE", error.Error.UserErrorMessage);
        Assert.Equal(before, await StoredEndAsync(accounts, accountId));
        Assert.Equal(0, await PlexBalanceAsync(host, session, 100));
    }

    [PgFact]
    public async Task A_certificate_activates_once_into_the_queues_and_an_alpha_account_is_refused_with_its_certificate_kept()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var host = await HostComposition.BuildAsync(Settings());
        var accounts = Accounts(source);
        var station = (await StationCatalog.LoadAsync(new SdeReader(source))).All.First();
        var accountId = await NewAccountAsync(accounts, "certificate-holder");
        var pilot = await CreatePilotAsync(source, station, "Certificate Pilot", accountId);
        var sink = new Packets();
        var session = Enter(host, sink, accountId, pilot, 541);
        var userSvc = host.Registry.Lookup("userSvc")!;

        var tokenId = await QaCertificateAsync(userSvc, session, 41, quantity: 2);
        var tokens = new PyList([Py.Tuple(new PyLong(tokenId), PyNone.Instance)]);
        var activated = await QueuesAfterActivationAsync(userSvc, session, 42, pilot, tokens);
        Assert.Equal(new long[] { 2, 3 }, activated.Keys.Order());
        Assert.Equal(1, sink.Count("OnMultipleCharactersTrainingUpdated"));

        // consumed: a replay adds nothing and the token is no longer listed
        var again = await QueuesAfterActivationAsync(userSvc, session, 43, pilot, tokens);
        Assert.Equal(activated, again);
        Assert.Empty(Assert.IsType<PyObjectEx>(await userSvc.CallAsync(Call(session, "GetRedeemTokens", 44, []))).List);

        // an Alpha account cannot activate it, and the certificate stays
        var alpha = await NewAccountAsync(accounts, "alpha-holder");
        accounts.SetSubscriptionEndsAt(alpha, Lapsed);
        var alphaPilot = await CreatePilotAsync(source, station, "Alpha Pilot", alpha);
        var alphaSession = Enter(host, new Packets(), alpha, alphaPilot, 545);
        var alphaToken = await QaCertificateAsync(userSvc, alphaSession, 46, quantity: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await userSvc.CallAsync(Call(alphaSession, "ActivateSoulboundMultiTraining", 47,
            [new PyLong(alphaPilot), new PyList([Py.Tuple(new PyLong(alphaToken), PyNone.Instance)])])));
        Assert.Single(Assert.IsType<PyObjectEx>(await userSvc.CallAsync(Call(alphaSession, "GetRedeemTokens", 48, []))).List);
        Assert.Empty(await QueuesAsync(host, alphaSession, 49));
    }

    private static async Task<long> QaCertificateAsync(IService userSvc, ClientSession session, long callId, long quantity)
    {
        session.Set("accountRole", new PyLong(Eve.Services.GameMasterRoles.Qa)); // the QA token tool is staff-only
        var call = new ServiceCall
        {
            Method = "CreateRedeemTokenQA",
            Args = [],
            Kwargs = new PyDict(
            [
                new(new PyStr("typeID"), new PyInt(34133)),
                new(new PyStr("quantity"), new PyInt(quantity)),
                new(new PyStr("soulbound"), PyBool.True),
                new(new PyStr("label"), new PyStr("MCT certificate")),
                new(new PyStr("description"), new PyStr("test")),
            ]),
            Session = session,
            CallId = new PyInt(callId),
        };
        var payload = Assert.IsType<PyObject>(await userSvc.CallAsync(call));
        return Number(Field(Assert.IsType<PyDict>(payload.Args), "tokenID"));
    }

    private static async Task<Dictionary<long, long>> QueuesAfterActivationAsync(IService userSvc, ClientSession session, long callId, long pilot, PyList tokens)
    {
        var list = Assert.IsType<PyList>(await userSvc.CallAsync(Call(session, "ActivateSoulboundMultiTraining", callId, [new PyLong(pilot), tokens])));
        return list.Items.Select(item => Assert.IsType<PyTuple>(item))
            .ToDictionary(tuple => Number(tuple.Items[0]), tuple => Number(tuple.Items[1]));
    }
}
