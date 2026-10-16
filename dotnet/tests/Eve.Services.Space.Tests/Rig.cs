using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Services.Inventory;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;

namespace Eve.Services.Space.Tests;

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


public sealed class PgTheoryAttribute : TheoryAttribute
{
    public PgTheoryAttribute()
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
    public SentLog Sent { get; } = new();

    public void Send(PyValue packet) => Sent.Add(packet);
}

/// <summary>
/// What a session was sent. The host's background work sends from its own threads while a test reads and clears the log, so every access takes the lock (a plain
/// list read during an add loses or tears what the test reads: a load-dependent failure of the death tests).
/// </summary>
internal sealed class SentLog : IEnumerable<PyValue>
{
    private readonly object _gate = new();
    private readonly List<PyValue> _items = [];

    public void Add(PyValue packet)
    {
        lock (_gate)
        {
            _items.Add(packet);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>A snapshot of what was sent so far.</summary>
    public PyValue[] ToArray()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    public IEnumerator<PyValue> GetEnumerator() => ((IEnumerable<PyValue>)ToArray()).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Real stores in throwaway schemas, the real static data, a character made the way the game makes one, and a simulation played by the test.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long ClockMs = 1_700_000_000_123;
    public const long Station = 60015249;

    private readonly string[] _schemas;
    private readonly List<string> _extraSchemas = [];
    private readonly string _suffix;

private readonly Dictionary<long, ClientSession> _fleetSessions = new();
    public Eve.Services.Space.FleetStore FleetStore { get; }

    /// <summary>The schema of this rig's fleets (a second store on it reads what the rig's store wrote).</summary>
    public string FleetSchema => $"pl_{_suffix}";

    private Rig(NpgsqlDataSource source, string suffix, DogmaStatic statics, CharacterCreation creation, CreationCatalog catalog, StationCatalog stations, SolarSystemCatalog systems, PgModuleStateStore modules, TimeSpan protection, TimeSpan jumpCloak, bool npcLoot, Eve.Npc.BeltRatConfig? beltRats, Eve.Scheduler.LocalTimers? localTimers, Eve.Npc.NpcAuthority? npcAuthority)
    {
        Source = source;
        // the fleets have a schema of their own: the shared one kept the memberships of earlier tests
        FleetStore = new Eve.Services.Space.FleetStore(source, $"pl_{suffix}");
        _suffix = suffix;
        _schemas = [$"pc_{suffix}", $"pi_{suffix}", $"ps_{suffix}", $"pw_{suffix}", $"pf_{suffix}", $"pn_{suffix}", $"pp_{suffix}", $"pd_{suffix}", $"pe_{suffix}", $"pl_{suffix}", $"pt_{suffix}"];
        Events = new Eve.Events.EventBus(source, new Eve.Scheduler.DurableScheduler(source, () => Now, null, _schemas[7]), null, _schemas[8]);
        SpaceItems = new PgSpaceItemStore(source, _schemas[6]);
        Deployed = new Eve.Ships.PgDeployedStore(source, _schemas[6]);
        Probes = new Eve.Ships.PgProbeStore(source, _schemas[6]);
        Characters = new PgCharacterStore(source, _schemas[0]);
        Items = new PgItemStore(source, _schemas[1]);
        Skills = new PgSkillStore(source, _schemas[2]);
        Statics = statics;
        Creation = creation;
        Modules = modules;
        Stations = stations;
        Systems = systems;
        Ships = new CharacterShips(source, statics, Characters, Items, Skills, modules);
        Wallets = new PgWalletStore(source, _schemas[3]);
        WalletTransactions = new WalletTransactions(source, _schemas[3]);
        Bounties = new Eve.Npc.PgBountyStore(source, WalletTransactions, _schemas[5], Log.Add);
        var npcAuthorityOf = npcAuthority ?? Eve.Npc.NpcAuthority.Load();
        Space = new SpaceSessions(Sim, Ships, statics, stations, systems, () => Now, Log.Add, protection, jumpCloak, localTimers) { Registry = Registry, RookieShipOfRace = race => catalog.Race(race)?.ShipTypeId, Npcs = new Eve.Npc.NpcSelector(npcAuthorityOf), NpcWrecks = npcLoot ? new NpcWrecks(_ => LootTable, () => Task.FromResult(new Eve.Npc.NpcLoot(LootPool)), Ships, () => 0) : null, BeltRats = beltRats is null ? null : new Eve.Npc.BeltRats(npcAuthorityOf, beltRats, type => Eve.Npc.NpcHull.HasHitPoints(statics.Type(type))) };
        Space.UseBounties(Bounties);
        Scheduler = new Eve.Scheduler.DurableScheduler(source, () => Now, null, _schemas[7]);
        Containers = new SpaceContainers(Sim, Ships, Items, SpaceItems, new Eve.Ships.PgWorkCycleLedger(source, _schemas[6]), new EventBusFacts(Events), statics, Space, () => Now, Log.Add, deployed: Deployed, scheduler: Scheduler);
        Scheduler.Register(SpaceContainers.AnchorJob, async (context, token) => await Containers.AnchorAsync(context.Payload<SpaceContainers.DisruptorAnchorDue>()!.DeployableId, token));
        Scheduler.Register(SpaceContainers.ObservatoryPingJob, async (context, token) => await Containers.PingObservatoryAsync(context.Payload<SpaceContainers.ObservatoryPingDue>()!.DeployableId, token));
        Scheduler.Register(SpaceContainers.ControlTowerFuelJob, async (context, token) => await Containers.ControlTowerFuelAsync(context.Connection, context.Transaction, context.Payload<SpaceContainers.ControlTowerFuelDue>()!.DeployableId, context.ScheduledForMs, token));
        Scheduler.Register(SpaceContainers.ControlTowerUnanchorJob, async (context, token) => await Containers.CompleteControlTowerUnanchorAsync(context.Payload<SpaceContainers.ControlTowerUnanchorDue>()!.DeployableId, token));
        Space.Probes = new Eve.Services.Space.SpaceProbes(Ships, statics, Probes, Space, Sim, () => Now);
        ProbeScans = new Eve.Services.Space.SpaceProbeScans(Space, Sim, statics, Probes, Ships, Skills, Scheduler, Space.Probes, () => Now, sites: null, random: new Random(7));
        ProbeScans.Register();
        Space.ProbeScans = ProbeScans;
        Space.DirectionalScans = new Eve.Services.Space.Scanning.SpaceDirectionalScans(Space, Sim, statics);
        Drones = new SpaceDrones(Sim, Ships, statics, Space, () => Now, Log.Add);
        Inventory = new InvBrokerService(Items, new ItemTypeCatalog(new SdeReader(source)), Stations, Ships, statics, () => Now, Containers, Drones);
        Ship = new ShipService(Space, () => ClockMs, Containers, Drones);
        Beyonce = new BeyonceService(Space, () => ClockMs, FleetStore, characterId => _fleetSessions.GetValueOrDefault(characterId));
        Beyonce.SkillLevelOfCharacter = (_, _, _) => Task.FromResult(5);
        Entity = new EntityService(Drones, () => ClockMs);
        Registry.Register(Ship);
        Registry.Register(Beyonce);
        Registry.Register(Entity);
    }

    /// <summary>The PostgreSQL schema of the space items and the work-cycle ledger: a host restarted over it reads the same ledger.</summary>
    public string SpaceSchema => _schemas[6];
    /// <summary>The schema of the rig's durable scheduler (a test that restarts the host makes a second scheduler over it).</summary>
    public string SchedulerSchema => _schemas[7];
    /// <summary>The schema of the rig's event bus (a restarted bus over the same cursors is made over it).</summary>
    public string EventsSchema => _schemas[8];

    /// <summary>The Eve.Structures schema of this rig (ST-1 d tests).</summary>
    public string StructureSchema => _schemas[^1];



    /// <summary>What every NPC leaves when the rig has loot (<c>npcLoot</c>): a stack of seven to nine of the first type of <see cref="LootPool"/>, or nothing when null (the dice always come out 0).</summary>
    public Eve.Npc.LootTable? LootTable { get; set; } = new("test_loot", "", 1, 1, 0, false, false, 7, 9, [], false);

    public static readonly Eve.Npc.LootType[] LootPool = [new(222, "Antimatter Charge S", "Hybrid Charge", 8), new(3296, "Civilian Gatling Pulse Laser", "Energy Weapon", 7)];

    /// <summary>The host's clock: <see cref="ClockMs"/> until a test moves it.</summary>
    public long Now { get; set; } = ClockMs;

    public FakeSim Sim { get; } = new();

    public StationCatalog Stations { get; private set; } = null!;

    /// <summary>The solar systems of the static data (their security and their constellation and region, for a pilot placed in one).</summary>
    public SolarSystemCatalog Systems { get; private set; } = null!;

    public ServiceRegistry Registry { get; } = new();

    public List<string> Log { get; } = [];

    public NpgsqlDataSource Source { get; }

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore Skills { get; }

    /// <summary>The probe scan of the probes in space (its jobs run when the test asks, with the scheduler).</summary>
    public Eve.Services.Space.SpaceProbeScans ProbeScans { get; }

    public DogmaStatic Statics { get; }

    public PgModuleStateStore Modules { get; }

    public CharacterShips Ships { get; }

    public CharacterCreation Creation { get; }

    public SpaceSessions Space { get; }

    public PgWalletStore Wallets { get; }

    public WalletTransactions WalletTransactions { get; }

    public Eve.Npc.PgBountyStore Bounties { get; }

    public ShipService Ship { get; }

    public SpaceContainers Containers { get; }

    public PgSpaceItemStore SpaceItems { get; }

    /// <summary>The records of the deployables the host keeps (the cynosural fields), in the same schema as the space items.</summary>
    public Eve.Ships.PgDeployedStore Deployed { get; }

    /// <summary>The durable scheduler the containers schedule an anchoring on (its jobs run when the test asks, <c>RunDueAsync</c>).</summary>
    public Eve.Scheduler.DurableScheduler Scheduler { get; }

    /// <summary>The scanner probes the host keeps a record of, in the same schema.</summary>
    public Eve.Ships.PgProbeStore Probes { get; }

    public BeyonceService Beyonce { get; }

    public SpaceDrones Drones { get; }

    public EntityService Entity { get; }

    /// <param name="protection">How long a ship is protected after it undocks (never, unless a test wants the end of it).</param>
    /// <param name="npcLoot">Whether an NPC that is destroyed leaves a wreck the host makes with <see cref="LootTable"/> in it.</param>
    /// <param name="beltRats">How the rats of the belts are rolled (none: the belts have none).</param>
    public InvBrokerService Inventory { get; }

    public const int Cockroach = 11019;
    private const int CaldariRookieShip = 601;

    private static CreationCatalog CockroachCatalog(CreationCatalog source) =>
        new(
            source.Races.Select(r => r.Id == 1 ? r with { ShipTypeId = Cockroach, ShipName = "Cockroach" } : r),
            source.Bloodlines,
            CreationCatalog.Schools.Select(s => source.StarterFor(s.CorporationId)!));

    public static async Task<Rig> CreateAsync(TimeSpan? protection = null, TimeSpan? jumpCloak = null, bool npcLoot = false, Eve.Npc.BeltRatConfig? beltRats = null, Eve.Scheduler.LocalTimers? localTimers = null, Eve.Npc.NpcAuthority? npcAuthority = null,
        IReadOnlyDictionary<int, Eve.Universe.ClientSystemFlags>? clientFlags = null)
    {
        CharacterNames.EnsureUnicodeSupport();
        var source = NpgsqlDataSource.Create(PgConnection.ForTest(Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var characters = new PgCharacterStore(source, $"pc_{suffix}");
        var items = new PgItemStore(source, $"pi_{suffix}");
        var skills = new PgSkillStore(source, $"ps_{suffix}");
        var wallets = new PgWalletStore(source, $"pw_{suffix}");
        var modules = new PgModuleStateStore(source, $"pf_{suffix}");
        await PgCharacterStore.MigrateAsync(source, $"pc_{suffix}");
        await PgItemStore.MigrateAsync(source, $"pi_{suffix}");
        await PgSkillStore.MigrateAsync(source, $"ps_{suffix}");
        await PgWalletStore.MigrateAsync(source, $"pw_{suffix}");
        await PgModuleStateStore.MigrateAsync(source, $"pf_{suffix}");
        await Eve.Npc.PgBountyStore.MigrateAsync(source, $"pn_{suffix}");
        await PgSpaceItemStore.MigrateAsync(source, $"pp_{suffix}");
        await Eve.Scheduler.DurableScheduler.MigrateAsync(source, $"pd_{suffix}");
        await Eve.Events.EventBus.MigrateAsync(source, $"pe_{suffix}");
        await Eve.Data.Migrations.ApplyAsync(source, $"pl_{suffix}",
        [
            new Eve.Data.Migration("001_fleet_core", """
                CREATE TABLE fleets (
                    fleet_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                    leader_character_id bigint NOT NULL,
                    auto_join_squad_id bigint
                );
                CREATE TABLE fleet_members (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    character_id bigint NOT NULL UNIQUE,
                    wing_id bigint NOT NULL DEFAULT -1,
                    squad_id bigint NOT NULL DEFAULT -1,
                    role smallint NOT NULL CHECK (role BETWEEN 1 AND 4),
                    job smallint NOT NULL DEFAULT 0,
                    joined_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (fleet_id, character_id)
                );
                CREATE TABLE fleet_wings (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    wing_id bigint GENERATED BY DEFAULT AS IDENTITY,
                    name text NOT NULL DEFAULT '',
                    position integer NOT NULL,
                    PRIMARY KEY (fleet_id, wing_id)
                );
                CREATE TABLE fleet_squads (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    squad_id bigint GENERATED BY DEFAULT AS IDENTITY,
                    wing_id bigint NOT NULL,
                    name text NOT NULL DEFAULT '',
                    position integer NOT NULL,
                    PRIMARY KEY (fleet_id, squad_id),
                    FOREIGN KEY (fleet_id, wing_id)
                        REFERENCES fleet_wings(fleet_id, wing_id) ON DELETE CASCADE
                );
                CREATE TABLE fleet_invitations (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    character_id bigint NOT NULL,
                    inviter_id bigint NOT NULL,
                    wing_id bigint NOT NULL DEFAULT -1,
                    squad_id bigint NOT NULL DEFAULT -1,
                    role smallint NOT NULL CHECK (role BETWEEN 1 AND 4),
                    created_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (fleet_id, character_id)
                );
                CREATE TABLE fleet_join_requests (
                    fleet_id bigint NOT NULL REFERENCES fleets(fleet_id) ON DELETE CASCADE,
                    character_id bigint NOT NULL,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (fleet_id, character_id)
                );
                """),
            new Eve.Data.Migration("002_fleet_finder", """
                ALTER TABLE fleets
                    ADD COLUMN advert_active boolean NOT NULL DEFAULT false,
                    ADD COLUMN fleet_name text NOT NULL DEFAULT '',
                    ADD COLUMN description text NOT NULL DEFAULT '',
                    ADD COLUMN invite_scope integer NOT NULL DEFAULT 0,
                    ADD COLUMN join_needs_approval boolean NOT NULL DEFAULT false,
                    ADD COLUMN hide_info boolean NOT NULL DEFAULT false,
                    ADD COLUMN advert_join_limit integer NOT NULL DEFAULT 256,
                    ADD COLUMN public_min_standing double precision NOT NULL DEFAULT -10,
                    ADD COLUMN max_member_count smallint NOT NULL DEFAULT 256
                        CHECK (max_member_count BETWEEN 1 AND 256);
                CREATE INDEX fleets_active_adverts
                    ON fleets (fleet_id) WHERE advert_active;
                """),
            new Eve.Data.Migration("003_fleet_optional_fc", """
                ALTER TABLE fleets
                    ALTER COLUMN leader_character_id DROP NOT NULL;
                """),
            new Eve.Data.Migration("004_fleet_member_runtime_fields", """
                ALTER TABLE fleets
                    ADD COLUMN IF NOT EXISTS is_free_move boolean NOT NULL DEFAULT false,
                    ADD COLUMN IF NOT EXISTS motd text NOT NULL DEFAULT '';
                ALTER TABLE fleet_members
                    ADD COLUMN IF NOT EXISTS accepts_fleet_warp boolean NOT NULL DEFAULT true,
                    ADD COLUMN IF NOT EXISTS accepts_conduit_jumps boolean NOT NULL DEFAULT true,
                    ADD COLUMN IF NOT EXISTS accepts_fleet_regroups boolean NOT NULL DEFAULT true,
                    ADD COLUMN IF NOT EXISTS ship_type_id bigint;
                """),
            Eve.Services.Space.FleetStore.FleetFinderAllowedEntitiesMigration,
            Eve.Services.Space.FleetStore.FleetFinderAdvertPayloadMigration,
            Eve.Services.Space.FleetStore.FleetJoinRequestProfileMigration
        ], CancellationToken.None);
        var catalog = CockroachCatalog(await CreationCatalog.LoadAsync(sde, Station));
        var creation = new CharacterCreation(
            source, characters, items, skills, await SkillCatalog.LoadAsync(sde), wallets, new StartingWallet(), catalog, modules);
        return new Rig(source, suffix, await DogmaStatic.LoadAsync(sde), creation, catalog, await StationCatalog.LoadAsync(sde), await SolarSystemCatalog.LoadAsync(sde, clientFlags), modules, protection ?? Timeout.InfiniteTimeSpan, jumpCloak ?? Timeout.InfiniteTimeSpan, npcLoot, beltRats, localTimers, npcAuthority);
    }

    public string InventorySchema => _schemas[1];

    /// <summary>A new character (Caldari), in its rookie ship in the station.</summary>
    /// <summary>A unit of something in the cargo hold of a ship (each is an item of its own).</summary>
    public async Task<long> GiveCargoAsync(long owner, long ship)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(3296, owner, ship, 5, 1, true));
        await transaction.CommitAsync();
        return id;
    }

    public async Task<Created> NewCharacterAsync(string name = "Space Tester", int account = 1, int shipTypeId = Cockroach)
    {
        await Statics.EnsureTypesAsync([Cockroach, 21857, 3638, 3651]);
        var made = await Creation.CreateAsync(new CreationRequest(account, name, 1, 1, 1, 10, 35, [1], [2]));
        var character = Assert.IsType<Created>(made);
        if (shipTypeId != Cockroach)
        {
            var replacement = (await Ships.GrantAsync(character.CharacterId, shipTypeId, 1, singleton: true))!.Single().After;
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await Characters.SetShipAsync(connection, transaction, character.CharacterId, replacement.Id);
            await transaction.CommitAsync();
            character = character with { ShipId = replacement.Id };
        }
        var kit = StarterKits.For(shipTypeId == Cockroach ? CaldariRookieShip : shipTypeId);
        if (kit.Count != 0)
        {
            await using var connection = await Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            foreach (var module in kit)
            {
                var moduleId = await Items.CreateAsync(connection, transaction, new NewItem(module.TypeId, character.CharacterId, character.ShipId, module.Flag, 1, true));
                await Modules.SetAsync(connection, transaction, moduleId, ItemState.Online);
            }
            await transaction.CommitAsync();
        }
        return character;
    }

    /// <summary>A session as choosing the character leaves it: docked at the station.</summary>
    public async Task<ClientSession> SessionAsync(Created created, long id = 1)
    {
        var character = (await Characters.GetAsync(created.CharacterId))!;
        var session = new ClientSession(new Sink()) { SessionId = id, ClientId = 431065450 + id, UserId = 34, NodeId = 65450 };
        session.CharacterId = created.CharacterId;
        session.Set("stationid", new PyInt(character.StationId));
        session.Set("stationid2", new PyInt(character.StationId));
        session.Set("solarsystemid2", new PyInt(character.SolarSystemId));
        session.Set("constellationid", new PyInt(character.ConstellationId));
        session.Set("regionid", new PyInt(character.RegionId));
        session.Set("locationid", new PyInt(character.StationId));
        session.Set("shipid", new PyLong(created.ShipId));
        _fleetSessions[created.CharacterId] = session;
        return session;
    }

    private async Task<PyValue> CallAsync(IService service, ClientSession session, string method, string? bound, PyValue[] args) =>
        await service.CallAsync(new ServiceCall { Method = method, Args = args, Session = session, CallId = new PyInt(1), BoundObjectId = bound });

    public Task<PyValue> ShipCallAsync(ClientSession session, string method, string? bound = null, params PyValue[] args) => CallAsync(Ship, session, method, bound, args);

    public ValueTask<PyValue> InventoryCallAsync(ClientSession session, string method, string? bound, PyValue[] args, PyValue? kwargs = null) =>
        ((IService)Inventory).CallAsync(new ServiceCall
        {
            Method = method,
            Args = args,
            Kwargs = kwargs ?? PyNone.Instance,
            Session = session,
            CallId = new PyInt(1),
            BoundObjectId = bound,
        });

    public async Task<string> BindInventoryShipInSpaceAsync(ClientSession session)
    {
        var opened = await InventoryCallAsync(session, "GetInventoryFromId", null, [new PyLong(session.GetInt("shipid")!.Value)]);
        return OidOf(opened);
    }

    public Task<PyValue> BeyonceCallAsync(ClientSession session, string method, string? bound = null, params PyValue[] args) => CallAsync(Beyonce, session, method, bound, args);

    /// <summary>A call of <c>beyonce</c> with keyword arguments.</summary>
    public async Task<PyValue> BeyonceCallWithAsync(ClientSession session, string method, string bound, PyValue[] args, params (string Name, PyValue Value)[] kwargs) =>
        await ((IService)Beyonce).CallAsync(new ServiceCall
        {
            Method = method,
            Args = args,
            Kwargs = new PyDict([.. kwargs.Select(k => new KeyValuePair<PyValue, PyValue>(new PyStr(k.Name), k.Value))]),
            Session = session,
            CallId = new PyInt(1),
            BoundObjectId = bound,
        });

    /// <summary>Binds a service the way the dispatcher does for the client: the answer's bound object is registered and the session is given it.</summary>
    private async Task<string> BindAsync(IService service, ClientSession session, long place, long group)
    {
        var answer = (PyTuple)await CallAsync(service, session, "MachoBindObject", null, [Py.Tuple(new PyInt(place), new PyInt(group)), PyNone.Instance]);
        var oid = OidOf(answer.Items[0]);
        Registry.RegisterBoundObject(oid, service);
        session.RememberBoundObject(oid);
        return oid;
    }

    public Task<string> BindShipAsync(ClientSession session) => BindAsync(Ship, session, session.GetInt("stationid")!.Value, 15);

    /// <summary>The ship service bound to the solar system the session flies in (what the client does before it jettisons).</summary>
    public Task<string> BindShipInSpaceAsync(ClientSession session) => BindAsync(Ship, session, session.GetInt("solarsystemid")!.Value, 5);

    public Task<PyValue> EntityCallAsync(ClientSession session, string method, string? bound = null, params PyValue[] args) => CallAsync(Entity, session, method, bound, args);

    /// <summary>The entity service, bound as the client does in space.</summary>
    public Task<string> BindEntityAsync(ClientSession session) => BindAsync(Entity, session, session.GetInt("solarsystemid")!.Value, 5);

    public Task<string> BindBeyonceAsync(ClientSession session) => BindAsync(Beyonce, session, session.GetInt("solarsystemid")!.Value, 5);

    public static string OidOf(PyValue bound) =>
        JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)bound).Value).Decoded!).Items[0])!;

    /// <summary>Waits for what a background task of the host does (a docking is finished off the thread the simulation spoke on).</summary>
    public static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "gave up waiting");
            await Task.Delay(10);
        }
    }

    /// <summary>The events bus of the foundations, when a test asked for it (<see cref="UseBusAsync"/>); what space announced on it is in <see cref="Announced"/>.</summary>
    public Eve.Events.EventBus? Bus { get; private set; }

    /// <summary>The bus the space containers publish their mining facts on (its outbox is in this rig's schema).</summary>
    public Eve.Events.EventBus Events { get; }

    public List<(string Topic, string Json)> Announced { get; } = [];

    /// <summary>Gives <see cref="Space"/> an events bus on scratch schemas, with a subscriber that records everything announced on it.</summary>
    public async Task UseBusAsync()
    {
        string[] schemas = [$"sc_{_suffix}", $"ev_{_suffix}"];
        _extraSchemas.AddRange(schemas);
        await Eve.Scheduler.DurableScheduler.MigrateAsync(Source, schemas[0]);
        await Eve.Events.EventBus.MigrateAsync(Source, schemas[1]);
        var scheduler = new Eve.Scheduler.DurableScheduler(Source, () => Now, null, schemas[0]);
        Bus = new Eve.Events.EventBus(Source, scheduler, null, schemas[1]);
        Bus.Subscribe("test.record", ["*"], (context, _) =>
        {
            lock (Announced)
            {
                Announced.Add((context.Event.Topic, context.Event.PayloadJson));
            }
            return Task.CompletedTask;
        });
        await Bus.PumpAsync();   // the subscriber starts here
        Space.Events = Bus;
    }

    /// <summary>Makes a test's own scratch schemas on this rig's source: they are dropped with the rig.</summary>
    public void KeepSchemas(params string[] schemas) => _extraSchemas.AddRange(schemas);

    public async ValueTask DisposeAsync()
    {
        Containers.Dispose();
        Space.Dispose();
        foreach (var schema in _schemas.Concat(_extraSchemas))
        {
            try
            {
                await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE");
                await command.ExecuteNonQueryAsync();
            }
            catch (NpgsqlException)
            {
                // Leaving a scratch schema behind is better than failing the test.
            }
        }
        await Source.DisposeAsync();
    }
}
