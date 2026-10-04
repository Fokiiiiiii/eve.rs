using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Identity;
using Eve.Dogma;
using Eve.Fitting;
using Eve.GameStore;
using System.Text.Json;
using System.Text.Json.Nodes;
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
PersistenceStore? mailStore = null;
OwnerSession? mailStoreOwner = null;
GameStoreCache? mailCache = null;
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
    mailStore = new PersistenceStore(source, Env("EVE_GAMESTORE_SCHEMA", "gamestore"));
    await mailStore.EnsureSchemaAsync();
    mailStoreOwner = await OwnerSession.StartAsync(mailStore, PersistenceRoles.World, $"eve-host-{Environment.ProcessId}");
    mailCache = new GameStoreCache(mailStore, mailStoreOwner, new GameStoreCacheOptions
    {
        ProcessRole = PersistenceRoles.World,
        OnFlushError = (table, error) => Console.Error.WriteLine($"eve-host: gameStore {table} flush failed: {error.Message}"),
    });
    await mailCache.RecoverAsync();
    await mailCache.LoadAsync("mail");
    (characterServices, space, simulation, crimewatch) = await CharacterServicesAsync(source, directory, online, clock, foundations, mailStore, mailCache);
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
if (mailCache is not null) await mailCache.DisposeAsync();
if (mailStoreOwner is not null) await mailStoreOwner.DisposeAsync();
if (mailStore is not null) await mailStore.DisposeAsync();
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
static async Task<(IReadOnlyList<IService> Services, SpaceSessions? Space, SimClient? Simulation, Eve.Services.Crimewatch.CrimewatchRuntime? Crimewatch)> CharacterServicesAsync(NpgsqlDataSource source, IAccountLookup accounts, OnlineCharacters online, Func<long> now, Foundations foundations, PersistenceStore mailStore, GameStoreCache mailCache)
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
        static bool ContactWatchlisted(JsonNode? node)
        {
            if (node is not JsonValue value)
                return false;
            return value.GetValueKind() switch
            {
                JsonValueKind.True => true,
                JsonValueKind.Number => double.TryParse(value.ToJsonString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) && number != 0,
                JsonValueKind.String => value.GetValue<string>() is "1" or "true",
                _ => false,
            };
        }
        async ValueTask<IReadOnlyCollection<long>> ReadWatchlistAsync(long characterId, CancellationToken cancellationToken)
        {
            var state = await ReadGameStoreTableAsync("characters", cancellationToken);
            var contacts = (state[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject)?["personalContacts"];
            var ids = new HashSet<long>();
            if (contacts is JsonObject byId)
            {
                foreach (var (key, value) in byId)
                {
                    if (value is JsonObject row && ContactWatchlisted(row["inWatchlist"]))
                    {
                        var id = long.TryParse(row["contactID"]?.ToString(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var storedId) ? storedId :
                            long.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var keyedId) ? keyedId : 0;
                        if (id > 0) ids.Add(id);
                    }
                }
            }
            else if (contacts is JsonArray rows)
            {
                foreach (var row in rows.OfType<JsonObject>())
                {
                    if (ContactWatchlisted(row["inWatchlist"]) &&
                        long.TryParse(row["contactID"]?.ToString(), System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0)
                        ids.Add(id);
                }
            }
            return ids;
        }
        // The service re-reads saved contacts for both sides of its mutual-watchlist check.
    // the combat timers and flags: kept in crimewatch's schema, told to the clients as they change (Eve.Services.Crimewatch)
    Func<long, CancellationToken, Task<double>> securityStatusOf = (id, token) => foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.SecurityStatus.Name, id, Eve.Relations.RelationStore.World, 0, null, null, token);
    Task<Eve.Relations.Affiliation> AffiliationOfAsync(long character, CancellationToken token) =>
        foundations.Relations.AffiliationOfAsync(character, cancellationToken: token);
    Task<JsonObject> ReadGameStoreTableAsync(string table, CancellationToken token) =>
        PersistenceReadHelpers.ReadOptionalTableAsync(
            (name, cancellation) => mailStore.LoadTableAsync(name, createIfMissing: false, cancellation),
            table,
            token);
    var charMgrReadModel = new PersistenceCharMgrReadModel(ReadGameStoreTableAsync);
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
        var securityRecovery = new Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber<Eve.Events.EventContext>(
            profile => npcAuthority.Profile(profile) is { Bounty: > 0 } npc ? npc.Bounty : null,
            async (context, characterId, token) =>
                1.0 + ((await skills.LevelAsync(context.Connection, context.Transaction, characterId, 3358, token)) * 0.05),
            (previous, bounty, fastTalk) => Eve.Npc.NpcSecurityStatusRecovery.TryNext(bounty, previous, fastTalk),
            async (context, characterId, token) =>
                await foundations.Relations.ValueAsync(Eve.Relations.RelationKinds.SecurityStatus.Name,
                    characterId, Eve.Relations.RelationStore.World, 0, context.Connection, context.Transaction, token),
            async (context, change, token) =>
            {
                await foundations.Relations.SetAsync(context.Connection, context.Transaction,
                    Eve.Relations.RelationKinds.SecurityStatus.Name, change.CharacterId, Eve.Relations.RelationStore.World,
                    change.NextStatus, "npc_bounty_recovery",
                    new { change.SourceEventId, change.NpcShipId, change.NpcTypeId, change.ProfileId, change.Bounty, change.FastTalkMultiplier },
                    cancellationToken: token);
                await context.PublishAsync("npc.security_status.recovered",
                    new { characterId = change.CharacterId, status = change.NextStatus },
                    $"character:{change.CharacterId}", token);
            });
        foundations.Events.Subscribe("npc.security-status-recovery", ["npc.killed"],
            async (context, token) => await securityRecovery.HandleAsync(context, context.Event.Id, context.Event.PayloadJson, token));
        foundations.Events.Subscribe("npc.security-status-notifications", ["npc.security_status.recovered"],
            (context, _) =>
            {
                var notice = context.Event.Json();
                if (notice.TryGetProperty("characterId", out var character) &&
                    character.TryGetInt64(out var characterId) && characterId > 0 &&
                    notice.TryGetProperty("status", out var status) &&
                    status.TryGetDouble(out var securityStatus) && double.IsFinite(securityStatus))
                {
                    characterSessions.Of(characterId)?.SendNotification("OnSecurityStatusUpdate", "charid", Py.Number(securityStatus));
                }
                return Task.CompletedTask;
            });
        var npcWrecks = new NpcWrecks(npcAuthority, () => NpcLoot.LoadAsync(sde), ships);
        spaceSessions = new SpaceSessions(sim, ships, dogma, stations, solarSystems, now, message => Console.Error.WriteLine($"eve-host: space: {message}")) { RookieShipOfRace = race => catalog.Race(race)?.ShipTypeId, Npcs = new NpcSelector(npcAuthority), NpcWrecks = npcWrecks, BeltRats = new BeltRats(npcAuthority, BeltRatConfig.FromEnvironment(Environment.GetEnvironmentVariable), type => NpcHull.HasHitPoints(dogma.Type(type))) };
        npcWrecks.Warm();
        // the periodic work of the space features is jobs of the shared scheduler, not timers of their own
        spaceSessions.UseBounties(new PgBountyStore(source, new WalletTransactions(source, walletSchema), npcSchema, message => Console.Error.WriteLine($"eve-host: bounty: {message}")));
        spaceSessions.Events = foundations.Events;   // what happens in space is announced on the bus as well
        spaceSessions.SafetyLevelOf = (character, token) => crimewatchStore.SafetyOfAsync(character, token);   // the ship safety the pilot has set
        spaceSessions.Aggression = async (request, token) => await crimewatchRuntime.RecordAggressionAsync(request, token);   // what an attack makes of the attacker
        spaceSessions.SecurityStatusOf = securityStatusOf;   // the ship in space shows it
        var weaponSafetyReadModel = new PersistenceWeaponTargetContextReadModel(
            AffiliationOfAsync,
            async (attacker, target, at, token) => crimewatchRuntime.Legal is { } legal && await legal(attacker, target, at, token),
            crimewatchRuntime.StateOfAsync,
            securityStatusOf,
            ReadGameStoreTableAsync,
            now);
        spaceSessions.WeaponTargetContextOf = weaponSafetyReadModel.ReadAsync;
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

    var agentLocationById = new Dictionary<long, long>();
    foreach (var row in await sde.ReadAsync("agtAgents"))
    {
        if (row.Doc.TryGetProperty("locationID", out var location) &&
            location.TryGetInt64(out var stationId) && stationId > 0)
            agentLocationById[row.Key] = stationId;
    }
    var structureDirectoryReadModel = new PersistenceStructureDirectoryReadModel(ReadGameStoreTableAsync, AffiliationOfAsync);
    var sovereigntyReadModel = new PersistenceSovFwReadModel(ReadGameStoreTableAsync, AffiliationOfAsync);
    var incursionAnomalyReadModel = new PersistenceIncursionAnomalyReadModel(
        foundations.Content.ActiveOfAsync,
        ReadGameStoreTableAsync,
        now);
    IReadOnlyList<IService> services =
    [
        new CharUnboundMgrService(accounts, characters, items, skills, wallets, creation, catalog, online, [new SkillsLoginHook(training, skillCatalog, characterSessions, announcer, now), corporationLogin], Random.Shared),
        // security status is the character's relation of that kind (Eve.Relations); crimewatch only asks for it
        new Eve.Services.Crimewatch.CrimewatchService(
            crimewatchStore, now,
            securityStatusOf),
        new SkillMgr2Service(),
        new SkillHandlerService(skills, skillCatalog, training, characters, announcer, now),
        new CharMgrService(characters, stations, charMgrReadModel.ReadAsync),
        new Eve.Services.CharacterInfo.OnlineStatusService(characterSessions, ReadWatchlistAsync),
        new StationService(stations, characters, online, now),
        .. Eve.Services.Core.SovFwService.Create(
            sovereigntyReadModel.ReadSnapshotAsync,
            sovereigntyReadModel.IsOnLocalFuelAccessGroupAsync),
        new Eve.Services.Core.IncursionAnomalyService(
            "map",
            incursionAnomalyReadModel.ReadIncursionsAsync,
            incursionAnomalyReadModel.ReadAnomaliesAsync,
            new MapService(foundations.Reads, now),
            now),
        new Eve.Services.Core.IncursionAnomalyService(
            "taleMgr",
            incursionAnomalyReadModel.ReadIncursionsAsync,
            incursionAnomalyReadModel.ReadAnomaliesAsync,
            nowUnixMs: now),
        new Eve.Services.Core.IncursionAnomalyService(
            "scanMgr",
            incursionAnomalyReadModel.ReadIncursionsAsync,
            incursionAnomalyReadModel.ReadAnomaliesAsync,
            nowUnixMs: now),
        HostInventoryServiceFactory.CreateInvBroker(items, new ItemTypeCatalog(sde), stations, ships, dogma, now, spaceContainers, spaceDrones),
        new DogmaIMService(dogma, ships, now, dogmaSync, spaceSessions, spaceSessions, spaceSessions, spaceSessions, log: message => Console.Error.WriteLine($"eve-host: ammo: {message}")),
        new AccountService(wallets, corporations, economyWallets),
        new Eve.Services.Market.MarketProxyService(market, marketTrading, marketTopology, characters, economyWallets, marketCatalog, now, characterSessions),
        new Eve.Services.Corporations.CorpMgrService(corporations, characters),
        new Eve.Services.Corporations.CorporationService(corporations, characters, characterSessions),
        new Eve.Services.Corporations.CorpRegistryService(corporations, characters, now, characterSessions)
        {
            AllianceAffiliationWriter = allianceDue.WriteAffiliationAsync
        },
        new Eve.Services.Corporations.StandingMgrService(foundations.Reads, corporations, now),
        new Eve.Services.Corporations.WarsInfoMgrService(foundations.Relations, now),
        new Eve.Services.Core.InsuranceService(new PersistenceInsuranceReadModel(mailStore), now),
        new MailService(
            ct => ReadGameStoreTableAsync("mail", ct),
            async (characterId, messageId, requestingSession, ct) =>
            {
                var statusPath = $"/mailboxes/{characterId}/statuses/{messageId}";
                if (mailCache.Read("mail", statusPath).Data is not JsonObject status)
                    return;
                var priorMask = long.TryParse(status["statusMask"]?.ToString(),
                    System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedMask) ? parsedMask : 0;
                if ((priorMask & 1) != 0)
                    return;
                status["statusMask"] = priorMask | 1L;
                var write = mailCache.Write("mail", statusPath, status);
                if (!write.Success)
                    throw new InvalidOperationException($"Could not mark mail {messageId} read: {write.ErrorMsg}");
                await mailCache.FlushTableAsync("mail", ct);
                if (characterSessions.Of(characterId) is { } otherSession &&
                    !ReferenceEquals(otherSession, requestingSession))
                {
                    otherSession.SendNotification("OnMailUpdatedByExternal", "clientID",
                        new PyTuple([new PyList([new PyInt(messageId)]), PyBool.True, PyNone.Instance]));
                }
            }),
        new NotificationMgrService(
            async (characterId, ct) =>
            {
                var notifications = await ReadGameStoreTableAsync("notifications", ct);
                return (notifications["boxes"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            },
            now),
        new CalendarMgrService(),
        new CalendarProxyService(),
        new MailingListService(async ct =>
        {
            var mailState = await ReadGameStoreTableAsync("mail", ct);
            return mailState["mailingLists"] as JsonObject;
        }),
        new RewardsService(),
        new StoreOffersService(async ct =>
            await ReadGameStoreTableAsync("newEdenStore", ct)),
        new IndustryJobsService(async ct =>
            await ReadGameStoreTableAsync("industryJobs", ct)),
        new Eve.Services.Core.CharacterMiningLedgerService(
            ct => ReadGameStoreTableAsync("miningLedger", ct)),
        new Eve.Services.Core.CorpMiningLedgerService(
            ct => ReadGameStoreTableAsync("miningLedger", ct)),
        new Eve.Services.Core.StructureDirectoryService(structureDirectoryReadModel),
        new AgentMissionService(
            async (characterId, ct) =>
            {
                var state = await ReadGameStoreTableAsync("missionRuntimeState", ct);
                return (state["charactersByID"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            },
            async (characterId, bookmarkIds, ct) =>
            {
                var state = await ReadGameStoreTableAsync("bookmarks", ct);
                var records = state["records"] as JsonObject;
                var bookmarks = new Dictionary<long, JsonObject>();
                foreach (var bookmarkId in bookmarkIds.Distinct())
                {
                    if (records?[bookmarkId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject row)
                        bookmarks[bookmarkId] = row;
                }
                return bookmarks;
            },
            async (characterId, ct) =>
            {
                var state = await ReadGameStoreTableAsync("researchRuntimeState", ct);
                return (state["charactersByID"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonArray;
            },
            (characterId, agentId, session) =>
            {
                var stationId = session.GetInt("stationid");
                return stationId is { } station &&
                    (!agentLocationById.TryGetValue(agentId, out var agentStationId) || station == agentStationId);
            }),
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

sealed class PersistenceInsuranceReadModel(PersistenceStore store) : Eve.Services.Core.IInsuranceReadModel
{
    public async ValueTask<IReadOnlyDictionary<long, long>> GetInsurancePricesCentsAsync(
        IReadOnlyCollection<long> typeIds, CancellationToken cancellationToken)
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, "shipInsurancePrices", cancellationToken);
        var rows = state["byTypeID"] as JsonObject;
        var prices = new Dictionary<long, long>();
        foreach (var typeId in typeIds.Distinct())
        {
            if (rows?[typeId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject row)
                continue;
            var cents = Integer(row["fullInsurancePriceCents"]);
            if (cents > 0)
                prices[typeId] = cents;
        }
        return prices;
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.InsuranceContractSnapshot>> GetContractsAsync(
        long ownerId, Eve.Services.Core.InsuranceOwnerKind ownerKind, CancellationToken cancellationToken)
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, "insuranceContracts", cancellationToken);
        var rows = state["contractsByShipID"] as JsonObject;
        if (rows is null)
            return [];
        var contracts = new List<Eve.Services.Core.InsuranceContractSnapshot>();
        foreach (var (key, node) in rows)
        {
            if (node is not JsonObject row)
                continue;
            var contract = Contract(row, LongKey(key));
            if (contract is not null && contract.OwnerId == ownerId && contract.OwnerKind == ownerKind)
                contracts.Add(contract);
        }
        return contracts;
    }

    public async ValueTask<Eve.Services.Core.InsuranceContractSnapshot?> GetContractForShipAsync(
        long shipId, CancellationToken cancellationToken)
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, "insuranceContracts", cancellationToken);
        return (state["contractsByShipID"] as JsonObject)?[shipId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject row
            ? Contract(row, shipId)
            : null;
    }

    private static Eve.Services.Core.InsuranceContractSnapshot? Contract(JsonObject row, long fallbackShipId)
    {
        var shipId = Integer(row["shipID"]);
        if (shipId <= 0) shipId = fallbackShipId;
        var ownerKind = Text(row["ownerKind"]).Equals("corporation", StringComparison.OrdinalIgnoreCase)
            ? Eve.Services.Core.InsuranceOwnerKind.Corporation
            : Eve.Services.Core.InsuranceOwnerKind.Character;
        var ownerId = Integer(row["ownerID"]);
        var typeId = Integer(row["typeID"]);
        if (shipId <= 0 || ownerId <= 0 || typeId <= 0)
            return null;
        return new Eve.Services.Core.InsuranceContractSnapshot(
            Integer(row["contractID"]), shipId, typeId, ownerId, ownerKind,
            Number(row["fraction"]), Integer(row["startDate"]), Integer(row["endDate"]),
            Text(row["status"]) is { Length: > 0 } status ? status : "active");
    }

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var integer) ? integer : 0;

    private static long LongKey(string key) => Integer(JsonValue.Create(key));

    private static double Number(JsonNode? value) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}

public sealed class PersistenceStructureDirectoryReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf) : Eve.Services.Core.IStructureDirectoryReadCallbacks
{
    private const long AnsiblexJumpBridgeTypeId = 35841;
    private const int JumpBridgeServiceId = 15;
    private const int DockingServiceId = 1;
    private const int JumpBridgeAccessSettingId = 34;
    private const int DockAccessSettingId = 19;

    private sealed record Bridge(long Id, long SolarSystemId, long DestinationSolarSystemId, JsonObject Value);

    public async ValueTask<PyValue> GetJumpBridgesWithMyAccessAsync(ServiceCall call)
    {
        var access = await ReadAccessContextAsync(call, call.Cancellation);
        if (access.CharacterId <= 0)
            return EmptyJumpBridgeAccess();

        var structures = await readTable("structures", call.Cancellation);
        var bridges = ReadBridges(structures);
        var profiles = await readTable("structureProfiles", call.Cancellation);
        var groups = await readTable("accessGroups", call.Cancellation);
        var pairs = ReciprocalPairs(bridges);
        var pairsWire = new List<PyValue>();
        var accessIDs = new HashSet<long>();
        var deniedIDs = new HashSet<long>();

        foreach (var (source, destination) in pairs)
        {
            pairsWire.Add(new PyList([BridgeMapPayload(source), BridgeMapPayload(destination)]));
            foreach (var bridge in new[] { source, destination })
            {
                if (CanUseJumpBridge(bridge, access, profiles, groups))
                    accessIDs.Add(bridge.Id);
                else
                    deniedIDs.Add(bridge.Id);
            }
        }

        return new PyTuple([
            new PyList(pairsWire),
            new PyList(accessIDs.Order().Select(InventoryWire.Id).ToArray()),
            new PyList(deniedIDs.Order().Select(InventoryWire.Id).ToArray()),
        ]);
    }

    public async ValueTask<PyValue> GetNearbyJumpBridgesAsync(ServiceCall call)
    {
        var access = await ReadAccessContextAsync(call, call.Cancellation);
        if (access.CharacterId <= 0)
            return new PyList([]);

        var structures = await readTable("structures", call.Cancellation);
        var profiles = await readTable("structureProfiles", call.Cancellation);
        var groups = await readTable("accessGroups", call.Cancellation);
        var currentSystem = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        return new PyList(ReadBridges(structures)
            .Where(bridge => IsJumpBridgeServiceOnline(bridge) && CanUseJumpBridge(bridge, access, profiles, groups))
            .Select(bridge => NearbyBridgePayload(bridge, currentSystem))
            .ToArray());
    }

    public async ValueTask<PyValue> GetStructureDescriptionAsync(ServiceCall call)
    {
        var structureId = Eve.Macho.JsSemantics.ToPositiveInteger(call.Arg(0));
        if (structureId <= 0)
            return new PyStr("");
        var structures = await readTable("structures", call.Cancellation);
        return structures[structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject structure
            ? new PyStr(Text(structure["description"])[..Math.Min(1000, Text(structure["description"]).Length)])
            : new PyStr("");
    }

    private async ValueTask<(long CharacterId, Eve.Relations.Affiliation Affiliation)> ReadAccessContextAsync(
        ServiceCall call,
        CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId.GetValueOrDefault();
        if (characterId <= 0)
            return (0, new Eve.Relations.Affiliation(null, null, null));
        return (characterId, await affiliationOf(characterId, cancellationToken));
    }

    private static IReadOnlyList<Bridge> ReadBridges(JsonObject state)
    {
        var bridges = new List<Bridge>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure || Integer(structure["typeID"]) != AnsiblexJumpBridgeTypeId ||
                Truthy(structure["destroyedAt"]))
                continue;
            var id = Integer(structure["structureID"]);
            if (id <= 0)
                id = Integer(JsonValue.Create(key));
            var systemId = Integer(structure["solarSystemID"]);
            var destinationId = JumpBridgeDestination(structure);
            if (id > 0 && systemId > 0)
                bridges.Add(new Bridge(id, systemId, destinationId, structure));
        }
        bridges.Sort((left, right) => left.Id.CompareTo(right.Id));
        return bridges;
    }

    private static IReadOnlyList<(Bridge Source, Bridge Destination)> ReciprocalPairs(IReadOnlyList<Bridge> bridges)
    {
        var pairs = new List<(Bridge Source, Bridge Destination)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in bridges)
        {
            if (!IsJumpBridgeServiceOnline(source) || source.DestinationSolarSystemId <= 0)
                continue;
            var destination = bridges.FirstOrDefault(candidate =>
                candidate.Id != source.Id &&
                candidate.SolarSystemId == source.DestinationSolarSystemId &&
                candidate.DestinationSolarSystemId == source.SolarSystemId &&
                IsJumpBridgeServiceOnline(candidate));
            if (destination is null)
                continue;
            var pairKey = source.Id < destination.Id
                ? $"{source.Id}:{destination.Id}"
                : $"{destination.Id}:{source.Id}";
            if (seen.Add(pairKey))
                pairs.Add((source, destination));
        }
        return pairs;
    }

    private static bool IsJumpBridgeServiceOnline(Bridge bridge) =>
        Integer((bridge.Value["serviceStates"] as JsonObject)?[JumpBridgeServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) == 1;

    private static long JumpBridgeDestination(JsonObject structure)
    {
        var flags = structure["devFlags"] as JsonObject;
        return Positive(Integer(flags?["destinationSolarsystemID"])) is var direct && direct > 0
            ? direct
            : Positive(Integer(flags?["sovereigntyJumpBridgeDestinationSolarsystemID"]));
    }

    private static bool CanUseJumpBridge(
        Bridge bridge,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        JsonObject profileState,
        JsonObject accessGroupState)
    {
        if (!IsJumpBridgeServiceOnline(bridge))
            return false;

        var ownerCorporationId = Integer(bridge.Value["ownerCorpID"]);
        if (ownerCorporationId <= 0)
            ownerCorporationId = Integer(bridge.Value["ownerID"]);
        var ownerAllianceId = Integer(bridge.Value["allianceID"]);
        var ownerAccess =
            ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId ||
            ownerAllianceId > 0 && access.Affiliation.Alliance == ownerAllianceId;
        var dockAccess = HasDockAccess(bridge, access, ownerCorporationId, ownerAllianceId, profileState, accessGroupState);
        if (!ownerAccess && !dockAccess)
            return false;

        return ProfileSettingAllows(
            bridge.Value,
            ownerCorporationId,
            access,
            profileState,
            accessGroupState,
            JumpBridgeAccessSettingId);
    }

    private static bool HasDockAccess(
        Bridge bridge,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        long ownerCorporationId,
        long ownerAllianceId,
        JsonObject profileState,
        JsonObject accessGroupState)
    {
        if (Integer((bridge.Value["serviceStates"] as JsonObject)?[DockingServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) != 1)
            return false;

        var accessProfile = bridge.Value["accessProfile"] as JsonObject;
        var policy = Text(accessProfile?["docking"]);
        if (policy.Length == 0)
            policy = "public";
        var policyAllows = policy switch
        {
            "public" => true,
            "none" => false,
            "owner" or "corp" => ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId,
            "alliance" => ownerAllianceId > 0
                ? access.Affiliation.Alliance == ownerAllianceId
                : ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId,
            _ => false,
        };
        return policyAllows && ProfileSettingAllows(
            bridge.Value,
            ownerCorporationId,
            access,
            profileState,
            accessGroupState,
            DockAccessSettingId);
    }

    private static bool ProfileSettingAllows(
        JsonObject structure,
        long ownerCorporationId,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        JsonObject profileState,
        JsonObject accessGroupState,
        int settingId)
    {
        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        var requestedProfileId = Integer(structure["profileID"]);
        JsonObject? profile = requestedProfileId > 0
            ? profiles[requestedProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : null;
        if (profile is not null && ownerCorporationId > 0 && Integer(profile["corporationID"]) != ownerCorporationId)
            profile = null;
        if (profile is null)
        {
            profile = profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate => Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));
        }
        if (profile is null)
        {
            // The default owner-corporation profile has no stored ACL; only that corporation can rely on the fallback.
            return requestedProfileId <= 0 && ownerCorporationId > 0 && access.Affiliation.Corporation == ownerCorporationId;
        }

        var settings = profile["settingsBySettingID"] as JsonObject;
        if (settings?[settingId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonArray settingGroups ||
            settingGroups.Count == 0)
        {
            return requestedProfileId <= 0 && ownerCorporationId > 0 &&
                access.Affiliation.Corporation == ownerCorporationId;
        }

        var hasDefaultGroup = false;
        var bestRank = 0;
        foreach (var node in settingGroups)
        {
            if (node is not JsonObject row)
                continue;
            var groupId = Integer(row["groupID"], -1);
            if (groupId == 0)
            {
                hasDefaultGroup = true;
                continue;
            }
            bestRank = Math.Max(bestRank, AccessGroupMatchRank(groupId, access, accessGroupState));
        }
        return bestRank > 0 || hasDefaultGroup;
    }

    internal static int AccessGroupMatchRank(
        long groupId,
        (long CharacterId, Eve.Relations.Affiliation Affiliation) access,
        JsonObject accessGroupState)
    {
        if (groupId <= 0 || access.CharacterId <= 0 ||
            accessGroupState[groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject group)
            return 0;

        var members = group["members"] as JsonArray ?? [];
        var direct = members.OfType<JsonObject>()
            .FirstOrDefault(member => Integer(member["memberID"], -1) == access.CharacterId);
        if (direct is not null)
            return AllowedMembership(direct) ? 430 : 0;
        if (Integer(group["creatorID"]) == access.CharacterId)
            return 430;

        foreach (var (memberId, specificity) in new (long Id, int Specificity)[]
        {
            (access.Affiliation.Corporation ?? 0, 300),
            (access.Affiliation.Alliance ?? 0, 200),
            (0, 100),
        })
        {
            var member = members.OfType<JsonObject>()
                .FirstOrDefault(candidate => Integer(candidate["memberID"], -1) == memberId);
            if (member is null)
                continue;
            return AllowedMembership(member) ? specificity + 30 : 0;
        }
        return 0;
    }

    private static bool AllowedMembership(JsonObject member)
    {
        var membership = Integer(member["membershipType"], 0);
        return membership is 0 or 1 or 2;
    }

    private static PyValue BridgeMapPayload(Bridge bridge)
    {
        var owner = Integer(bridge.Value["ownerCorpID"]);
        if (owner <= 0)
            owner = Integer(bridge.Value["ownerID"]);
        var name = Text(bridge.Value["itemName"]);
        if (name.Length == 0)
            name = Text(bridge.Value["name"]);
        if (name.Length == 0)
            name = "Ansiblex Jump Bridge";
        return Py.KeyVal(
            ("structureID", new PyInt(bridge.Id)),
            ("itemID", new PyInt(bridge.Id)),
            ("typeID", new PyInt(Integer(bridge.Value["typeID"]))),
            ("solarSystemID", new PyInt(bridge.SolarSystemId)),
            ("ownerID", owner > 0 ? new PyInt(owner) : PyNone.Instance),
            ("corporationID", owner > 0 ? new PyInt(owner) : PyNone.Instance),
            ("allianceID", Integer(bridge.Value["allianceID"]) > 0 ? new PyInt(Integer(bridge.Value["allianceID"])) : PyNone.Instance),
            ("itemName", new PyStr(name)),
            ("structureName", new PyStr(name)),
            ("destinationSolarsystemID", bridge.DestinationSolarSystemId > 0 ? new PyInt(bridge.DestinationSolarSystemId) : PyNone.Instance));
    }

    private static PyValue NearbyBridgePayload(Bridge bridge, long currentSystem)
    {
        var owner = Integer(bridge.Value["ownerCorpID"]);
        if (owner <= 0)
            owner = Integer(bridge.Value["ownerID"]);
        var name = Text(bridge.Value["itemName"]);
        if (name.Length == 0)
            name = Text(bridge.Value["name"]);
        if (name.Length == 0)
            name = "Ansiblex Jump Bridge";
        return Py.KeyVal(
            ("structureID", new PyInt(bridge.Id)),
            ("typeID", new PyInt(Integer(bridge.Value["typeID"]))),
            ("solarSystemID", new PyInt(bridge.SolarSystemId)),
            ("ownerID", owner > 0 ? new PyInt(owner) : PyNone.Instance),
            ("structureName", new PyStr(name)),
            ("destinationSolarsystemID", bridge.DestinationSolarSystemId > 0 ? new PyInt(bridge.DestinationSolarSystemId) : PyNone.Instance),
            ("alignedToCurrentSystem", PyValue.Of(bridge.DestinationSolarSystemId > 0 && bridge.DestinationSolarSystemId == currentSystem)));
    }

    private static PyValue EmptyJumpBridgeAccess() =>
        new PyTuple([new PyList([]), new PyList([]), new PyList([])]);

    private static long Positive(long value) => value > 0 ? value : 0;
    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;
    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
    private static bool Truthy(JsonNode? value)
    {
        if (value is null)
            return false;
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean))
            return boolean;
        if (double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number != 0;
        return value.ToString().Length > 0;
    }
}

public sealed class PersistenceSovFwReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf)
{
    public async ValueTask<Eve.Services.Core.SovFwReadSnapshot> ReadSnapshotAsync(
        Eve.Services.ServiceCall call) =>
        await ReadSnapshotAsync(call.Cancellation);

    public async ValueTask<bool> IsOnLocalFuelAccessGroupAsync(Eve.Services.ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0)
            return false;
        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        if (systemId <= 0)
            return false;
        var snapshot = await ReadSnapshotAsync(call.Cancellation);
        if (!snapshot.FuelAccessGroupIdsBySolarSystemId.TryGetValue(systemId, out var groupId) ||
            groupId is not { } actualGroupId || actualGroupId <= 0)
            return false;
        var groups = await readTable("accessGroups", call.Cancellation);
        var affiliation = await affiliationOf(characterId, call.Cancellation);
        return PersistenceStructureDirectoryReadModel.AccessGroupMatchRank(
            actualGroupId, (characterId, affiliation), groups) > 0;
    }

    public async ValueTask<Eve.Services.Core.SovFwReadSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        if (systems is null || systems.Count == 0)
            return Eve.Services.Core.SovFwReadSnapshot.Empty;

        var claims = new Dictionary<long, Eve.Services.Core.SovClaimRecord>();
        var hubs = new Dictionary<long, Eve.Services.Core.SovHubRecord>();
        var structures = new Dictionary<long, IReadOnlyList<Eve.Services.Core.SovStructureRecord>>();
        var occupations = new List<Eve.Services.Core.FwOccupationRecord>();
        var fuelGroups = new Dictionary<long, long?>();

        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system)
                continue;
            var systemId = Positive(Integer(system["solarSystemID"]));
            if (systemId <= 0 && !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out systemId))
                continue;
            if (systemId <= 0)
                continue;

            var claimStructureId = NullablePositive(system["claimStructureID"]);
            var corporationId = NullablePositive(system["corporationID"]);
            var allianceId = NullablePositive(system["allianceID"]);
            if (claimStructureId is not null || corporationId is not null || allianceId is not null)
                claims[systemId] = new(claimStructureId, corporationId, allianceId);

            var hubId = NullablePositive(system["infrastructureHubID"]);
            var claimTime = NullablePositive(system["claimTime"]);
            if (hubId is not null)
                hubs[systemId] = new(hubId, corporationId, allianceId, claimTime);

            var groupId = NullablePositive(system["fuelAccessGroupID"]);
            if (system.ContainsKey("fuelAccessGroupID"))
                fuelGroups[systemId] = groupId;

            var systemStructures = (system["structures"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(structure => ReadStructure(structure, systemId))
                .Where(structure => structure is not null)
                .Cast<Eve.Services.Core.SovStructureRecord>()
                .OrderBy(structure => structure.ItemId)
                .ToArray();
            if (systemStructures.Length > 0)
                structures[systemId] = systemStructures;

            var warzoneId = Positive(Integer(system["warzoneID"] ?? system["warZoneID"]));
            var occupierId = Positive(Integer(system["factionID"]));
            if (occupierId <= 0)
                occupierId = Positive(Integer(system["ownerFactionID"]));
            if (occupierId <= 0)
                occupierId = Positive(Integer(system["occupierID"] ?? system["fwOccupierID"]));
            if (warzoneId > 0 && occupierId > 0)
                occupations.Add(new(warzoneId, systemId, occupierId));
        }

        return new Eve.Services.Core.SovFwReadSnapshot(claims, hubs, structures, occupations, fuelGroups);
    }

    private static Eve.Services.Core.SovStructureRecord? ReadStructure(JsonObject value, long systemId)
    {
        var itemId = Positive(Integer(value["itemID"] ?? value["structureID"]));
        var typeId = Positive(Integer(value["typeID"]));
        if (itemId <= 0 || typeId <= 0)
            return null;
        return new(
            itemId,
            typeId,
            NullablePositive(value["ownerID"]),
            NullablePositive(value["corporationID"] ?? value["ownerCorpID"]),
            NullablePositive(value["allianceID"]),
            Positive(Integer(value["solarSystemID"])) is var storedSystem && storedSystem > 0 ? storedSystem : systemId,
            NullableInt(value["campaignEventType"]),
            NullablePositive(value["campaignStartTimeFileTime"] ?? value["campaignStartTime"]),
            NumberMap(value["campaignScoresByTeam"] ?? value["campaignScores"]),
            NullablePositive(value["vulnerableStartTimeFileTime"] ?? value["vulnerableStartTime"]),
            NullablePositive(value["vulnerableEndTimeFileTime"] ?? value["vulnerableEndTime"]),
            Number(value["defenseMultiplier"], 1),
            Truthy(value["isCapital"]));
    }

    private static IReadOnlyDictionary<long, double>? NumberMap(JsonNode? value)
    {
        if (value is not JsonObject values)
            return null;
        var parsed = new Dictionary<long, double>();
        foreach (var (key, node) in values)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
                continue;
            var number = Number(node, double.NaN);
            if (double.IsFinite(number))
                parsed[id] = number;
        }
        return parsed;
    }

    private static long? NullablePositive(JsonNode? value)
    {
        var number = Integer(value);
        return number > 0 ? number : null;
    }

    private static int? NullableInt(JsonNode? value)
    {
        var number = Integer(value, int.MinValue);
        return number == int.MinValue ? null : checked((int)number);
    }

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static double Number(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;

    private static bool Truthy(JsonNode? value)
    {
        if (value is JsonValue scalar && scalar.TryGetValue<bool>(out var boolean))
            return boolean;
        if (double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            return number != 0;
        return false;
    }
}

public static class PersistenceReadHelpers
{
    public static async Task<JsonObject> ReadOptionalTableAsync(
        Func<string, CancellationToken, Task<JsonObject>> readTableAsync,
        string table,
        CancellationToken cancellationToken)
    {
        try
        {
            return await readTableAsync(table, cancellationToken);
        }
        catch (Eve.GameStore.PersistenceException error) when (error.Code == Eve.GameStore.PersistenceCodes.TableNotFound)
        {
            return new JsonObject();
        }
    }

    public static Task<JsonObject> ReadOptionalTableAsync(
        PersistenceStore store,
        string table,
        CancellationToken cancellationToken) =>
        ReadOptionalTableAsync(
            (name, token) => store.LoadTableAsync(name, createIfMissing: false, token),
            table,
            cancellationToken);
}

public sealed class PersistenceCharMgrReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTableAsync)
{
    private const string DefaultFoldersLabel = "S:Folders";
    private const string DefaultFoldersNote = "1::F::0::Main|";

    public async ValueTask<CharMgrPersonalData?> ReadAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
        {
            return null;
        }

        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var characterTable = await ReadOptionalTableAsync("characters", cancellationToken);
        var notesTable = await ReadOptionalTableAsync("characterNotes", cancellationToken);
        var character = characterTable[key] as JsonObject;
        var notes = (notesTable["characters"] as JsonObject)?[key] as JsonObject;

        var nextNoteId = Number((notesTable["_meta"] as JsonObject)?["nextNoteID"]);
        return new CharMgrPersonalData(
            ReadContacts(character?["personalContacts"]),
            ReadBlockedSenders(character?["blockedOwners"]),
            ReadLabels(character?["personalContactLabels"]),
            ReadOwnerNotes(notes?["notes"]),
            ReadItemNotes(notes?["itemNotes"]))
        {
            NextOwnerNoteId = nextNoteId > 0 ? nextNoteId : null,
        };
    }

    private Task<JsonObject> ReadOptionalTableAsync(string table, CancellationToken cancellationToken) =>
        PersistenceReadHelpers.ReadOptionalTableAsync(readTableAsync, table, cancellationToken);

    private static IReadOnlyList<CharMgrContact> ReadContacts(JsonNode? source) =>
        Rows(source)
            .Select(entry => new CharMgrContact(
                Number(entry.Row["contactID"], NumberFromKey(entry.Key)),
                Boolean(entry.Row["inWatchlist"]),
                Number(entry.Row["relationshipID"]),
                Number(entry.Row["labelMask"])))
            .Where(contact => contact.ContactId > 0)
            .OrderBy(contact => contact.ContactId)
            .ToArray();

    private static IReadOnlyList<long> ReadBlockedSenders(JsonNode? source) =>
        Rows(source)
            .Select(entry => Number(
                entry.Row["senderID"],
                Number(entry.Row["contactID"], NumberFromKey(entry.Key))))
            .Where(senderId => senderId > 0)
            .Distinct()
            .Order()
            .ToArray();

    private static IReadOnlyDictionary<long, CharMgrContactLabel> ReadLabels(JsonNode? source)
    {
        var labels = new SortedDictionary<long, CharMgrContactLabel>();
        foreach (var entry in Rows(source))
        {
            var labelId = NumberFromKey(entry.Key);
            if (labelId <= 0)
            {
                continue;
            }

            labels[labelId] = new CharMgrContactLabel(
                Text(entry.Row["name"]),
                Number(entry.Row["color"]));
        }

        return labels;
    }

    private static IReadOnlyList<CharMgrOwnerNote> ReadOwnerNotes(JsonNode? source) =>
        Rows(source)
            .Select(entry => new CharMgrOwnerNote(
                Number(entry.Row["noteID"], NumberFromKey(entry.Key)),
                Text(entry.Row["label"]),
                Text(entry.Row["note"])))
            .Where(note => note.NoteId > 0)
            .OrderBy(note => note.NoteId)
            .ToArray();

    private static IReadOnlyDictionary<long, string> ReadItemNotes(JsonNode? source)
    {
        var notes = new SortedDictionary<long, string>();
        if (source is not JsonObject byId)
        {
            return notes;
        }

        foreach (var (key, value) in byId)
        {
            var itemId = Number(value is JsonObject entry ? entry["itemID"] : null, NumberFromKey(key));
            if (itemId <= 0)
            {
                continue;
            }

            notes[itemId] = value is JsonObject row ? Text(row["note"]) : Text(value);
        }

        return notes;
    }

    private static IEnumerable<(string Key, JsonObject Row)> Rows(JsonNode? source)
    {
        if (source is JsonObject byKey)
        {
            foreach (var (key, value) in byKey)
            {
                if (value is JsonObject row)
                {
                    yield return (key, row);
                }
            }
        }
        else if (source is JsonArray rows)
        {
            for (var index = 0; index < rows.Count; index++)
            {
                if (rows[index] is JsonObject row)
                {
                    yield return (index.ToString(System.Globalization.CultureInfo.InvariantCulture), row);
                }
            }
        }
    }

    private static long NumberFromKey(string key) =>
        long.TryParse(key, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static long Number(JsonNode? node, long fallback = 0)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
        {
            return Number(wrapped, fallback);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var number))
            {
                return number;
            }
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }

        return fallback;
    }

    private static bool Boolean(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
        {
            return Boolean(wrapped);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var boolean))
            {
                return boolean;
            }
            if (value.TryGetValue<long>(out var number))
            {
                return number != 0;
            }
            if (value.TryGetValue<string>(out var text))
            {
                return text.Trim().ToLowerInvariant() is "1" or "true";
            }
        }

        return false;
    }

    private static string Text(JsonNode? node)
    {
        if (node is JsonObject wrapper && wrapper["value"] is { } wrapped)
        {
            return Text(wrapped);
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
            {
                return text;
            }
            return value.ToString();
        }

        return "";
    }
}

public sealed class PersistenceIncursionAnomalyReadModel(
    Func<string, long, CancellationToken, Task<IReadOnlyList<Eve.Content.ContentInstance>>> activeOf,
    Func<string, CancellationToken, Task<JsonObject>> readGameStoreTable,
    Func<long> nowUnixMs)
{
    private const long GlobalOwnerId = 0;
    private static readonly HashSet<string> ActiveDungeonStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "active", "seeded", "paused"
    };

    public async ValueTask<IReadOnlyList<JsonObject>> ReadIncursionsAsync(CancellationToken cancellationToken)
    {
        var now = nowUnixMs();
        var active = await activeOf("incursion", GlobalOwnerId, cancellationToken);
        var result = new List<JsonObject>();
        foreach (var instance in active)
        {
            if (instance.ExpiresMs is { } expires && expires <= now ||
                ParseObject(instance.DataJson) is not { } data)
                continue;
            if (data["taleID"] is null)
                data["taleID"] = instance.Id;
            if (data["state"] is null)
                data["state"] = instance.State;
            result.Add(data);
        }
        return result;
    }

    public async ValueTask<IReadOnlyList<JsonObject>> ReadAnomaliesAsync(
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
            return [];
        var now = nowUnixMs();
        var result = new Dictionary<long, JsonObject>();

        foreach (var instance in await activeOf("anomaly", solarSystemId, cancellationToken))
        {
            if (instance.ExpiresMs is { } expires && expires <= now ||
                ParseObject(instance.DataJson) is not { } data)
                continue;
            if (data["solarSystemID"] is null)
                data["solarSystemID"] = solarSystemId;
            if (Positive(Integer(data["solarSystemID"])) != solarSystemId)
                continue;
            var siteId = Positive(Integer(data["siteID"]));
            if (siteId <= 0)
                continue;
            if (data["instanceID"] is null)
                data["instanceID"] = instance.Id;
            result[siteId] = data;
        }

        var dungeonState = await readGameStoreTable("dungeonRuntimeState", cancellationToken);
        var instancesById = dungeonState["instancesByID"] as JsonObject;
        if (instancesById is not null)
        {
            foreach (var (key, node) in instancesById)
            {
                if (node is not JsonObject instance ||
                    !ActiveDungeonStates.Contains(Text(instance["lifecycleState"])) ||
                    !string.Equals(Text(instance["siteKind"]), "anomaly", StringComparison.OrdinalIgnoreCase))
                    continue;
                var systemId = Positive(Integer(instance["solarSystemID"]));
                if (systemId != solarSystemId)
                    continue;
                var siteId = Positive(Integer(instance["siteID"] ?? (instance["metadata"] as JsonObject)?["siteID"]));
                var targetId = Text(instance["targetID"]);
                if (siteId <= 0 || targetId.Length == 0)
                    continue;
                var copy = (JsonObject)instance.DeepClone();
                copy["siteID"] = siteId;
                copy["solarSystemID"] = solarSystemId;
                if (copy["instanceID"] is null && long.TryParse(key, out var instanceId) && instanceId > 0)
                    copy["instanceID"] = instanceId;
                result.TryAdd(siteId, copy);
            }
        }

        return result.OrderBy(entry => entry.Key).Select(entry => entry.Value).ToArray();
    }

    private static JsonObject? ParseObject(string json)
    {
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static long Positive(long value) => value > 0 ? value : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}

public sealed class PersistenceWeaponTargetContextReadModel(
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf,
    Func<long, long, long, CancellationToken, Task<bool>> activeWar,
    Func<long, CancellationToken, Task<Eve.Crimewatch.CombatState>> crimewatchStateOf,
    Func<long, CancellationToken, Task<double>> securityStatusOf,
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long> nowUnixMs)
{
    public async Task<Eve.Services.Space.WeaponTargetContext> ReadAsync(
        long attackerCharacterId,
        long targetCharacterId,
        CancellationToken cancellationToken)
    {
        if (attackerCharacterId <= 0 || targetCharacterId <= 0)
            return default;

        var now = nowUnixMs();
        var attackerAffiliationTask = affiliationOf(attackerCharacterId, cancellationToken);
        var targetAffiliationTask = affiliationOf(targetCharacterId, cancellationToken);
        var targetStateTask = crimewatchStateOf(targetCharacterId, cancellationToken);
        var targetSecurityTask = securityStatusOf(targetCharacterId, cancellationToken);
        var crimewatchRowsTask = readTable("crimewatchRuntime", cancellationToken);
        var corporationRowsTask = readTable("corporationRuntime", cancellationToken);
        await Task.WhenAll(attackerAffiliationTask, targetAffiliationTask, targetStateTask, targetSecurityTask, crimewatchRowsTask, corporationRowsTask);

        var attackerAffiliation = await attackerAffiliationTask;
        var targetAffiliation = await targetAffiliationTask;
        var targetState = await targetStateTask;
        var targetSecurity = await targetSecurityTask;
        var activeWarState = await activeWar(attackerCharacterId, targetCharacterId, now, cancellationToken);
        var activeLimitedEngagement = HasActiveLimitedEngagement(
            await crimewatchRowsTask, attackerCharacterId, targetCharacterId, now);
        var legalFriendlyFire = attackerAffiliation.Corporation is { } corporationId &&
            targetAffiliation.Corporation == corporationId &&
            FriendlyFireIsEnabled(await corporationRowsTask, corporationId, now);

        return new Eve.Services.Space.WeaponTargetContext(
            AttackerIsPlayer: true,
            TargetIsPlayerProperty: true,
            SamePilot: attackerCharacterId == targetCharacterId,
            TargetIsCriminal: targetState.IsCriminal(now),
            TargetIsSuspect: targetState.IsSuspect(now),
            TargetIsOutlaw: targetSecurity <= -5.0,
            ActiveWar: activeWarState,
            ActiveLimitedEngagement: activeLimitedEngagement,
            LegalFriendlyFire: legalFriendlyFire);
    }

    private static bool HasActiveLimitedEngagement(
        JsonObject state,
        long firstCharacterId,
        long secondCharacterId,
        long nowUnixMs)
    {
        var low = Math.Min(firstCharacterId, secondCharacterId);
        var high = Math.Max(firstCharacterId, secondCharacterId);
        foreach (var node in state)
        {
            if (node.Value is not JsonObject row ||
                !string.Equals(Text(row["kind"]), "limitedEngagement", StringComparison.Ordinal))
                continue;
            var first = Integer(row["firstCharacterID"]);
            var second = Integer(row["secondCharacterID"]);
            if (Math.Min(first, second) != low || Math.Max(first, second) != high)
                continue;
            var engagement = row["engagement"] as JsonObject;
            var expiresAtMs = Integer(engagement?["expiresAtMs"]);
            if (expiresAtMs > nowUnixMs)
                return true;
        }
        return false;
    }

    private static bool FriendlyFireIsEnabled(JsonObject state, long corporationId, long nowUnixMs)
    {
        var corporations = state["corporations"] as JsonObject;
        if (corporations?[corporationId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject corporation)
            return false;
        if (corporation["aggressionSettings"] is not JsonObject settings)
            return Integer(corporation["friendlyFire"]) == 1;

        var nowFileTime = checked((nowUnixMs + 11_644_473_600_000L) * 10_000L);
        var enableAfter = NullableInteger(settings["enableAfter"]);
        var disableAfter = NullableInteger(settings["disableAfter"]);
        var enablePast = enableAfter is { } enable && enable <= nowFileTime;
        var disablePast = disableAfter is { } disable && disable <= nowFileTime;
        if (disablePast && enablePast)
            return disableAfter < enableAfter;
        if (disablePast)
            return false;
        if (enablePast)
            return true;
        return true;
    }

    private static long? NullableInteger(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;

    private static long Integer(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";
}

public static class HostInventoryServiceFactory
{
    public static InvBrokerService CreateInvBroker(
        PgItemStore items,
        ItemTypeCatalog types,
        StationCatalog stations,
        CharacterShips ships,
        DogmaStatic statics,
        Func<long> nowUnixMs,
        Eve.Services.Ships.ISpaceContainers? space,
        Eve.Services.Ships.ISpaceDroneBays? droneBays) =>
        new(items, types, stations, ships, statics, nowUnixMs, space, droneBays);
}
