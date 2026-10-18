using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services.Inventory;
using Eve.Services.Slash;
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
    private readonly object _gate = new();
    private readonly List<PyValue> _sent = [];

    /// <summary>What was sent, as a copy (a reload that ends in the background sends while a test reads).</summary>
    public IReadOnlyList<PyValue> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    /// <summary>What was sent, and forgets it, in one step: nothing sent in between is lost.</summary>
    public IReadOnlyList<PyValue> Take()
    {
        lock (_gate)
        {
            var taken = _sent.ToArray();
            _sent.Clear();
            return taken;
        }
    }

    public void Send(PyValue packet)
    {
        lock (_gate)
        {
            _sent.Add(packet);
        }
    }
}

/// <summary>Real stores in throwaway schemas, the real static data, and a character made the way the game makes one.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long ClockMs = 1_700_000_000_123;
    public const long Station = 60015249;

    private readonly string[] _schemas;

    private Rig(NpgsqlDataSource source, string suffix, SdeReader sde, DogmaStatic statics, CharacterCreation creation, PgModuleStateStore modules, SkillBoosterActivationHandler? activateBooster, SkillCatalog catalog)
    {
        Source = source;
        Catalog = catalog;
        _schemas = [$"dc_{suffix}", $"di_{suffix}", $"ds_{suffix}", $"dw_{suffix}", $"df_{suffix}", $"dk_{suffix}", $"de_{suffix}", $"dr_{suffix}"];
        Characters = new PgCharacterStore(source, _schemas[0]);
        Items = new PgItemStore(source, _schemas[1]);
        Skills = new PgSkillStore(source, _schemas[2]);
        Statics = statics;
        Creation = creation;
        Modules = modules;
        Ships = new CharacterShips(source, statics, Characters, Items, Skills, modules);
        Timers = new Eve.Scheduler.LocalTimers(delay: Delay.WaitAsync);
        var boosterActivation = activateBooster ?? ((long _, long _, string _, long? _, long? _, CancellationToken _) => ValueTask.FromResult<bool?>(null));
        Service = new DogmaIMService(statics, Ships, () => ClockMs, boosterActivation, null, Targeting, SpaceModules, Health, Rounds, Delay.WaitAsync, localTimers: Timers).ListeningForDryGuns();
        Transactions = new WalletTransactions(source, _schemas[3]);
        Wallets = new PgWalletStore(source, _schemas[3]);
        Loyalty = new Eve.Wallet.LoyaltyPointStore(source, _schemas[3]);
        Scheduler = new Eve.Scheduler.DurableScheduler(source, () => ClockMs, new Eve.Scheduler.SchedulerOptions { Concurrency = 1 }, _schemas[5]);
        Bus = new Eve.Events.EventBus(source, Scheduler, null, _schemas[6]);
        Relations = new Eve.Relations.RelationStore(source, Scheduler, Bus, _schemas[7]);
        Slash = new SlashService(new ShipHealthCommands(Ships, statics, () => ClockMs, Health), items: new ItemCommands(new ItemTypeNames(sde), Ships, statics, () => ClockMs), isk: new IskCommands(Transactions, source, () => ClockMs, Wallets, Loyalty),
            loyalty: new LoyaltyCommands(Loyalty, StandingDirectory, () => ClockMs),
            standings: new StandingCommands(StandingDirectory, Relations, (character, owner, standing) => StandingNotices.Add((character, owner, standing))),
            skillBulk: new SkillBulkCommands(catalog, Skills, source, Characters, null, () => ClockMs, _ => null));
        Inventory = new InvBrokerService(Items, new ItemTypeCatalog(sde), new Eve.Universe.StationCatalog([]), Ships, statics, () => ClockMs);
    }

    /// <summary>Space as a test plays it: it keeps the orders and answers what the test tells it to.</summary>
    public FakeTargeting Targeting { get; } = new();

    /// <summary>The modules of space as a test plays them.</summary>
    public FakeModules SpaceModules { get; } = new();

    /// <summary>The health of a ship in space as a test plays it.</summary>
    public FakeHealth Health { get; } = new();

    /// <summary>The rounds of the guns that run, as a test plays the simulation's answers.</summary>
    public FakeRounds Rounds { get; } = new();

    /// <summary>The time a reload in space takes, which passes when the test says so.</summary>
    public ManualDelay Delay { get; } = new();

    /// <summary>The game master's commands.</summary>
    public SlashService Slash { get; }

    /// <summary>The wallets, the same schema the character's ISK lives in.</summary>
    public PgWalletStore Wallets { get; }

    public WalletTransactions Transactions { get; }

    /// <summary>The owners and pilots the standing commands name, as a test sets them.</summary>
    public FakeStandingDirectory StandingDirectory { get; } = new();

    /// <summary>The <c>OnStandingSet</c> notices the standing commands sent: the pilot, the owner and the new standing.</summary>
    public List<(long Character, long Owner, double Standing)> StandingNotices { get; } = [];

    public Eve.Wallet.LoyaltyPointStore Loyalty { get; }

    public Eve.Scheduler.DurableScheduler Scheduler { get; }

    public Eve.Events.EventBus Bus { get; }

    public Eve.Relations.RelationStore Relations { get; }

    public NpgsqlDataSource Source { get; }

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore Skills { get; }

    /// <summary>The SDE skill catalog the rig was built with (the starting sets and the published skills).</summary>
    public SkillCatalog Catalog { get; }

    public DogmaStatic Statics { get; }

    public PgModuleStateStore Modules { get; }

    public CharacterShips Ships { get; }

    public CharacterCreation Creation { get; }

    public DogmaIMService Service { get; }
    public Eve.Scheduler.LocalTimers Timers { get; }

    public InvBrokerService Inventory { get; }

    public const int Cockroach = 11019, Ibis = 601;
    private const int CaldariRookieShip = 601;

    private static CreationCatalog CockroachCatalog(CreationCatalog source) =>
        new(
            source.Races.Select(r => r.Id == 1 ? r with { ShipTypeId = Cockroach, ShipName = "Cockroach" } : r),
            source.Bloodlines,
            CreationCatalog.Schools.Select(s => source.StarterFor(s.CorporationId)!));

    public static async Task<Rig> CreateAsync(SkillBoosterActivationHandler? activateBooster = null)
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url!));
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
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, $"dk_{suffix}");
        await Eve.Events.EventBus.MigrateAsync(source, $"de_{suffix}");
        await Eve.Relations.RelationStore.MigrateAsync(source, $"dr_{suffix}");
        var catalog = await SkillCatalog.LoadAsync(sde);
        var creation = new CharacterCreation(
            source, characters, items, skills, catalog, wallets, new StartingWallet(), CockroachCatalog(await CreationCatalog.LoadAsync(sde, Station)), modules);
        return new Rig(source, suffix, sde, await DogmaStatic.LoadAsync(sde), creation, modules, activateBooster, catalog);
    }

    /// <summary>A new character of a race (Caldari by default), on the Cockroach test hull.</summary>
    public async Task<Created> NewCharacterAsync(string name = "Dogma Tester", int race = 1, int bloodline = 1, int school = 35, int account = 1, int? shipTypeId = null)
    {
        await Statics.EnsureTypesAsync([Cockroach, 21857, 3638, 3651]);
        var made = await Creation.CreateAsync(new CreationRequest(account, name, race, bloodline, 1, 10, school, [1], [2]));
        var character = Assert.IsType<Created>(made);
        if (shipTypeId is { } selectedTypeId && (await Items.GetAsync(character.ShipId))?.TypeId != selectedTypeId)
        {
            var replacement = (await Ships.GrantAsync(character.CharacterId, selectedTypeId, 1, singleton: true))!.Single().After;
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Characters.SetShipAsync(connection, transaction, character.CharacterId, replacement.Id);
            await transaction.CommitAsync();
            character = character with { ShipId = replacement.Id };
        }
        if (race == 1 || shipTypeId is not null)
        {
            var kitTypeId = shipTypeId ?? CaldariRookieShip;
            var kit = StarterKits.For(kitTypeId == Cockroach ? CaldariRookieShip : kitTypeId);
            if (kit.Count != 0)
            {
                await using var connection = await Source.OpenConnectionAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                foreach (var module in kit)
                {
                    var moduleId = await Items.CreateAsync(connection, transaction, new NewItem(module.TypeId, character.CharacterId, character.ShipId, module.Flag, 1, true));
                    await Modules.SetAsync(connection, transaction, moduleId, Eve.Dogma.ItemState.Online);
                }
                await transaction.CommitAsync();
            }
        }
        return character;
    }

    public static ClientSession Session(long? character, long id = 1, long? station = Station)
    {
        var session = new ClientSession(new Sink()) { SessionId = id, ClientId = id, UserId = 34, NodeId = 65450 };
        session.Set("accountRole", new PyLong(GameMasterRoles.Gml)); // the slash commands are the game masters' tools
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

    /// <summary>Runs a slash command and answers with its message.</summary>
    public async Task<string> SlashAsync(ClientSession session, string command) =>
        JsSemantics.StringValue(await ((IService)Slash).CallAsync(new ServiceCall { Method = "SlashCmd", Args = [new PyStr(command)], Session = session, CallId = new PyInt(1) }))!;

    public async Task<PyValue> InventoryAsync(ClientSession session, string method, string? bound = null, PyValue[]? args = null, PyValue? kwargs = null) =>
        await ((IService)Inventory).CallAsync(new ServiceCall { Method = method, Args = args ?? [], Kwargs = kwargs ?? PyNone.Instance, Session = session, CallId = new PyInt(1), BoundObjectId = bound });

    /// <summary>Binds dogma to the solar system the session's ship is in and returns the bound object's id.</summary>
    public async Task<string> BindInSpaceAsync(ClientSession session, long system)
    {
        var answer = (PyTuple)await CallAsync(session, "MachoBindObject", null, Py.Tuple(new PyInt(system), new PyInt(5)), PyNone.Instance);
        return OidOf(answer.Items[0]);
    }

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
        Timers.Dispose();
        foreach (var schema in _schemas)
        {
            await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
            await command.ExecuteNonQueryAsync();
        }
        await Source.DisposeAsync();
    }
}
