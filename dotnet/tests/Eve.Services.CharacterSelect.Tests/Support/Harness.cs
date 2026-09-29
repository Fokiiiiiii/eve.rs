using Eve.Characters;
using Eve.Data;
using Eve.Fitting;
using Eve.Identity;
using Eve.Inventory;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Skills;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.CharacterSelect.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Harness.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>What a session sends, kept.</summary>
public sealed class Capture : IPacketSink
{
    public List<PyValue> Sent { get; } = [];

    public void Send(PyValue packet) => Sent.Add(packet);
}

/// <summary>The service over scratch schemas, a small catalog and in-memory accounts; the schemas are dropped on dispose.</summary>
public sealed class Harness : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    /// <summary>Two races, two schools each for race 1: enough to exercise creation without the SDE.</summary>
    public static readonly CreationCatalog Catalog = new(
        [new Race(1, 601, "Ibis"), new Race(2, 588, "Reaper")],
        [new Bloodline(1, 1), new Bloodline(2, 1), new Bloodline(3, 2)],
        [
            new StarterLocation(1000006, 500001, 60000610, 30002775, 20000406, 10000033),
            new StarterLocation(1000007, 500001, 60000611, 30002775, 20000406, 10000033),
            new StarterLocation(1000044, 500002, 60000001, 30000001, 20000001, 10000001),
        ]);

    /// <summary>The skills of the small catalog: race 1 starts with three of them, race 2 with one.</summary>
    public static readonly SkillCatalog Skills = new(
        [new SkillType(3300, "Gunnery", 255, "Gunnery", true, 1), new SkillType(3301, "Small Hybrid Turret", 255, "Gunnery", true, 1), new SkillType(3315, "Repair Systems", 1210, "Armor", true, 4)],
        new Dictionary<int, IReadOnlyList<StartingSkill>>
        {
            [1] = [new(3300, 4), new(3301, 1), new(3315, 0)],
            [2] = [new(3300, 3)],
        });

    /// <summary>What a race-1 character starts with: 45255 + 250 + 0 points.</summary>
    public const long Race1Points = 45_505;

    public const long ClockUnixMs = 1_700_000_000_123;

    private readonly string _characterSchema;
    private readonly string _inventorySchema;
    private readonly string _skillsSchema;
    private readonly string _walletSchema;
    private readonly string _fittingSchema;

    private Harness(NpgsqlDataSource source, string suffix)
    {
        Source = source;
        _characterSchema = $"cs_test_{suffix}";
        _inventorySchema = $"csi_test_{suffix}";
        _skillsSchema = $"css_test_{suffix}";
        _walletSchema = $"csw_test_{suffix}";
        _fittingSchema = $"csf_test_{suffix}";
        Characters = new PgCharacterStore(source, _characterSchema);
        Items = new PgItemStore(source, _inventorySchema);
        SkillStore = new PgSkillStore(source, _skillsSchema);
        Wallets = new PgWalletStore(source, _walletSchema);
        Accounts = new InMemoryAccountDirectory(new AccountOptions());
        Online = new OnlineCharacters();
        Service = new CharUnboundMgrService(
            Accounts, Characters, Items, SkillStore, Wallets,
            new CharacterCreation(source, Characters, Items, SkillStore, Skills, Wallets, new StartingWallet(), Catalog, new PgModuleStateStore(source, _fittingSchema)), Catalog, Online,
            [new SkillsLoginHook(SkillStore, Skills, () => ClockUnixMs)], new Random(7));
        Handler = new SkillHandlerService(SkillStore, Skills, () => ClockUnixMs);
        Door = new SkillMgr2Service();
    }

    public NpgsqlDataSource Source { get; }

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore SkillStore { get; }

    public PgWalletStore Wallets { get; }

    public SkillHandlerService Handler { get; }

    public SkillMgr2Service Door { get; }

    public InMemoryAccountDirectory Accounts { get; }

    public OnlineCharacters Online { get; }

    public CharUnboundMgrService Service { get; }

    public static async Task<Harness> CreateAsync()
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var harness = new Harness(source, Guid.NewGuid().ToString("N")[..12]);
        await PgCharacterStore.MigrateAsync(source, harness._characterSchema);
        await PgItemStore.MigrateAsync(source, harness._inventorySchema);
        await PgSkillStore.MigrateAsync(source, harness._skillsSchema);
        await PgWalletStore.MigrateAsync(source, harness._walletSchema);
        await PgModuleStateStore.MigrateAsync(source, harness._fittingSchema);
        return harness;
    }

    /// <summary>A logged-in session of a new account (the account is created by logging in).</summary>
    public async Task<(ClientSession Session, Capture Sink)> LoginAsync(string user, ulong role = 0)
    {
        var accounts = Accounts.WithOptions(new AccountOptions { NewAccountRole = role });
        var login = (LoggedIn)await accounts.LoginAsync(new LoginAttempt(user, new byte[] { 1 }, "EN", "127.0.0.1"), CancellationToken.None);
        var sink = new Capture();
        var id = Interlocked.Increment(ref _sessions);
        var session = new ClientSession(sink) { SessionId = 26_859_804_000_000 + id, ClientId = 20_000_000 + id, UserId = login.AccountId };
        session.Set("accountRole", new PyLong(unchecked((long)login.Role)));
        session.Set("hqID", new PyInt(0));
        session.Closed += () => Online.Leave(session.SessionId);
        return (session, sink);
    }

    private static long _sessions;

    public async ValueTask<PyValue> CallAsync(ClientSession session, string method, params PyValue[] args) =>
        await ((IService)Service).CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    public async ValueTask<PyValue> SkillsAsync(ClientSession session, string method, params PyValue[] args) =>
        await ((IService)Handler).CallAsync(new ServiceCall { Method = method, Args = args, Session = session });

    public async Task<long> ExecuteAsync(string sql)
    {
        await using var command = Source.CreateCommand(sql.Replace("{characters}", _characterSchema, StringComparison.Ordinal));
        return await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in new[] { _characterSchema, _inventorySchema, _skillsSchema, _walletSchema, _fittingSchema })
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
