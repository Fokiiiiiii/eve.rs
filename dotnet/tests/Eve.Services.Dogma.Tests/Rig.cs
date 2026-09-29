using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Inventory;
using Eve.Ships;
using Eve.Skills;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Dogma.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL with the SDE loaded (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>Keeps what the server sent, so a test can read the events.</summary>
internal sealed class Sink : IPacketSink
{
    public List<PyValue> Sent { get; } = [];

    public void Send(PyValue packet) => Sent.Add(packet);
}

/// <summary>Real stores in throwaway schemas, the real static data, and a character made the way the game makes one.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long ClockMs = 1_700_000_000_123;
    public const long Station = 60015249;

    private readonly string[] _schemas;

    private Rig(NpgsqlDataSource source, string suffix, SdeReader sde, DogmaStatic statics, CharacterCreation creation, PgModuleStateStore modules)
    {
        Source = source;
        _schemas = [$"dc_{suffix}", $"di_{suffix}", $"ds_{suffix}", $"dw_{suffix}", $"df_{suffix}"];
        Characters = new PgCharacterStore(source, _schemas[0]);
        Items = new PgItemStore(source, _schemas[1]);
        Skills = new PgSkillStore(source, _schemas[2]);
        Statics = statics;
        Creation = creation;
        Modules = modules;
        Ships = new CharacterShips(source, statics, Characters, Items, Skills, modules);
        Service = new DogmaIMService(statics, Ships, () => ClockMs);
        Inventory = new InvBrokerService(Items, new ItemTypeCatalog(sde), new Eve.Universe.StationCatalog([]), Ships, statics, () => ClockMs);
    }

    public NpgsqlDataSource Source { get; }

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore Skills { get; }

    public DogmaStatic Statics { get; }

    public PgModuleStateStore Modules { get; }

    public CharacterShips Ships { get; }

    public CharacterCreation Creation { get; }

    public DogmaIMService Service { get; }

    public InvBrokerService Inventory { get; }

    public static async Task<Rig> CreateAsync()
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var characters = new PgCharacterStore(source, $"dc_{suffix}");
        var items = new PgItemStore(source, $"di_{suffix}");
        var skills = new PgSkillStore(source, $"ds_{suffix}");
        var wallets = new PgWalletStore(source, $"dw_{suffix}");
        var modules = new PgModuleStateStore(source, $"df_{suffix}");
        await PgCharacterStore.MigrateAsync(source, $"dc_{suffix}");
        await PgItemStore.MigrateAsync(source, $"di_{suffix}");
        await PgSkillStore.MigrateAsync(source, $"ds_{suffix}");
        await PgWalletStore.MigrateAsync(source, $"dw_{suffix}");
        await PgModuleStateStore.MigrateAsync(source, $"df_{suffix}");
        var creation = new CharacterCreation(
            source, characters, items, skills, await SkillCatalog.LoadAsync(sde), wallets, new StartingWallet(), await CreationCatalog.LoadAsync(sde, Station), modules);
        return new Rig(source, suffix, sde, await DogmaStatic.LoadAsync(sde), creation, modules);
    }

    /// <summary>A new character of a race (Caldari by default), in its rookie ship in the station.</summary>
    public async Task<Created> NewCharacterAsync(string name = "Dogma Tester", int race = 1, int bloodline = 1, int school = 35, int account = 1)
    {
        var made = await Creation.CreateAsync(new CreationRequest(account, name, race, bloodline, 1, 10, school, [1], [2]));
        return Assert.IsType<Created>(made);
    }

    public static ClientSession Session(long? character, long id = 1, long? station = Station)
    {
        var session = new ClientSession(new Sink()) { SessionId = id, ClientId = id, UserId = 34, NodeId = 65450 };
        if (station is { } s)
        {
            session.Set("stationid", new PyInt(s));
        }
        if (character is { } c)
        {
            session.CharacterId = c;
        }
        return session;
    }

    public async Task<PyValue> CallAsync(ClientSession session, string method, string? bound = null, params PyValue[] args) =>
        await ((IService)Service).CallAsync(new ServiceCall { Method = method, Args = args, Session = session, CallId = new PyInt(1), BoundObjectId = bound });

    public async Task<PyValue> InventoryAsync(ClientSession session, string method, string? bound = null, PyValue[]? args = null, PyValue? kwargs = null) =>
        await ((IService)Inventory).CallAsync(new ServiceCall { Method = method, Args = args ?? [], Kwargs = kwargs ?? PyNone.Instance, Session = session, CallId = new PyInt(1), BoundObjectId = bound });

    /// <summary>Binds dogma to the station and returns the bound object's id.</summary>
    public async Task<string> BindAsync(ClientSession session)
    {
        var answer = (PyTuple)await CallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(Station), new PyInt(15)), PyNone.Instance);
        return OidOf(answer.Items[0]);
    }

    public static string OidOf(PyValue bound) =>
        JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)bound).Value).Decoded!).Items[0])!;

    public async ValueTask DisposeAsync()
    {
        foreach (var schema in _schemas)
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
