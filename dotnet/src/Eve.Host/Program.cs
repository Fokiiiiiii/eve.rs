using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Ships;
using Eve.Marshal;
using Eve.Npc;
using Eve.Sde;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
using Npgsql;
using Eve.Services;
using Eve.Services.CharacterSelect;
using Eve.Services.Core;
using Eve.Services.Dogma;
using Eve.Services.Inventory;
using Eve.Services.CharacterInfo;
using Eve.Services.Skills;
using Eve.Services.Slash;
using Eve.Services.Space;
using Eve.Space;
using Eve.Services.Station;
using Eve.Services.Wallet;

// eve-host: the service host.
//
//   EVE_HOST               where to listen: a socket path, "unix:/path" or "tcp:host:port"   (default /tmp/eve-host.sock)
//   EVE_IMAGE_SERVER_URL   the image server the client is told about                         (default http://127.0.0.1:26001/)
//   EVE_PG_URL             PostgreSQL for accounts (postgres://... or an Npgsql string); without it accounts
//                          live in memory and are lost when the host stops
//   EVE_IDENTITY_SCHEMA    the schema of the accounts module                                  (default identity)
//   EVE_CHARACTERS_SCHEMA  the schema of the characters module                                (default characters)
//   EVE_INVENTORY_SCHEMA   the schema of the inventory module                                 (default inventory)
//   EVE_SKILLS_SCHEMA      the schema of the skills module                                    (default skills)
//   EVE_WALLET_SCHEMA      the schema of the wallet module                                    (default wallet)
//   EVE_FITTING_SCHEMA     the schema of the fitting module (which modules are online)        (default fitting)
//   EVE_SPACE_SCHEMA       the schema of what lies in space (cans and wrecks: where, until when) (default space)
//   EVE_SIM_SOCKET         the Unix socket of the space simulation (eve-sim)                  (default /tmp/eve-sim.sock)
//   EVE_SIM                off runs without the simulation: nobody can undock                 (default on)
//   EVE_START_ISK / EVE_START_PLEX   what a new character starts with                         (100000 / 2222, the reference's)
//   EVE_START_STATION      the station every new character starts at (the reference's; 60015249), or
//                          "school" for the headquarters of the character's school corporation
//                          Characters need EVE_PG_URL and the SDE (`eve-sde ensure`): without them the host runs
//                          without charUnboundMgr, and a client cannot get past the character screen.
//   EVE_MARKET_SEED        skip: do not fetch the NPC market's first orders from CCP's ESI (CI, development)  (default auto: fetch once per SDE build)
//   EVE_SCHEDULER_SCHEMA, EVE_EVENTS_SCHEMA, EVE_RELATIONS_SCHEMA, EVE_CONTENT_SCHEMA   the schemas of the shared foundations (scheduler, event bus, relations, content)
//                          (defaults scheduler, events, relations, content)
//   EVE_CRIMEWATCH_SCHEMA  the schema of crimewatch (ship safety levels)                          (default crimewatch)
//   EVE_DEV_SKIP_PASSWORD  1 accepts any password (development only)                          (default 0)
//   EVE_DEV_AUTO_CREATE    0 refuses unknown user names                                       (default 1)

static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;

static bool Flag(string name, bool fallback) => Env(name, fallback ? "1" : "0") is "1" or "true" or "TRUE" or "yes";

static Socket Listen(string endpoint)
{
    if (endpoint.StartsWith("tcp:", StringComparison.Ordinal))
    {
        var text = endpoint["tcp:".Length..];
        var colon = text.LastIndexOf(':');
        var host = colon > 0 ? text[..colon] : "127.0.0.1";
        var port = int.Parse(text[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        tcp.Bind(new IPEndPoint(IPAddress.Parse(host), port));
        tcp.Listen(128);
        return tcp;
    }
    var path = endpoint.StartsWith("unix:", StringComparison.Ordinal) ? endpoint["unix:".Length..] : endpoint;
    if (File.Exists(path))
    {
        File.Delete(path);
    }
    var unix = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    unix.Bind(new UnixDomainSocketEndPoint(path));
    unix.Listen(128);
    return unix;
}

var endpoint = Env("EVE_HOST", "/tmp/eve-host.sock");
var options = new ClusterOptions { ImageServerUrl = Env("EVE_IMAGE_SERVER_URL", "http://127.0.0.1:26001/") };
var accountOptions = new AccountOptions
{
    SkipPasswordCheck = Flag("EVE_DEV_SKIP_PASSWORD", false),
    AutoCreate = Flag("EVE_DEV_AUTO_CREATE", true),
};
IAccountDirectory accounts;
IReadOnlyList<IService> characterServices = [];
Foundations? foundations = null;
SpaceSessions? space = null;
Eve.Services.Crimewatch.CrimewatchRuntime? crimewatch = null;
SimClient? simulation = null;
var online = new OnlineCharacters();
if (Env("EVE_PG_URL", "") is { Length: > 0 } pgUrl)
{
    var schema = Env("EVE_IDENTITY_SCHEMA", PgAccountDirectory.DefaultSchema);
    var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl));
    var applied = await PgAccountDirectory.MigrateAsync(source, schema);
    Console.Error.WriteLine($"eve-host: accounts in PostgreSQL, schema {schema}{(applied.Count > 0 ? "; applied " + string.Join(", ", applied) : "")}");
    var directory = new PgAccountDirectory(source, accountOptions, schema);
    accounts = directory;
    Func<long> clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    foundations = await Foundations.CreateAsync(source, clock, Env, message => Console.Error.WriteLine($"eve-host: {message}"));
    (characterServices, space, simulation, crimewatch) = await CharacterServicesAsync(source, directory, online, clock, foundations);
    // every feature has registered its job handlers by now: the scheduler may start running jobs
    await foundations.StartAsync();
}
else
{
    Console.Error.WriteLine("eve-host: no EVE_PG_URL: accounts are kept in memory and lost when the host stops; no characters");
    accounts = new InMemoryAccountDirectory(accountOptions);
}

var registry = new ServiceRegistry();
Func<long> now = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
foreach (var service in CoreServices.Create(new SubscriptionSettings(), new NoOmegaAccounts(), now))
{
    registry.Register(service);
}
foreach (var service in characterServices)
{
    registry.Register(service);
}
if (characterServices.Count > 0)
{
    // names the client also uses for the same service
    registry.RegisterAlias("station", "stationSvc");
    registry.RegisterAlias("skillMgr", "skillHandler");
}
if (space is not null)
{
    space.Registry = registry;
}
var config = GlobalConfig.Build(options);
var info = new HostMachoNetInfo(registry, config);
registry.Register(new MachoNetService(info, now));

var dispatcher = new PacketDispatcher(registry, now);
dispatcher.OnProblem = (message, error) => Console.Error.WriteLine($"eve-host: {message}{(error is null ? "" : ": " + error.Message)}");
var clusterConfig = MarshalCodec.Encode(config);
var server = new HostServer(
    new HostOptions
    {
        Log = message => Console.Error.WriteLine($"eve-host: {message}"),
        SessionOpened = session => session.Closed += () => online.Leave(session.SessionId),
    },
    dispatcher,
    accounts,
    () => clusterConfig);

using var listener = Listen(endpoint);
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancel.Cancel();
};
Console.Error.WriteLine($"eve-host: listening on {endpoint}; {registry.Count} services");
await server.ServeAsync(listener, cancel.Token);
if (foundations is not null)
{
    await foundations.DisposeAsync();
}
space?.Dispose();
crimewatch?.Dispose();
if (simulation is not null)
{
    await simulation.DisposeAsync();
}
return 0;

/// <summary>
/// The character-side modules (characters, inventory, skills) and their services, or none (with the reason logged) when
/// the static data they need is not in the database.
/// </summary>
static async Task<(IReadOnlyList<IService> Services, SpaceSessions? Space, SimClient? Simulation, Eve.Services.Crimewatch.CrimewatchRuntime? Crimewatch)> CharacterServicesAsync(NpgsqlDataSource source, IAccountLookup accounts, OnlineCharacters online, Func<long> now, Foundations foundations)
{
    CharacterNames.EnsureUnicodeSupport();
    var characterSchema = Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema);
    var inventorySchema = Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema);
    var skillsSchema = Env("EVE_SKILLS_SCHEMA", PgSkillStore.DefaultSchema);
    var walletSchema = Env("EVE_WALLET_SCHEMA", PgWalletStore.DefaultSchema);
    var fittingSchema = Env("EVE_FITTING_SCHEMA", PgModuleStateStore.DefaultSchema);
    await PgCharacterStore.MigrateAsync(source, characterSchema);
    await PgItemStore.MigrateAsync(source, inventorySchema);
    await PgSkillStore.MigrateAsync(source, skillsSchema);
    await PgWalletStore.MigrateAsync(source, walletSchema);
    var npcSchema = Env("EVE_NPC_SCHEMA", PgBountyStore.DefaultSchema);
    await PgBountyStore.MigrateAsync(source, npcSchema);
    await PgModuleStateStore.MigrateAsync(source, fittingSchema);
    var spaceSchema = Env("EVE_SPACE_SCHEMA", PgSpaceItemStore.DefaultSchema);
    await PgSpaceItemStore.MigrateAsync(source, spaceSchema);
    var crimewatchSchema = Env("EVE_CRIMEWATCH_SCHEMA", Eve.Crimewatch.PgCrimewatchStore.DefaultSchema);
    await Eve.Crimewatch.PgCrimewatchStore.MigrateAsync(source, crimewatchSchema);
    var crimewatchStore = new Eve.Crimewatch.PgCrimewatchStore(source, crimewatchSchema);

    var sde = new SdeReader(source);
    if (await sde.BuildAsync() is not { } build)
    {
        Console.Error.WriteLine("eve-host: the static data is not loaded (run `eve-sde ensure --pg $EVE_PG_URL`): no charUnboundMgr, no skills");
        return ([], null, null, null);
    }
    var startText = Env("EVE_START_STATION", "60015249");
    long? startStation = startText == "school" ? null : long.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
    var catalog = await CreationCatalog.LoadAsync(sde, startStation);
    var characters = new PgCharacterStore(source, characterSchema);
    var items = new PgItemStore(source, inventorySchema);
    var skills = new PgSkillStore(source, skillsSchema);
    var skillCatalog = await SkillCatalog.LoadAsync(sde);
    var wallets = new PgWalletStore(source, walletSchema);
    var modules = new PgModuleStateStore(source, fittingSchema);
    var stations = await StationCatalog.LoadAsync(sde);
    var solarSystems = await SolarSystemCatalog.LoadAsync(sde);
    var dogma = await DogmaStatic.LoadAsync(sde);
    var omega = new OmegaLicenses(new SubscriptionSettings(), new NoOmegaAccounts(), now);
    var training = new CharacterTraining(
        source, skillCatalog, skills, new PgTrainingStore(source, skillsSchema),
        new TrainingSettings
        {
            Speed = double.Parse(Env("EVE_SKILL_TRAINING_SPEED", "1"), System.Globalization.CultureInfo.InvariantCulture),
            HasOmega = account => omega.Resolve(new PyInt(account)).HasLicense,
        },
        now);
    var ships = new CharacterShips(source, dogma, characters, items, skills, modules, training);
    // the simulation starts empty with this host: a ship that a host which stopped without closing its sessions left in space is put back where its character last was
    var stranded = await ships.RecoverAllFromSpaceAsync(id => stations.Get(id) is { } station ? (station.SolarSystemId, station.ConstellationId, station.RegionId) : null);
    if (stranded > 0)
    {
        Console.Error.WriteLine($"eve-host: {stranded} ship(s) left in space by the last host were put back in their stations");
    }
    var dogmaSync = new CharacterDogmaSync(dogma, ships, now);
    var characterSessions = new CharacterSessions();
    // the combat timers and flags: kept in crimewatch's schema, told to the clients as they change (Eve.Services.Crimewatch)
    Func<long, CancellationToken, Task<double>> securityStatusOf = (id, token) => foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.SecurityStatus.Name, id, Eve.Relations.RelationStore.World, 0, null, null, token);
    var crimewatchRuntime = new Eve.Services.Crimewatch.CrimewatchRuntime(crimewatchStore, characterSessions, now, message => Console.Error.WriteLine($"eve-host: crimewatch: {message}"));
    crimewatchRuntime.Legal = new Eve.Services.Crimewatch.RelationWars(foundations.Relations).AreAtWarAsync;
    await crimewatchRuntime.ResumeAsync();
    var announcer = new TrainingAnnouncer(training, skillCatalog, characterSessions, dogmaSync, now);
    announcer.Attach();
    var startingWallet = new StartingWallet(
        decimal.Parse(Env("EVE_START_ISK", "100000"), System.Globalization.CultureInfo.InvariantCulture),
        long.Parse(Env("EVE_START_PLEX", "2222"), System.Globalization.CultureInfo.InvariantCulture));
    Console.Error.WriteLine($"eve-host: characters in PostgreSQL, schemas {characterSchema}, {inventorySchema}, {skillsSchema} and {walletSchema}; {stations.Count} stations; dogma of {dogma.AttributeCount} attributes and {dogma.EffectCount} effects; static data build {build}; new characters start at {(startStation is { } id ? $"station {id}" : "their school's headquarters")}");
    var creation = new CharacterCreation(source, characters, items, skills, skillCatalog, wallets, startingWallet, catalog, modules);
    // Codex-owned corporation registration; keep combat/NPC construction in its own block below.
    var corporationSchema = Env("EVE_CORPORATIONS_SCHEMA", Eve.Corporations.CorporationStore.DefaultSchema);
    await Eve.Corporations.CorporationStore.MigrateAsync(source, corporationSchema);
    var economyWallets = new WalletTransactions(source, walletSchema);
    var economyAnnouncer = new WalletAnnouncer(economyWallets, characterSessions);
    var corporations = new Eve.Corporations.CorporationStore(source, characters, wallets, economyWallets, skills, now, corporationSchema);
    await corporations.SeedNpcAsync(sde);
    var corporationLogin = new Eve.Services.Corporations.CorporationLoginHook(corporations, characters);
    var marketSchema = Env("EVE_MARKET_SCHEMA", Eve.Market.MarketStore.DefaultSchema);
    await Eve.Market.MarketStore.MigrateAsync(source, marketSchema);
    var market = new Eve.Market.MarketStore(source, marketSchema);
    var marketCatalog = await Eve.Market.MarketCatalog.LoadAsync(sde);
    if (Env("EVE_MARKET_SEED", "auto") is "skip" or "off")
    {
        // CI and development: no fetch from CCP's ESI (minutes, and a load on it); the market starts with no NPC orders
        Console.Error.WriteLine("eve-host: native market v4 seed skipped (EVE_MARKET_SEED=skip)");
    }
    else if (!await market.HasSeedV4Async(build))
    {
        var typeIds = (await sde.ReadTypeSummariesAsync()).Where(type => type.TypeId > 0).Select(type => (long)type.TypeId).ToHashSet();
        var npcStationIds = stations.All.Select(station => station.Id).ToHashSet();
        var regionIds = stations.All.Select(station => (long)station.RegionId).Distinct().ToArray();
        var snapshotPath = Path.Combine(Path.GetTempPath(), $"eve.rs-market-seed-v4-{Guid.NewGuid():N}.jsonl");
        try
        {
            await using var snapshotFile = new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var esi = new HttpClient();
            var captured = await Eve.Market.MarketSeedV4.CaptureAsync(
                esi, snapshotFile, regionIds, typeIds, npcStationIds, build, DateTimeOffset.FromUnixTimeMilliseconds(now()));
            await snapshotFile.FlushAsync();
            snapshotFile.Position = 0;
            var fingerprint = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(snapshotFile)).ToLowerInvariant();
            snapshotFile.Position = 0;
            var snapshot = Eve.Market.MarketSeedV4.Read(snapshotFile);
            var imported = await market.SeedNpcV4Async(
                Eve.Market.MarketSeedV4.ToSeedsAsync(snapshot.Orders, stations),
                fingerprint, checked((int)build), captured.CapturedAtUtc.ToUnixTimeMilliseconds(), now);
            if (imported == 0)
            {
                Console.Error.WriteLine("eve-host: matching market v4 seed was imported by another host instance");
            }
            else
            {
                var verified = await snapshot.Completion;
                if (verified.RetainedOrderCount != captured.RetainedOrderCount || imported != captured.RetainedOrderCount)
                    throw new InvalidDataException("Market Seed v4 snapshot/import counts do not match.");
                Console.Error.WriteLine($"eve-host: native market v4 seeded {imported} TQ NPC-station orders");
            }
        }
        finally
        {
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        }
    }
    else
    {
        Console.Error.WriteLine($"eve-host: native market v4 seed already loaded for SDE build {build}");
    }
    var marketTopology = await Eve.Market.MarketTopology.LoadAsync(sde);
    var marketTrading = new Eve.Market.MarketTrading(source, market, characters, corporations, skills, items, economyWallets, stations, marketTopology, now);
    SimClient? sim = null;
    SpaceSessions? spaceSessions = null;
    SpaceContainers? spaceContainers = null;
    SpaceDrones? spaceDrones = null;
    if (Env("EVE_SIM", "on") is not ("off" or "0" or "false"))
    {
        var socket = Env("EVE_SIM_SOCKET", "/tmp/eve-sim.sock");
        sim = SimClient.ToUnixSocket(socket, message => Console.Error.WriteLine($"eve-host: {message}"));
        sim.Start();
        var npcAuthority = NpcAuthority.Load();
        var npcWrecks = new NpcWrecks(npcAuthority, () => NpcLoot.LoadAsync(sde), ships);
        spaceSessions = new SpaceSessions(sim, ships, dogma, stations, solarSystems, now, message => Console.Error.WriteLine($"eve-host: space: {message}")) { RookieShipOfRace = race => catalog.Race(race)?.ShipTypeId, Npcs = new NpcSelector(npcAuthority), NpcWrecks = npcWrecks, BeltRats = new BeltRats(npcAuthority, BeltRatConfig.FromEnvironment(Environment.GetEnvironmentVariable), type => NpcHull.HasHitPoints(dogma.Type(type))) };
        npcWrecks.Warm();
        // the periodic work of the space features is jobs of the shared scheduler, not timers of their own
        spaceSessions.UseBounties(new PgBountyStore(source, new WalletTransactions(source, walletSchema), npcSchema, message => Console.Error.WriteLine($"eve-host: bounty: {message}")));
        spaceSessions.Events = foundations.Events;   // what happens in space is announced on the bus as well
        spaceSessions.SafetyLevelOf = (character, token) => crimewatchStore.SafetyOfAsync(character, token);   // the ship safety the pilot has set
        spaceSessions.Aggression = async (request, token) => await crimewatchRuntime.RecordAggressionAsync(request, token);   // what an attack makes of the attacker
        spaceSessions.SecurityStatusOf = securityStatusOf;   // the ship in space shows it
        // a crime costs security status: written to the relation, told to the pilot, shown on the ship in space
        new Eve.Services.Crimewatch.SecurityStatusPenalties(crimewatchRuntime, characterSessions, securityStatusOf,
            async (character, value, reason, token) => await foundations.Relations.SetAsync(Eve.Relations.RelationKinds.SecurityStatus.Name, character, Eve.Relations.RelationStore.World, value, reason, token),
            now, async (character, token) => await spaceSessions.AffiliationChangedAsync(character, token)).Attach();
        var bounties = spaceSessions;
        foundations.Scheduler.Register("space.bounty.pay", async (_, token) => await bounties.PayBountiesAsync(token));
        await foundations.Scheduler.EnsureRecurringAsync("space.bounty.pay", "pay", TimeSpan.FromSeconds(10));
        spaceContainers = new SpaceContainers(sim, ships, items, new PgSpaceItemStore(source, spaceSchema), dogma, spaceSessions, now, message => Console.Error.WriteLine($"eve-host: cans: {message}"));
        var containers = spaceContainers;
        spaceDrones = new SpaceDrones(sim, ships, dogma, spaceSessions, now, message => Console.Error.WriteLine($"eve-host: {message}"));
        foundations.Scheduler.Register("space.containers.sweep", async (_, _) => await containers.SweepAsync());
        await foundations.Scheduler.EnsureRecurringAsync("space.containers.sweep", "sweep", TimeSpan.FromSeconds(30));
        Console.Error.WriteLine($"eve-host: the space simulation is expected at {socket}");
    }
    Func<long, CancellationToken, Task> allianceAffiliationNotify = spaceSessions is null
        ? static (_, _) => Task.CompletedTask
        : spaceSessions.AffiliationChangedAsync;
    var allianceDue = new Eve.Services.Corporations.AllianceApplicationDueWorker(
        corporations, characters, foundations.Scheduler, foundations.Relations, foundations.Events,
        allianceAffiliationNotify);
    await allianceDue.RegisterAsync();
    var allianceRegistry = new Eve.Services.Corporations.AllianceRegistryService(
        corporations, characters, now,
        scheduleAllianceApplication: allianceDue.ScheduleAcceptedAsync,
        wakeAllianceApplicationScheduler: allianceDue.Wake);

    IReadOnlyList<IService> services =
    [
        new CharUnboundMgrService(accounts, characters, items, skills, wallets, creation, catalog, online, [new SkillsLoginHook(training, skillCatalog, characterSessions, announcer, now), corporationLogin], Random.Shared),
        // security status is the character's relation of that kind (Eve.Relations); crimewatch only asks for it
        new Eve.Services.Crimewatch.CrimewatchService(
            crimewatchStore, now,
            securityStatusOf),
        new SkillMgr2Service(),
        new SkillHandlerService(skills, skillCatalog, training, characters, announcer, now),
        new CharMgrService(characters, stations),
        new StationService(stations, characters, online, now),
        new InvBrokerService(items, new ItemTypeCatalog(sde), stations, ships, dogma, now, spaceContainers),
        new DogmaIMService(dogma, ships, now, dogmaSync, spaceSessions, spaceSessions, spaceSessions, spaceSessions, log: message => Console.Error.WriteLine($"eve-host: ammo: {message}")),
        new AccountService(wallets, corporations, economyWallets),
        new Eve.Services.Market.MarketProxyService(market, marketTrading, marketTopology, characters, economyWallets, marketCatalog, now, characterSessions),
        new Eve.Services.Corporations.CorpMgrService(corporations, characters),
        new Eve.Services.Corporations.CorporationService(corporations, characters, characterSessions),
        new Eve.Services.Corporations.CorpRegistryService(corporations, characters, now, characterSessions)
        {
            AllianceAffiliationWriter = allianceDue.WriteAffiliationAsync
        },
        allianceRegistry,
        new SlashService(new ShipHealthCommands(ships, dogma, now, spaceSessions), spaceSessions is null ? null : new DeathTestCommands(spaceSessions, new ShipTypeNames(sde)), spaceSessions is null ? null : new SuicideCommands(spaceSessions), spaceSessions is null ? null : new NpcCommands(spaceSessions), new ItemCommands(new ItemTypeNames(sde), ships, dogma, now), new SkillCommands(skillCatalog, skills, source, dogmaSync, now)),
        .. spaceSessions is null ? [] : new IService[] { new ShipService(spaceSessions, now, spaceContainers, spaceDrones), new BeyonceService(spaceSessions, now), new EntityService(spaceDrones!, now) },
    ];
    return (services, spaceSessions, sim, crimewatchRuntime);
}

/// <summary>Until accounts live in the database (S2), nobody has a stored Omega expiry.</summary>
sealed class NoOmegaAccounts : IOmegaAccountStore
{
    public string? GetOmegaExpiry(long accountId) => null;
}

/// <summary>machoNet's view of this host: the registered services, with the locations the client routes by.</summary>
sealed class HostMachoNetInfo(ServiceRegistry registry, PyDict config) : IMachoNetInfo
{
    public IReadOnlyList<KeyValuePair<string, string?>> Services() =>
        [.. registry.ServiceNames.Select(name => new KeyValuePair<string, string?>(name, ServiceLocations.Of(name)))];

    public PyDict GlobalConfig() => config;

    public int UserCount() => 0;
}
