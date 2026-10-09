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
using Sentry;
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
//   EVE_DEV_AUTO_CREATE    0 refuses unknown user names                                       (default 0)

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

var sentryDsn = Env("SENTRY_DSN", "");
using var sentry = string.IsNullOrWhiteSpace(sentryDsn)
    ? null
    : SentrySdk.Init(options =>
    {
        options.Dsn = sentryDsn;
        options.Environment = Env("SENTRY_ENVIRONMENT", "development");
        options.SendDefaultPii = false;
    });

var endpoint = Env("EVE_HOST", "/tmp/eve-host.sock");
var newCharacterExperienceMode = Env(
    "EVE_NEW_CHARACTER_EXPERIENCE_MODE",
    Flag("EVE_AIR_NPE_ENABLED", false) ? "tutorial" : "disabled").Trim().ToLowerInvariant();
if (newCharacterExperienceMode is not ("disabled" or "tutorial" or "cinematic"))
    throw new InvalidOperationException("EVE_NEW_CHARACTER_EXPERIENCE_MODE must be disabled, tutorial, or cinematic.");
var options = new ClusterOptions
{
    ImageServerUrl = Env("EVE_IMAGE_SERVER_URL", "http://127.0.0.1:26001/"),
    NewCharacterExperienceEnabled = newCharacterExperienceMode == "tutorial",
    NewCharacterExperienceMode = newCharacterExperienceMode,
};
var accountOptions = new AccountOptions
{
    SkipPasswordCheck = Flag("EVE_DEV_SKIP_PASSWORD", false),
    AutoCreate = Flag("EVE_DEV_AUTO_CREATE", false),
};
IAccountDirectory accounts;
IReadOnlyList<IService> characterServices = [];
Eve.Services.Core.GameRpcCallbacks? gameRpcCallbacks = null;
Eve.Services.Core.KeeperRpcCallbacks? keeperRpcCallbacks = null;
Eve.Calendar.ICalendarStore? calendarStore = null;
Eve.Calendar.ICalendarAudienceReader? calendarAudience = null;
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
    await mailCache.LoadAsync("notifications");
    await mailCache.LoadAsync("crimewatchRuntime");
    await mailCache.LoadAsync("characters");
    await mailCache.LoadAsync("sovereignty");
    await mailCache.LoadAsync("characterEnergyState");
    await mailCache.LoadAsync("shipCosmetics");
    await mailCache.LoadAsync("shipLogoFittings");
    await mailCache.LoadAsync("planetRuntimeState");
    await mailCache.LoadAsync("industryRuntime");
    await mailCache.LoadAsync("moonExtractions");
    await mailCache.LoadAsync("overviewSharedPresets");
      await mailCache.LoadAsync("sharedSettings");
    (characterServices, space, simulation, crimewatch, calendarStore, calendarAudience, gameRpcCallbacks, keeperRpcCallbacks) = await CharacterServicesAsync(source, directory, online, clock, foundations, mailStore, mailStoreOwner, mailCache, options.NewCharacterExperienceMode);
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
ReadJumpTimersCallback? readJumpTimers = mailCache is { } characterCache
    ? (characterId, cancellationToken) =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = $"/{characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        var character = characterCache.Read("characters", path).Data as JsonObject;
        return ValueTask.FromResult(PersistenceCharacterRpcReadModel.ReadJumpTimers(character));
    }
    : null;
ReadNonDiminishingInjectionCountCallback? readNonDiminishingInjections = mailStore is { } characterStore
    ? async (characterId, cancellationToken) =>
    {
        var state = await PersistenceReadHelpers.ReadOptionalTableAsync(
            characterStore, "skillTradingState", cancellationToken);
        return PersistenceCharacterRpcReadModel.ReadNonDiminishingInjections(state, characterId);
    }
    : null;
// superWeaponMgr: the activations go to the space host's modules (none is wired without the space simulation, and then the route says so)
var superWeapons = space is null ? null : new SuperWeaponService(space);
var superWeaponCallbacks = new SuperWeaponRpcCallbacks(
    ActivatePointTargetedAsync: (request, cancellationToken) => (superWeapons ?? throw SuperWeaponMgrService.NotWired())
        .ActivatePointTargetedAsync(request.Session, request.ModuleId, request.EffectId, request.Destination, cancellationToken),
    ActivateSlashAsync: (request, cancellationToken) => (superWeapons ?? throw SuperWeaponMgrService.NotWired())
        .ActivateSlashAsync(request.Session, request.ModuleId, request.StartPoint, request.EndPoint, cancellationToken));
foreach (var service in CoreServices.Create(
    new SubscriptionSettings(), new NoOmegaAccounts(), now, calendarStore, calendarAudience,
    gameRpcCallbacks: gameRpcCallbacks,
    readJumpTimers: readJumpTimers,
    readNonDiminishingInjections: readNonDiminishingInjections,
    keeperRpcCallbacks: keeperRpcCallbacks,
    superWeaponRpcCallbacks: superWeaponCallbacks))
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
dispatcher.OnProblem = (message, error) =>
{
    Console.Error.WriteLine($"eve-host: {message}{(error is null ? "" : ": " + error.Message)}");
    if (error is not null)
        SentrySdk.CaptureException(error);
};
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
static async Task<(IReadOnlyList<IService> Services, SpaceSessions? Space, SimClient? Simulation, Eve.Services.Crimewatch.CrimewatchRuntime? Crimewatch, Eve.Calendar.ICalendarStore? CalendarStore, Eve.Calendar.ICalendarAudienceReader? CalendarAudience, Eve.Services.Core.GameRpcCallbacks? GameRpcCallbacks, Eve.Services.Core.KeeperRpcCallbacks? KeeperRpcCallbacks)> CharacterServicesAsync(NpgsqlDataSource source, IAccountLookup accounts, OnlineCharacters online, Func<long> now, Foundations foundations, PersistenceStore mailStore, OwnerSession mailStoreOwner, GameStoreCache mailCache, string newCharacterExperienceMode)
{
    CharacterNames.EnsureUnicodeSupport();
    var characterSchema = Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema);
    var inventorySchema = Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema);
    var skillsSchema = Env("EVE_SKILLS_SCHEMA", PgSkillStore.DefaultSchema);
    var walletSchema = Env("EVE_WALLET_SCHEMA", PgWalletStore.DefaultSchema);
    var fittingSchema = Env("EVE_FITTING_SCHEMA", PgModuleStateStore.DefaultSchema);
    await PgCharacterStore.MigrateAsync(source, characterSchema);
    await PgItemStore.MigrateAsync(source, inventorySchema);
    var industrySchema = Env("EVE_INDUSTRY_SCHEMA", "industry");
    if (!System.Text.RegularExpressions.Regex.IsMatch(industrySchema, "^[a-z_][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        throw new InvalidOperationException("EVE_INDUSTRY_SCHEMA is not a valid PostgreSQL schema name.");
    await Eve.Data.Migrations.ApplyAsync(source, industrySchema,
    [
        new Eve.Data.Migration("001_jobs", $$"""
            CREATE TABLE IF NOT EXISTS jobs (
                job_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                install_operation_id text NOT NULL UNIQUE,
                install_request_fingerprint text NOT NULL,
                activity_id integer NOT NULL,
                blueprint_item_id bigint NOT NULL REFERENCES "{{inventorySchema}}".items(id) ON DELETE RESTRICT,
                blueprint_type_id integer NOT NULL,
                blueprint_copy boolean NOT NULL,
                blueprint_location_id bigint NOT NULL,
                blueprint_location_flag_id integer NOT NULL,
                facility_id bigint NOT NULL,
                owner_id bigint NOT NULL,
                installer_id bigint NOT NULL,
                completed_character_id bigint NOT NULL DEFAULT 0,
                solar_system_id bigint NOT NULL,
                station_id bigint NOT NULL DEFAULT 0,
                input_location_id bigint NOT NULL,
                input_flag_id integer NOT NULL,
                output_location_id bigint NOT NULL,
                output_flag_id integer NOT NULL,
                status integer NOT NULL,
                start_date_filetime bigint NOT NULL,
                end_date_filetime bigint NOT NULL,
                pause_date_filetime bigint NULL,
                runs integer NOT NULL CHECK (runs > 0),
                licensed_runs integer NOT NULL DEFAULT 0 CHECK (licensed_runs >= 0),
                successful_runs bigint NOT NULL DEFAULT 0,
                cost_isk bigint NOT NULL CHECK (cost_isk >= 0),
                time_in_seconds integer NOT NULL CHECK (time_in_seconds >= 0),
                probability double precision NOT NULL DEFAULT 1,
                product_type_id integer NULL,
                optional_type_id integer NULL,
                optional_type_id2 integer NULL,
                payment_owner_id bigint NOT NULL,
                payment_account_key integer NOT NULL CHECK (payment_account_key BETWEEN 1000 AND 1006),
                activity_snapshot jsonb NOT NULL,
                result_snapshot jsonb NULL,
                output_item_ids bigint[] NOT NULL DEFAULT '{}',
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                completed_at timestamptz NULL,
                CHECK (payment_owner_id = owner_id OR payment_owner_id = installer_id)
            );
            CREATE INDEX IF NOT EXISTS industry_jobs_owner_history ON jobs (owner_id, job_id);
            CREATE INDEX IF NOT EXISTS industry_jobs_installer_active ON jobs (installer_id, status, job_id);
            CREATE INDEX IF NOT EXISTS industry_jobs_facility_active ON jobs (facility_id, status, end_date_filetime);
            """)
    ]);
    await PgSkillStore.MigrateAsync(source, skillsSchema);
    await PgWalletStore.MigrateAsync(source, walletSchema);
    var npcSchema = Env("EVE_NPC_SCHEMA", PgBountyStore.DefaultSchema);
    await PgBountyStore.MigrateAsync(source, npcSchema);
    await PgModuleStateStore.MigrateAsync(source, fittingSchema);
    var spaceSchema = Env("EVE_SPACE_SCHEMA", PgSpaceItemStore.DefaultSchema);
    await PgSpaceItemStore.MigrateAsync(source, spaceSchema);
        // the mining ledger's queries over the space schema (the table is migration 0003 of the ships' migrations, applied just above)
        var miningLedgerReads = Eve.Services.Core.MiningLedgerQueries.Register(
            new Eve.ReadSide.ReadSide(source, new Dictionary<string, string> { [Eve.Services.Core.MiningLedgerQueries.SpaceSchema] = spaceSchema }));
    var assetSafetySchema = Env("EVE_ASSET_SAFETY_SCHEMA", PersistenceStructureAssetSafetyReadModel.DefaultSchema);
    await PersistenceStructureAssetSafetyReadModel.MigrateAsync(source, assetSafetySchema);
    var crimewatchSchema = Env("EVE_CRIMEWATCH_SCHEMA", Eve.Crimewatch.PgCrimewatchStore.DefaultSchema);
    await Eve.Crimewatch.PgCrimewatchStore.MigrateAsync(source, crimewatchSchema);
    var crimewatchStore = new Eve.Crimewatch.PgCrimewatchStore(source, crimewatchSchema);

    var sde = new SdeReader(source);
    if (await sde.BuildAsync() is not { } build)
    {
        Console.Error.WriteLine("eve-host: the static data is not loaded (run `eve-sde ensure --pg $EVE_PG_URL`): no charUnboundMgr, no skills");
        return ([], null, null, null, null, null, null, null);
    }
    var startText = Env("EVE_START_STATION", "60015249");
    long? startStation = startText == "school" ? null : long.Parse(startText, System.Globalization.CultureInfo.InvariantCulture);
    var catalog = await CreationCatalog.LoadAsync(sde, startStation);
    var characters = new PgCharacterStore(source, characterSchema);
    var items = new PgItemStore(source, inventorySchema);
    var spaceItemStore = new PgSpaceItemStore(source, spaceSchema);
    var itemTypes = new ItemTypeCatalog(sde);
    var skills = new PgSkillStore(source, skillsSchema);
    var skillCatalog = await SkillCatalog.LoadAsync(sde);
    var wallets = new PgWalletStore(source, walletSchema);
    var modules = new PgModuleStateStore(source, fittingSchema);
    var stations = await StationCatalog.LoadAsync(sde);
    var solarSystems = await SolarSystemCatalog.LoadAsync(sde);
    var dogma = await DogmaStatic.LoadAsync(sde);
    // the abyssal mutation authority (ADR 0016): the mutaplasmid catalog of the static data, the item store and its provenance, the event bus
    var itemMutations = new Eve.Inventory.Mutation.ItemMutationService(
        source, items, items.Mutations, await Eve.Inventory.Mutation.MutatorCatalog.LoadAsync(sde), dogma, new BusItemMutationEvents(foundations.Events), now);
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
    var stranded = await ships.RecoverAllFromSpaceAsync(
        id => stations.Get(id) is { } station ? (station.SolarSystemId, station.ConstellationId, station.RegionId) : null,
        shouldRecover: async (characterId, cancellationToken) =>
        {
            var character = await characters.GetAsync(characterId, cancellationToken);
            return character?.ShipId is not { } shipId ||
                   await spaceItemStore.GetAsync(shipId, cancellationToken) is null;
        });
    if (stranded > 0)
    {
        Console.Error.WriteLine($"eve-host: {stranded} ship(s) left in space by the last host were put back in their stations");
    }
    var dogmaSync = new CharacterDogmaSync(dogma, ships, now);
        var characterSessions = new CharacterSessions();
        var signalTrackerRegistrationProvider = new Eve.Host.SignalTrackerRegistrationProvider(characterSessions);
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
    static long CharMgrJsonLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    static PyValue CharMgrJsonToPy(JsonNode? node)
    {
        if (node is JsonObject obj)
            return new PyDict(obj.Select(pair => new KeyValuePair<PyValue, PyValue>(
                new PyStr(pair.Key), CharMgrJsonToPy(pair.Value))).ToArray());
        if (node is JsonArray array)
            return new PyList(array.Select(CharMgrJsonToPy).ToArray());
        if (node is not JsonValue value)
            return PyNone.Instance;
        if (value.TryGetValue<bool>(out var boolean))
            return boolean ? PyBool.True : PyBool.False;
        if (value.TryGetValue<int>(out var integer))
            return new PyInt(integer);
        if (value.TryGetValue<long>(out var longValue))
            return new PyLong(longValue);
        if (value.TryGetValue<double>(out var real))
            return Py.Number(real);
        if (value.TryGetValue<decimal>(out var decimalValue))
            return Py.Number((double)decimalValue);
        if (value.TryGetValue<string>(out var text))
            return new PyStr(text);
        return new PyStr(value.ToString());
    }

    static IEnumerable<(string Key, JsonObject Row)> CharMgrRows(JsonNode? source)
    {
        if (source is JsonObject byKey)
        {
            foreach (var (key, value) in byKey)
                if (value is JsonObject row)
                    yield return (key, row);
        }
        else if (source is JsonArray rows)
        {
            for (var index = 0; index < rows.Count; index++)
                if (rows[index] is JsonObject row)
                    yield return (index.ToString(System.Globalization.CultureInfo.InvariantCulture), row);
        }
    }

    static long CharMgrKillmailTime(JsonObject row)
    {
        var value = row["killmailTime"]?.ToString();
        if (long.TryParse(value, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var unixMs))
            return unixMs;
        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var timestamp)
            ? timestamp.ToUnixTimeMilliseconds()
            : 0;
    }

    async ValueTask<IReadOnlyList<PyValue>> ReadRecentShipKillmailPageAsync(
        long characterId, int num, int startIndex, CancellationToken cancellationToken)
    {
        if (characterId <= 0 || num <= 0 || startIndex < 0)
            return [];
        var table = await ReadGameStoreTableAsync("killmails", cancellationToken);
        var records = new Dictionary<long, JsonObject>();
        void Add(JsonNode? source)
        {
            foreach (var (key, row) in CharMgrRows(source))
            {
                var killmailId = CharMgrJsonLong(row["killmailID"]);
                if (killmailId <= 0)
                    killmailId = CharMgrJsonLong(JsonValue.Create(key));
                var victim = CharMgrJsonLong(row["victimCharacterID"]);
                var finalCharacter = CharMgrJsonLong(row["finalCharacterID"]);
                if (killmailId <= 0 || victim != characterId && finalCharacter != characterId)
                    continue;
                records[killmailId] = (JsonObject)row.DeepClone();
            }
        }
        Add(table["pending"]);
        Add(table["records"]);
        var page = records
            .OrderByDescending(entry => CharMgrKillmailTime(entry.Value))
            .ThenByDescending(entry => entry.Key)
            .Skip(startIndex)
            .Take(Math.Min(num, 1000))
            .Select(entry => entry.Value)
            .ToArray();
        return PersistenceCharMgrReadModel.PackKillmailRows(page, CharMgrJsonToPy);
    }

    async ValueTask<string?> ReadCharacterDescriptionAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return null;
        var table = await ReadGameStoreTableAsync("characters", cancellationToken);
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return table[key] is JsonObject row && row["description"] is JsonValue value &&
            value.TryGetValue<string>(out var description) ? description : null;
    }

    async ValueTask WriteCharacterDescriptionAsync(long characterId, string description, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            throw new ArgumentOutOfRangeException(nameof(characterId));
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var write = mailCache.Write("characters", $"/{key}/description", JsonValue.Create(description));
        if (!write.Success)
            throw new InvalidOperationException($"Could not persist character {characterId} description: {write.ErrorMsg}");
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await mailCache.FlushTableAsync("characters", cancellationToken);
            var persisted = await mailStore.LoadTableAsync("characters", createIfMissing: false, cancellationToken);
            if (persisted[key] is JsonObject row && row["description"] is JsonValue value &&
                value.TryGetValue<string>(out var stored) && stored == description)
                return;
            await Task.Delay(10, cancellationToken);
        }
        throw new TimeoutException($"Character {characterId} description did not become durable.");
    }

    async ValueTask<long?> ReadMedal1GraphicIdAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return null;
        var table = await ReadGameStoreTableAsync("characters", cancellationToken);
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (table[key] is not JsonObject row)
            return null;
        var graphic = CharMgrJsonLong(row["medal1GraphicID"]);
        if (graphic <= 0)
            graphic = CharMgrJsonLong(row["medal1GraphicId"]);
        return graphic > 0 ? graphic : null;
    }

    async ValueTask<double> ReadCharacterSecurityStatusAsync(long characterId, CancellationToken cancellationToken) =>
        await securityStatusOf(characterId, cancellationToken);

    var charMgrReadModel = new PersistenceCharMgrReadModel(
        ReadGameStoreTableAsync,
        ReadRecentShipKillmailPageAsync,
        ReadCharacterDescriptionAsync,
        WriteCharacterDescriptionAsync,
        ReadMedal1GraphicIdAsync,
        (table, path, data) => mailCache.Write(table, path, data),
        (table, path) => mailCache.Remove(table, path));
    var crimewatchPersistence = new PersistenceCrimewatchCallbacks(
        foundations.Relations, mailStore, mailCache, characterSessions, AffiliationOfAsync, now);
    var crimewatchRuntime = new Eve.Services.Crimewatch.CrimewatchRuntime(
        crimewatchStore, characterSessions, now, crimewatchPersistence,
        message => Console.Error.WriteLine($"eve-host: crimewatch: {message}"),
        localTimers: foundations.LocalTimers);
    var relationWars = new Eve.Services.Crimewatch.RelationWars(foundations.Relations);
    crimewatchRuntime.Legal = relationWars.AreAtWarAsync;
    crimewatchRuntime.WarAssistanceFactsOf = relationWars.AssistanceFactsAsync;
    crimewatchRuntime.SecurityOfSystem = (systemId, _) => Task.FromResult(
        systemId is > 0 and <= int.MaxValue ? solarSystems.Get((int)systemId)?.Security : null);
    crimewatchRuntime.CreateKillRight = (request, token) => crimewatchStore.CreateKillRightAsync(
        request.OwnerCharacterId, request.TargetCharacterId, request.SolarSystemId, request.CreatedAtUnixMs, token);
    var announcer = new TrainingAnnouncer(training, skillCatalog, characterSessions, dogmaSync, now, localTimers: foundations.LocalTimers);
    announcer.Attach();
    var readAugmentations = SkillHandlerService.CreateAugmentationReader(items, dogma, skills, now);
    var skillHandler = new SkillHandlerService(
        skills, skillCatalog, training, characters, items, dogma, announcer, now, readAugmentations,
        isAlphaClone: accountId => !omega.Resolve(new PyInt(accountId)).HasLicense);
    var startingWallet = new StartingWallet(
        decimal.Parse(Env("EVE_START_ISK", "100000"), System.Globalization.CultureInfo.InvariantCulture),
        long.Parse(Env("EVE_START_PLEX", "2222"), System.Globalization.CultureInfo.InvariantCulture));
    Console.Error.WriteLine($"eve-host: characters in PostgreSQL, schemas {characterSchema}, {inventorySchema}, {skillsSchema} and {walletSchema}; {stations.Count} stations; dogma of {dogma.AttributeCount} attributes and {dogma.EffectCount} effects; static data build {build}; new characters start at {(startStation is { } id ? $"station {id}" : "their school's headquarters")}");
    var creation = new CharacterCreation(
        source, characters, items, skills, skillCatalog, wallets, startingWallet, catalog, modules,
        newCharacterExperienceMode == "tutorial", newCharacterExperienceMode);
    // Codex-owned corporation registration; keep combat/NPC construction in its own block below.
    var corporationSchema = Env("EVE_CORPORATIONS_SCHEMA", Eve.Corporations.CorporationStore.DefaultSchema);
    await Eve.Corporations.CorporationStore.MigrateAsync(source, corporationSchema);
    await Eve.Corporations.CorporationOfficeStore.MigrateAsync(source, corporationSchema);
    var economyWallets = new WalletTransactions(source, walletSchema);
    var economyAnnouncer = new WalletAnnouncer(economyWallets, characterSessions);
    var insuranceReadModel = new PersistenceInsuranceReadModel(mailStore);
    var insuranceWriteModel = new PersistenceInsuranceWriteModel(
        source, items, mailStore, mailStoreOwner.Fence, economyWallets, insuranceReadModel, now);
    items.ShipInsuranceInvalidator = insuranceWriteModel;
    var insuranceService = new Eve.Services.Core.InsuranceService(insuranceReadModel, now, insuranceWriteModel);
    var storeRuntime = new PersistenceNewEdenStoreRuntime(
        source, mailStore, mailStoreOwner.Fence, wallets, characters, items, itemTypes,
        characterSessions, now, ct => ReadGameStoreTableAsync("newEdenStore", ct));
    async Task SetCorporationBlueprintLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long corporationId,
        long itemId,
        long typeId,
        long locationId,
        bool locked,
        CancellationToken cancellationToken)
    {
        var item = await items.LockAndGetAsync(connection, transaction, itemId, cancellationToken);
        var type = item is null ? null : await itemTypes.GetAsync(item.TypeId, cancellationToken);
        if (item is null || item.OwnerId != corporationId || item.TypeId != typeId ||
            item.LocationId != locationId || !Eve.Inventory.ItemFlags.IsCorporationHangar(item.Flag) ||
            !item.Singleton || item.BlueprintCopy || type?.GroupId != 9)
            throw new Eve.Corporations.CorporationError("CORPORATION_VOTE_BLUEPRINT_NOT_LOCKABLE");
        await items.SetBlueprintLockedAsync(connection, transaction, corporationId, itemId, locked, cancellationToken);
    }
    var corporations = new Eve.Corporations.CorporationStore(source, characters, wallets, economyWallets, skills, now, corporationSchema,
        setBlueprintLock: SetCorporationBlueprintLockAsync);
    await corporations.SeedNpcAsync(sde);
    foundations.Scheduler.Register("corporation.votes.resolve",
        async (_, token) => await corporations.ResolveAllExpiredVotesAsync(token));
    await foundations.Scheduler.EnsureRecurringAsync(
        "corporation.votes.resolve", "resolve", TimeSpan.FromMinutes(1));
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
        sim = socket.StartsWith("tcp:", StringComparison.Ordinal)
            ? SimClient.ToTcpSocket(socket["tcp:".Length..], message => Console.Error.WriteLine($"eve-host: {message}"))
            : SimClient.ToUnixSocket(socket, message => Console.Error.WriteLine($"eve-host: {message}"));
        sim.Start();
        var npcAuthority = NpcAuthority.Load();
        var securityRecovery = new Eve.Services.Crimewatch.NpcSecurityStatusRecoverySubscriber<Eve.Events.EventContext>(
            profile => npcAuthority.Profile(profile) is { Bounty: > 0 } npc
                ? new Eve.Services.Crimewatch.NpcSecurityStatusProfile(npc.Bounty, npc.CorporationId, npc.FactionId, npc.ShipTypeId)
                : null,
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
                    new Dictionary<string, object?>
                    {
                        ["eventTypeID"] = change.EventTypeID,
                        ["otherOwnerID"] = change.NpcCorporationId,
                        ["otherTypeID"] = change.NpcTypeId,
                        ["locationID"] = change.SolarSystemId,
                        ["modification"] = change.Modification,
                        ["sourceEventID"] = change.SourceEventId,
                        ["npcShipID"] = change.NpcShipId,
                        ["npcFactionID"] = change.NpcFactionId,
                        ["profileID"] = change.ProfileId,
                        ["bounty"] = change.Bounty,
                        ["fastTalkMultiplier"] = change.FastTalkMultiplier,
                    },
                    cancellationToken: token);
                await context.PublishAsync("npc.security_status.recovered",
                    new { characterId = change.CharacterId, status = change.NextStatus },
                    $"character:{change.CharacterId}", token);
            });
        // a mining cycle's ledger entry: written in the event's transaction, so a replayed event writes nothing (the entry id is the event id)
        // the mining ledger is a table of the space schema (MiningLedgerStore), read through the rowset read side; the rows the game store held before are copied once
        // (the copy adds only the entries that are not there, so a second start adds none)
        await using (var ledgerConnection = await source.OpenConnectionAsync())
        await using (var ledgerTransaction = await ledgerConnection.BeginTransactionAsync())
        {
            await Eve.Ships.MiningLedgerStore.ImportAsync(ledgerConnection, ledgerTransaction, spaceSchema,
                (await mailStore.LoadTableAsync("miningLedger")) as System.Text.Json.Nodes.JsonObject ?? new System.Text.Json.Nodes.JsonObject(),
                Eve.Ships.MiningLedgerStore.FileTimeOf(now()));
            await ledgerTransaction.CommitAsync();
        }
        var miningLedger = new MiningLedgerRecorder(spaceSchema);
        foundations.Events.Subscribe("mining.ledger", [Eve.Ships.OreMinedEvent.Topic], (context, token) => miningLedger.RecordAsync(context, token));
        // the daily goals of mining and salvage: content defined before the foundations attach, advanced by one subscriber (DailyGoalRecorder)
        var dailyGoals = new DailyGoalRecorder(foundations.Content, foundations.Scheduler);
        foreach (var definition in DailyGoalRecorder.Definitions)
        {
            foundations.Content.Define(definition);
        }
        foundations.Events.Subscribe(DailyGoalRecorder.SubscriberName, [Eve.Ships.OreMinedEvent.Topic, Eve.Ships.SalvageWreckedEvent.Topic], (context, token) => dailyGoals.RecordAsync(context, token));
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
        spaceSessions = new SpaceSessions(sim, ships, dogma, stations, solarSystems, now, message => Console.Error.WriteLine($"eve-host: space: {message}"), localTimers: foundations.LocalTimers, spaceItems: spaceItemStore) { RookieShipOfRace = race => catalog.Race(race)?.ShipTypeId, Npcs = new NpcSelector(npcAuthority), NpcWrecks = npcWrecks, BeltRats = new BeltRats(npcAuthority, BeltRatConfig.FromEnvironment(Environment.GetEnvironmentVariable), type => NpcHull.HasHitPoints(dogma.Type(type))) };
        npcWrecks.Warm();
        // the periodic work of the space features is jobs of the shared scheduler, not timers of their own
        spaceSessions.UseBounties(new PgBountyStore(source, new WalletTransactions(source, walletSchema), npcSchema, message => Console.Error.WriteLine($"eve-host: bounty: {message}")));
        spaceSessions.Events = foundations.Events;   // what happens in space is announced on the bus as well
        ships.TransitionEventWriter = spaceSessions.Transitions.WriteEventAsync;
        spaceSessions.SafetyLevelOf = (character, token) => crimewatchStore.SafetyOfAsync(character, token);   // the ship safety the pilot has set
        spaceSessions.Aggression = async (request, token) => await crimewatchRuntime.RecordAggressionAsync(request, token);   // what an attack makes of the attacker
        spaceSessions.SecurityStatusOf = securityStatusOf;   // the ship in space shows it
        spaceSessions.CrimewatchStateOf = crimewatchRuntime.StateOfAsync;
        spaceSessions.CriminalEnteredSystem = crimewatchRuntime.RespondToCriminalEntryAsync;
        spaceSessions.LifecycleBlockers = async (action, session, _, token) =>
        {
            if (session.CharacterId is not { } characterId || characterId <= 0)
                return Array.Empty<string>();
            var state = await crimewatchRuntime.StateOfAsync(characterId, token);
            var currentTime = now();
            var blockers = new List<string>();
            if (state.HasWeaponsTimer(currentTime))
                blockers.Add("weapon_timer");
            if (action == Eve.Services.Space.ShipLifecycleAction.SafeLogoff &&
                (state.PvpUntilMs > currentTime || state.NpcUntilMs > currentTime))
                blockers.Add("combat_timer");
            if ((action is Eve.Services.Space.ShipLifecycleAction.Board or Eve.Services.Space.ShipLifecycleAction.Eject) &&
                state.IsCriminal(currentTime) && state.SolarSystemId is > 0 and <= int.MaxValue &&
                solarSystems.Get((int)state.SolarSystemId)?.Security >= 0.5)
                blockers.Add("concord_pursuit");
            return blockers;
        };
        spaceSessions.InvalidateInsuranceForBoarding = async (boarded, token) =>
        {
            await insuranceService.InvalidateShipAsync(new Eve.Services.Core.InsuranceInvalidationRequest(
                boarded.ShipId, Eve.Services.Core.InsuranceInvalidationReason.Boarded,
                boarded.BoardedAtFileTime, boarded.ActorCharacterId,
                boarded.ActorCorporationId > 0 ? boarded.ActorCorporationId : null), token);
        };
        spaceSessions.ShipLost += loss =>
        {
            if (loss.ShipTypeId == Eve.Ships.CharacterShips.CapsuleTypeId)
                return;
            try
            {
                insuranceService.SettleShipLossAsync(new Eve.Services.Core.InsuranceLossSettlementRequest(
                    loss.ShipId, loss.ShipId,
                    checked(116444736000000000L + now() * 10_000L),
                    loss.Cause == Eve.Services.Space.ShipLossCause.Concord,
                    loss.CharacterId, 0), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"eve-host: insurance loss {loss.ShipId} could not be settled: {error}");
            }
        };
        crimewatchRuntime.ConcordResponseDue += async request =>
        {
            if (spaceSessions is not null)
                await spaceSessions.RespondToCriminalAsync(
                    request.AttackerCharacterId, request.SolarSystemId, request.AttackerShipId).ConfigureAwait(false);
        };
        crimewatchRuntime.Aggressed += result =>
        {
            if (characterSessions.Of(result.Request.Attacker) is { } activeSession)
                spaceSessions.CancelSafeLogoff(activeSession);
            return Task.CompletedTask;
        };
        var weaponSafetyReadModel = new PersistenceWeaponTargetContextReadModel(
            AffiliationOfAsync,
            async (attacker, target, at, token) => crimewatchRuntime.Legal is { } legal && await legal(attacker, target, at, token),
            crimewatchRuntime.StateOfAsync,
            securityStatusOf,
            ReadGameStoreTableAsync,
            now);
        spaceSessions.WeaponTargetContextOf = weaponSafetyReadModel.ReadAsync;
        crimewatchRuntime.AttackPolicyFactsOf = async (request, _, token) =>
        {
            var facts = Eve.Services.Space.WeaponSafety.PolicyFactsOf(
                await weaponSafetyReadModel.ReadAsync(request.Attacker, request.Target, token));
            return facts with { TargetIsCapsule = request.TargetIsCapsule };
        };
        spaceSessions.RemoteAssistancePolicyOf = crimewatchRuntime.EvaluateRemoteAssistanceAsync;
        spaceSessions.RecordRemoteAssistance = crimewatchRuntime.RecordRemoteAssistanceAsync;
        // a crime costs security status: written to the relation, told to the pilot, shown on the ship in space
        new Eve.Services.Crimewatch.SecurityStatusPenalties(
            crimewatchRuntime,
            characterSessions,
            securityStatusOf,
            async (request, token) =>
                (await items.GetAsync(request.TargetShipId, token))?.TypeId,
            async (change, token) =>
            {
                await using var connection = await source.OpenConnectionAsync(token);
                await using var transaction = await connection.BeginTransactionAsync(token);
                var attrs = new Dictionary<string, object?>
                {
                    ["eventTypeID"] = change.EventTypeID,
                    ["otherOwnerID"] = change.OtherOwnerID,
                    ["otherTypeID"] = change.OtherTypeID,
                    ["locationID"] = change.LocationID,
                    ["modification"] = change.Modification,
                    ["targetShipItemID"] = change.TargetShipItemId,
                };
                await foundations.Relations.SetAsync(
                    connection, transaction,
                    Eve.Relations.RelationKinds.SecurityStatus.Name,
                    change.CharacterId, Eve.Relations.RelationStore.World, change.NextStatus,
                    change.Reason, attrs, cancellationToken: token);
                await transaction.CommitAsync(token);
            },
            now,
            async (character, token) => await spaceSessions.AffiliationChangedAsync(character, token)).Attach();
        var bounties = spaceSessions;
        foundations.Scheduler.Register("space.bounty.pay", async (_, token) => await bounties.PayBountiesAsync(token));
        await foundations.Scheduler.EnsureRecurringAsync("space.bounty.pay", "pay", TimeSpan.FromSeconds(10));
        spaceContainers = new SpaceContainers(sim, ships, items, new PgSpaceItemStore(source, spaceSchema), new PgWorkCycleLedger(source, spaceSchema), new Eve.Services.Space.EventBusFacts(foundations.Events), dogma, spaceSessions, now, message => Console.Error.WriteLine($"eve-host: cans: {message}"));
        var containers = spaceContainers;
        spaceDrones = new SpaceDrones(sim, ships, dogma, spaceSessions, now, message => Console.Error.WriteLine($"eve-host: {message}"));
        foundations.Scheduler.Register("space.containers.sweep", async (_, _) => await containers.SweepAsync());
        await foundations.Scheduler.EnsureRecurringAsync("space.containers.sweep", "sweep", TimeSpan.FromSeconds(30));
        Console.Error.WriteLine($"eve-host: the space simulation is expected at {Env("EVE_SIM_SOCKET", "/tmp/eve-sim.sock")}");
    }
    await crimewatchRuntime.ResumeAsync();
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
        wakeAllianceApplicationScheduler: allianceDue.Wake,
        relations: foundations.Relations);

    var npcCharacterRows = await sde.ReadAsync("npcCharacters", cancellationToken: CancellationToken.None);
    var agentsInSpaceById = (await sde.ReadAsync("agentsInSpace", cancellationToken: CancellationToken.None))
        .ToDictionary(row => row.Key);
    var agentLocationById = new Dictionary<long, long>();
    foreach (var row in npcCharacterRows)
    {
        if (row.Key > 0 &&
            row.Doc.TryGetProperty("locationID", out var location) &&
            location.TryGetInt64(out var locationId) &&
            locationId > 0)
        {
            agentLocationById[row.Key] = locationId;
        }
    }

    static long AgentSdeInteger(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var result)
            ? result
            : 0;
    static long? AgentSdeOptionalInteger(JsonElement document, string name) =>
        AgentSdeInteger(document, name) is > 0 and var value ? value : null;
    static string AgentSdeText(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    static bool AgentSdeBoolean(JsonElement document, string name) =>
        document.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    var agentMissionAgentRows = npcCharacterRows
        .Where(row => row.Key > 0 &&
            row.Doc.TryGetProperty("agent", out var agent) &&
            agent.ValueKind == JsonValueKind.Object)
        .Select(row =>
        {
            var agent = row.Doc.GetProperty("agent");
            var locationId = AgentSdeInteger(row.Doc, "locationID");
            var station = locationId > 0 ? stations.Get(locationId) : null;
            JsonElement? inSpace = agentsInSpaceById.TryGetValue(row.Key, out var inSpaceRow)
                ? inSpaceRow.Doc
                : null;
            var inSpaceSolarSystemId = inSpace is { } inSpaceDoc
                ? AgentSdeInteger(inSpaceDoc, "solarSystemID")
                : 0;
            var isInSpace = inSpaceSolarSystemId is > 0 and <= int.MaxValue &&
                solarSystems.Get((int)inSpaceSolarSystemId) is not null;
            long? solarSystemId = isInSpace
                ? inSpaceSolarSystemId
                : station?.SolarSystemId;
            if (solarSystemId is null &&
                locationId is > 0 and <= int.MaxValue &&
                solarSystems.Get((int)locationId) is not null)
            {
                solarSystemId = locationId;
            }

            return new AgentMissionAgentData(
                row.Key,
                AgentSdeInteger(agent, "agentTypeID"),
                AgentSdeInteger(agent, "divisionID"),
                AgentSdeInteger(agent, "level") is > 0 and var level ? level : 1,
                isInSpace ? null : AgentSdeOptionalInteger(row.Doc, "locationID"),
                AgentSdeInteger(row.Doc, "corporationID"),
                AgentSdeBoolean(agent, "isLocator"),
                isInSpace,
                AgentSdeOptionalInteger(row.Doc, "careerID"),
                AgentSdeBoolean(row.Doc, "importantMission") || AgentSdeBoolean(agent, "importantMission"),
                AgentSdeText(row.Doc, "missionKind") is { Length: > 0 } missionKind
                    ? missionKind
                    : AgentSdeText(agent, "missionKind"),
                AgentSdeText(row.Doc, "missionTypeLabel") is { Length: > 0 } missionType
                    ? missionType
                    : AgentSdeText(agent, "missionTypeLabel"),
                AgentSdeOptionalInteger(row.Doc, "specialityID"),
                station?.TypeId > 0 ? station.TypeId : AgentSdeOptionalInteger(row.Doc, "stationTypeID"),
                AgentSdeOptionalInteger(row.Doc, "bloodlineID"),
                solarSystemId,
                isInSpace && inSpace is { } spaceDungeon ? AgentSdeOptionalInteger(spaceDungeon, "dungeonID") : null,
                isInSpace && inSpace is { } spaceSpawn ? AgentSdeOptionalInteger(spaceSpawn, "spawnPointID") : null,
                isInSpace && inSpace is { } spaceType ? AgentSdeOptionalInteger(spaceType, "typeID") : null);
        })
        .OrderBy(agent => agent.AgentId)
        .ToArray();
    var structureDirectoryReadModel = new PersistenceStructureDirectoryReadModel(ReadGameStoreTableAsync, AffiliationOfAsync, sde, mailCache);
    if (spaceSessions is not null)
        spaceSessions.StructureExistsAsync = async (structureId, cancellationToken) =>
            await structureDirectoryReadModel.GetStructureInfoAsync(structureId, cancellationToken) is { PersistentlyDeployed: true };
    var structureProfilesRpcBackend = new Eve.Host.PersistenceStructureProfilesRpcBackend(
        ReadGameStoreTableAsync, mailCache, characterSessions);
    var structureRpcCallbacks = new Eve.Services.Core.StructureRpcCallbacks
    {
        GetProfilesAsync = structureProfilesRpcBackend.GetProfilesAsync,
        GetProfileSettingsAsync = structureProfilesRpcBackend.GetProfileSettingsAsync,
        GetMyHangarViewStateAsync = (call, structureId) =>
            structureProfilesRpcBackend.GetMyHangarViewStateAsync(structureId, call.Cancellation),
        CreateProfileAsync = (call, corporationId, name, description) =>
            structureProfilesRpcBackend.CreateProfileAsync(corporationId, name, description, call.Cancellation),
        UpdateProfileAsync = (call, corporationId, profileId, name, description) =>
            structureProfilesRpcBackend.UpdateProfileAsync(corporationId, profileId, name, description, call.Cancellation),
        SaveProfileSettingsAsync = (call, corporationId, profileId, settings) =>
            structureProfilesRpcBackend.SaveProfileSettingsAsync(corporationId, profileId, settings, call.Cancellation),
        SetDefaultProfileAsync = (call, corporationId, profileId) =>
            structureProfilesRpcBackend.SetDefaultProfileAsync(corporationId, profileId, call.Cancellation),
        ChangeProfilesAsync = (call, corporationId, structureIds, profileId) =>
            structureProfilesRpcBackend.ChangeProfilesAsync(corporationId, structureIds, profileId, call.Cancellation),
        DeleteProfileAsync = (call, corporationId, profileId) =>
            structureProfilesRpcBackend.DeleteProfileAsync(corporationId, profileId, call.Cancellation),
        DuplicateProfileAsync = (call, corporationId, profileId) =>
            structureProfilesRpcBackend.DuplicateProfileAsync(corporationId, profileId, call.Cancellation),
    };
    var structureAssetSafetyReadModel = new PersistenceStructureAssetSafetyReadModel(
        source, items, new PgSpaceItemStore(source, spaceSchema), economyWallets, sde,
        ReadGameStoreTableAsync, AffiliationOfAsync, now, inventorySchema, corporationSchema,
        assetSafetySchema, structureDirectoryReadModel,
        message => Console.Error.WriteLine($"eve-host: asset safety: {message}"));
    foundations.Scheduler.Register(
        PersistenceStructureAssetSafetyReadModel.DueJobKind,
        (_, cancellation) => structureAssetSafetyReadModel.ProcessDueAutomaticDeliveriesAsync(cancellation).AsTask());
    await foundations.Scheduler.EnsureRecurringAsync(
        PersistenceStructureAssetSafetyReadModel.DueJobKind,
        "host-asset-safety",
        System.TimeSpan.FromMinutes(5));
    var structureSettingsService = new Eve.Services.Core.StructureSettingsService(structureDirectoryReadModel);
    var sovereigntyReadModel = new PersistenceSovFwReadModel(ReadGameStoreTableAsync, AffiliationOfAsync);
    async Task<IReadOnlyList<Eve.Services.Core.SkyhookResourceSnapshot>> ReadPersistedSkyhooksAsync(CancellationToken cancellationToken)
    {
        var state = await mailStore.LoadTableAsync("sovereignty", createIfMissing: false, cancellationToken);
        var rows = state["skyhooks"] as JsonObject;
        if (rows is null)
            return Array.Empty<Eve.Services.Core.SkyhookResourceSnapshot>();

        static long Number(JsonNode? value, long fallback = 0) =>
            long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : fallback;
        static bool Flag(JsonNode? value) =>
            bool.TryParse(value?.ToString(), out var flag) ? flag : Number(value) != 0;

        var result = new List<Eve.Services.Core.SkyhookResourceSnapshot>();
        foreach (var (key, node) in rows)
        {
            if (node is not JsonObject row)
                continue;
            var skyhookId = Number(row["skyhookID"], Number(row["itemID"],
                long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var keyedId) ? keyedId : 0));
            var typeId = Number(row["typeID"]);
            var systemId = Number(row["solarSystemID"]);
            var planetId = Number(row["planetID"]);
            var corporationId = Number(row["corporationID"], Number(row["ownerCorpID"]));
            if (skyhookId <= 0 || typeId != 81080 || systemId <= 0 || planetId <= 0 || corporationId <= 0 ||
                Flag(row["destroyed"]))
                continue;

            var reagents = (row["reagents"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(reagent => new Eve.Services.Core.SkyhookReagentResource(
                    Number(reagent["typeID"]),
                    Math.Max(0, Number(reagent["secureStock"])),
                    Math.Max(0, Number(reagent["insecureStock"]))))
                .Where(reagent => reagent.TypeId > 0)
                .ToArray();
            Eve.Services.Core.SkyhookTheftVulnerability? vulnerability = null;
            if (row["theftVulnerability"] is JsonObject theft)
            {
                vulnerability = new Eve.Services.Core.SkyhookTheftVulnerability(
                    Flag(theft["vulnerable"]),
                    Number(theft["startUnixSeconds"]),
                    Number(theft["endUnixSeconds"]));
            }
            int? skyhookState = int.TryParse(row["state"]?.ToString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedState)
                ? parsedState : null;
            var allianceId = Number(row["allianceID"]);

            result.Add(new Eve.Services.Core.SkyhookResourceSnapshot(
                skyhookId,
                typeId,
                systemId,
                planetId,
                corporationId,
                allianceId > 0 ? allianceId : null,
                Flag(row["productionActive"]),
                row["workforce"] is null ? null : Number(row["workforce"]),
                row["resourceVersion"]?.ToString() ?? "",
                reagents,
                vulnerability,
                skyhookState));
        }
        return result;
    }

    async ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?> ReadSkyhookAsync(long skyhookId, CancellationToken cancellationToken) =>
        (await ReadPersistedSkyhooksAsync(cancellationToken)).FirstOrDefault(skyhook => skyhook.SkyhookId == skyhookId);

    async ValueTask<IReadOnlyList<Eve.Services.Core.SkyhookResourceSnapshot>> ReadCorporationSkyhooksAsync(
        long corporationId, CancellationToken cancellationToken) =>
        (await ReadPersistedSkyhooksAsync(cancellationToken))
            .Where(skyhook => skyhook.CorporationId == corporationId)
            .ToArray();

    async ValueTask<bool> AuthorizeSkyhookAsync(
        Eve.Services.ServiceCall call,
        Eve.Services.Core.SkyhookResourceSnapshot skyhook,
        Eve.Services.Core.SkyhookResourcePermission permission,
        CancellationToken cancellationToken)
    {
        if (call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            (call.Session.GetInt("corpid") ?? call.Session.GetInt("corporationID")) != skyhook.CorporationId)
            return false;

        var affiliation = await AffiliationOfAsync(characterId, cancellationToken);
        if (affiliation.Corporation != skyhook.CorporationId ||
            await corporations.MemberAsync(characterId, cancellationToken) is not { } member ||
            member.CorporationId != skyhook.CorporationId)
            return false;

        if (permission == Eve.Services.Core.SkyhookResourcePermission.Read)
            return true;
        if (permission is not (Eve.Services.Core.SkyhookResourcePermission.Activate or Eve.Services.Core.SkyhookResourcePermission.Deactivate))
            return false;

        var corporation = await corporations.GetAsync(skyhook.CorporationId, cancellationToken);
        if (corporation is null || corporation.Deleted)
            return false;
        return corporation.CeoId == characterId && !corporation.CeoRolesSuspended ||
            (member.Masks.Roles & Eve.Corporations.CorporationRoles.Director) != 0;
    }

    var sovereigntyWriteGate = new SemaphoreSlim(1, 1);
    async ValueTask<bool> CanManageSovHubAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub)
    {
        if (solarSystemId <= 0 || hub.HubId is not > 0 || hub.CorporationId is not { } corporationId ||
            call.Session.CharacterId is not { } characterId || characterId <= 0 ||
            (call.Session.GetInt("corpid") ?? call.Session.GetInt("corporationID")) != corporationId)
            return false;

        var affiliation = await AffiliationOfAsync(characterId, call.Cancellation);
        if (affiliation.Corporation != corporationId ||
            await corporations.MemberAsync(characterId, call.Cancellation) is not { } member ||
            member.CorporationId != corporationId)
            return false;

        var corporation = await corporations.GetAsync(corporationId, call.Cancellation);
        return corporation is { Deleted: false } &&
            (corporation.CeoId == characterId && !corporation.CeoRolesSuspended ||
                (member.Masks.Roles & Eve.Corporations.CorporationRoles.Director) != 0);
    }

    async ValueTask<bool> ChargeSovSkyhookAcquisitionAsync(
        IReadOnlyList<SovSkyhookAcquisitionCharge> charges,
        CancellationToken cancellationToken)
    {
        if (charges.Count == 0)
            return true;

        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var charge in charges)
        {
            if (charge.CorporationId <= 0 || charge.AmountIsk <= 0 ||
                decimal.Round(charge.AmountIsk, 2) != charge.AmountIsk ||
                charge.ReferenceTypeId <= 0 || string.IsNullOrWhiteSpace(charge.OperationKey))
                return false;

            await economyWallets.AdjustAsync(
                connection,
                transaction,
                charge.CorporationId,
                charge.IsRefund ? charge.AmountIsk : -charge.AmountIsk,
                0,
                charge.ReferenceTypeId,
                charge.IsRefund ? "Skyhook acquisition refund" : "Skyhook acquisition",
                now(),
                charge.OperationKey,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    async ValueTask<bool> HasSovSkyhookWalletOperationAsync(
        SovSkyhookAcquisitionCharge charge,
        CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {walletSchema}.journal WHERE owner_id = $1 AND account_key = 1000 AND operation_key = $2)",
            connection);
        command.Parameters.AddWithValue(charge.CorporationId);
        command.Parameters.AddWithValue(charge.OperationKey);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    var sovWriteOperations = new PersistenceSovFwWriteOperations(
        table => mailCache.Read(table).Data as JsonObject ?? new JsonObject(),
        (table, path, data) => mailCache.Write(table, path, data),
        (table, token) => mailCache.FlushTableAsync(table, token),
        (table, token) => mailStore.LoadTableAsync(table, createIfMissing: false, token),
        CanManageSovHubAsync,
        sovereigntyWriteGate,
        ChargeSovSkyhookAcquisitionAsync,
        HasSovSkyhookWalletOperationAsync);
    await sovWriteOperations.ReplayPendingSkyhookAcquisitionsAsync(CancellationToken.None);
    foundations.Scheduler.Register(
        "sovereignty.skyhook-acquisition.reconcile",
        async (_, token) => await sovWriteOperations.ReplayPendingSkyhookAcquisitionsAsync(token));
    await foundations.Scheduler.EnsureRecurringAsync(
        "sovereignty.skyhook-acquisition.reconcile", "pending", TimeSpan.FromMinutes(1));
    var skyhookActivationWriter = new PersistenceSkyhookActivationWriter(
        table => mailCache.Read(table).Data as JsonObject ?? new JsonObject(),
        (table, path, data) => mailCache.Write(table, path, data),
        (table, token) => mailCache.FlushTableAsync(table, token),
        ReadSkyhookAsync,
        AuthorizeSkyhookAsync,
        sovereigntyWriteGate);
    var skyhookResourceService = new Eve.Services.Core.SkyhookResourceService(
        ReadSkyhookAsync,
        ReadCorporationSkyhooksAsync,
        AuthorizeSkyhookAsync,
        skyhookActivationWriter.PersistAsync);
    var skyhookResourceMgrService = new Eve.Services.Core.SkyhookResourceMgrService(skyhookResourceService);

    var fleetSchema = Env("EVE_FLEET_SCHEMA", Eve.Services.Space.FleetStore.DefaultSchema);
    await Eve.Data.Migrations.ApplyAsync(source, fleetSchema,
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
        Eve.Services.Space.FleetStore.FleetFinderAdvertPayloadMigration
    ], CancellationToken.None);
    var fleetStore = new Eve.Services.Space.FleetStore(source, fleetSchema);
    async Task<Eve.Services.Space.FleetAdvertLeaderProfile?> FleetAdvertLeaderProfileForAsync(
        long characterId, CancellationToken cancellationToken)
    {
        var affiliation = await AffiliationOfAsync(characterId, cancellationToken);
        var securityStatus = await securityStatusOf(characterId, cancellationToken);
        return new Eve.Services.Space.FleetAdvertLeaderProfile(
            affiliation.Corporation, affiliation.Alliance, affiliation.Faction, securityStatus);
    }
    Eve.Services.ClientSession? FleetSessionForCharacter(long characterId) =>
        characterSessions.Of(characterId);
    static bool SameFleetBubble(Eve.Services.ClientSession sender, Eve.Services.ClientSession recipient) =>
        sender.GetInt("ballparkid") is { } bubbleId && recipient.GetInt("ballparkid") == bubbleId;
    async Task<PyValue> FleetCompositionForMembersAsync(
        IReadOnlyList<long> characterIds,
        CancellationToken cancellationToken)
    {
        var orderedIds = characterIds.Where(id => id > 0).Distinct().ToArray();
        if (orderedIds.Length == 0)
            return new PyList([]);

        var firstFleetId = await fleetStore.FleetIdForCharacterAsync(orderedIds[0], cancellationToken);
        var fleetSnapshot = firstFleetId is { } fleetId
            ? await fleetStore.SnapshotAsync(fleetId, cancellationToken)
            : null;
        var shipTypes = fleetSnapshot?.Members.ToDictionary(member => member.CharacterId, member => member.ShipTypeId)
            ?? new Dictionary<long, long?>();
        var result = new List<PyValue>(orderedIds.Length);
        foreach (var characterId in orderedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var session = FleetSessionForCharacter(characterId);
            var shipTypeId = session?.GetInt("shiptypeid") is { } liveShipType
                ? (long?)liveShipType
                : shipTypes.GetValueOrDefault(characterId);
            var skillRows = await skills.ForCharacterAsync(characterId, cancellationToken);
            var skillValues = new PyList([.. skillRows.Select(skill => (PyValue)Py.KeyVal(
                ("typeID", new PyInt(skill.TypeId)),
                ("level", new PyInt(skill.Level)),
                ("skillPoints", new PyLong(skill.SkillPoints))))]);
            var skillIds = new PyList([.. skillRows.Select(skill => (PyValue)new PyInt(skill.TypeId))]);
            result.Add(Py.KeyVal(
                ("characterID", new PyLong(characterId)),
                ("solarSystemID", session?.GetInt("solarsystemid2") is { } solarSystemId
                    ? new PyLong(solarSystemId) : PyNone.Instance),
                ("stationID", session?.GetInt("stationid") is { } stationId
                    ? new PyLong(stationId) : PyNone.Instance),
                ("shipTypeID", shipTypeId is { } typeId
                    ? new PyLong(typeId) : PyNone.Instance),
                ("skills", skillValues),
                ("skillIDs", skillIds)));
        }
        return new PyList(result);
    }
    var incursionAnomalyReadModel = new PersistenceIncursionAnomalyReadModel(
        foundations.Content.ActiveOfAsync,
        ReadGameStoreTableAsync,
        now);
    var dungeonReadModel = new Eve.Host.PersistenceDungeonReadModel(
        (table, token) => sde.ReadAsync(table, cancellationToken: token),
        foundations.Content.ActiveByKindAsync,
        ReadGameStoreTableAsync,
        now);
    var keeperRpcCallbacks = new Eve.Services.Core.KeeperRpcCallbacks(
        ReadCurrentDungeonAsync: async (session, cancellationToken) =>
        {
            if (spaceSessions is null || session.CharacterId is not { } characterId)
                return null;
            var shipId = spaceSessions.ShipIdOf(session);
            if (shipId is not > 0)
                return null;
            var position = await spaceSessions.PositionOfAsync(session, shipId.Value, cancellationToken)
                .ConfigureAwait(false);
            var currentSystemId = position?.SolarSystemId ??
                session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
            return await dungeonReadModel.ReadCurrentDungeonAsync(
                characterId, shipId.Value, currentSystemId,
                position?.X, position?.Y, position?.Z,
                spaceSessions.IsWarping(session), cancellationToken).ConfigureAwait(false);
        });
    async Task<PersistenceMailSendRecipients?> ResolveMailSendRecipientsAsync(
        Eve.Services.Core.MailSendRequest request,
        Eve.Services.ClientSession session,
        CancellationToken cancellationToken)
    {
        if (request.SenderCharacterId <= 0 || session.CharacterId != request.SenderCharacterId)
            return null;
        var sender = await characters.GetAsync(request.SenderCharacterId, cancellationToken);
        if (sender is null)
            return null;

        var recipientIds = new List<long>();
        var recipientLabelMasks = new Dictionary<long, int>();
        void AddRecipient(long characterId, int labelMask)
        {
            if (characterId <= 0)
                return;
            if (recipientLabelMasks.TryGetValue(characterId, out var prior))
                recipientLabelMasks[characterId] = prior | labelMask;
            else
            {
                recipientIds.Add(characterId);
                recipientLabelMasks.Add(characterId, labelMask);
            }
        }

        var senderLabelMask = 2;
        foreach (var characterId in request.CharacterRecipientIds.Distinct())
        {
            if (characterId <= 0 || await characters.GetAsync(characterId, cancellationToken) is null)
                return null;
            AddRecipient(characterId, 1);
            if (characterId == request.SenderCharacterId)
                senderLabelMask |= 1;
        }

        if (request.MailingListId is { } mailingListId)
        {
            var listKey = mailingListId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var list = mailCache.Read("mail", $"/mailingLists/{listKey}").Data as JsonObject;
            if (list is null || (list["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var isDeleted) && isDeleted))
                return null;
            var members = list["members"] as JsonObject;
            var ownerId = long.TryParse(list["ownerID"]?.ToString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsedOwner)
                ? parsedOwner : 0;
            var senderRole = 0d;
            if (members?[request.SenderCharacterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is { } storedRole)
                double.TryParse(storedRole.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out senderRole);
            if (ownerId != request.SenderCharacterId && Math.Truncate(senderRole) <= 0)
                return null;
            if (members is not null)
            {
                foreach (var (key, value) in members)
                {
                    if (!long.TryParse(key, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var characterId) || characterId <= 0 ||
                        !double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var role) || Math.Truncate(role) == 0)
                        continue;
                    if (await characters.GetAsync(characterId, cancellationToken) is not null)
                        AddRecipient(characterId, 1);
                }
            }
            if (ownerId > 0 && await characters.GetAsync(ownerId, cancellationToken) is not null)
                AddRecipient(ownerId, 1);
        }

        if (request.CorporationOrAllianceRecipientIds is { Count: > 0 } organizationIds)
        {
            foreach (var organizationId in organizationIds.Distinct())
            {
                if (organizationId <= 0)
                    return null;
                if (await corporations.AllianceAsync(organizationId, cancellationToken) is not null)
                {
                    senderLabelMask |= 8;
                    foreach (var memberCorporation in await corporations.AllianceMembersAsync(organizationId, cancellationToken))
                        foreach (var member in await corporations.MembersAsync(memberCorporation.CorporationId, cancellationToken))
                            AddRecipient(member.CharacterId, 1 | 8);
                }
                else if (await corporations.GetAsync(organizationId, cancellationToken) is not null)
                {
                    senderLabelMask |= 4;
                    foreach (var member in await corporations.MembersAsync(organizationId, cancellationToken))
                        AddRecipient(member.CharacterId, 1 | 4);
                }
                else
                    return null;
            }
        }

        return recipientIds.Count == 0
            ? null
            : new PersistenceMailSendRecipients(recipientIds, sender.Name, senderLabelMask, recipientLabelMasks);
    }

    await Eve.Calendar.CalendarStore.MigrateAsync(source, CancellationToken.None);
    var calendarStore = new Eve.Calendar.CalendarStore(source);
    var calendarAudience = new PersistenceCalendarAudienceReader(
        async (corporationId, ct) =>
            (await corporations.MembersAsync(corporationId, ct)).Select(member => member.CharacterId).ToArray(),
        async (allianceId, ct) =>
            (await corporations.AllianceMembersAsync(allianceId, ct)).Select(member => member.CorporationId).ToArray());

    var mailSendWriter = new PersistenceMailSendHostAdapter(
        table => mailCache.Read(table, null).Data as JsonObject ?? new JsonObject(),
        (table, path, data) => mailCache.Write(table, path, data),
        (table, cancellationToken) => mailCache.FlushTableAsync(table, cancellationToken),
        (table, cancellationToken) => mailStore.LoadTableAsync(table, createIfMissing: false, cancellationToken),
        ResolveMailSendRecipientsAsync,
        now,
        (characterId, args) => characterSessions.Of(characterId)?.SendNotification("OnNotificationReceived", "clientID", args));

    int ReadUnreadMailCount(long characterId)
    {
        var mailState = mailCache.Read("mail", null).Data as JsonObject;
        var messages = mailState?["messages"] as JsonObject;
        var statuses = (mailState?["mailboxes"] as JsonObject)?[
            characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject mailbox
            ? mailbox["statuses"] as JsonObject
            : null;
        if (messages is null || statuses is null)
            return 0;

        var unread = 0;
        const long readMask = 1;
        const long trashedMask = 8;
        foreach (var (messageId, statusNode) in statuses)
        {
            if (!messages.ContainsKey(messageId) || statusNode is not JsonObject status)
                continue;
            var statusMask = long.TryParse(status["statusMask"]?.ToString(),
                System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture,
                out var parsedMask) ? parsedMask : 0;
            if ((statusMask & readMask) == 0 && (statusMask & trashedMask) == 0)
                unread++;
        }
        return unread;
    }

    async Task SendNewCharacterWelcomeMailAsync(long characterId, CancellationToken cancellationToken)
    {
        try
        {
            var character = await characters.GetAsync(characterId, cancellationToken);
            var corporation = character is null
                ? null
                : await corporations.GetAsync(character.CorporationId, cancellationToken);
            if (character is null || corporation is null ||
                await mailSendWriter.SendWelcomeMailAsync(
                    corporation.Id, corporation.Name, character.Id, character.Name, cancellationToken) is not > 0)
                throw new InvalidOperationException("The new character or starter corporation is unavailable.");
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"[CharacterCreation] Welcome mail failed for character {characterId}: {error.Message}");
        }
    }

    var mailingListMutationGate = new SemaphoreSlim(1, 1);
    static long? MailingListPyLong(Eve.Marshal.PyValue? value)
    {
        var number = value switch
        {
            Eve.Marshal.PyInt small => (double)small.Value,
            Eve.Marshal.PyLong large => large.Value,
            Eve.Marshal.PyReal real => real.Value,
            _ => double.NaN,
        };
        return double.IsFinite(number) && number >= long.MinValue && number < (double)long.MaxValue
            ? (long)Math.Truncate(number)
            : null;
    }
    static string? MailingListPyText(Eve.Marshal.PyValue? value) => value switch
    {
        Eve.Marshal.PyStr text => text.Value,
        Eve.Marshal.PyWString text => text.Value,
        _ => null,
    };
    static long MailingListJsonLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    static string MailingListJsonText(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : string.Empty;
    static string MailingListKey(long id) =>
        id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    static bool MailingListDeleted(JsonObject row) =>
        row["deleted"] is JsonValue deleted && deleted.TryGetValue<bool>(out var value) && value;
    static long MailingListRole(JsonObject row, long characterId)
    {
        if (MailingListJsonLong(row["ownerID"]) == characterId)
            return 3;
        var members = row["members"] as JsonObject;
        return MailingListMemberRole(MailingListJsonLong(members?[MailingListKey(characterId)]));
    }
    static long MailingListMemberRole(long value) => value is 0 or 2 or 3 ? value : 1;
    static long MailingListAccess(long value) => value == 0 ? 0 : 1;
    static bool MailingListCanManageTarget(long actorRole, long targetRole) =>
        actorRole is 2 or 3 && targetRole != 3 && !(actorRole == 2 && targetRole == 2);
    static string MailingListTimestamp(long unixMs) =>
        Py.FileTimeFromUnixMs(unixMs).ToString(System.Globalization.CultureInfo.InvariantCulture);
    static string MailingListBoundedText(string? value, int length)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length > length ? text[..length] : text;
    }
    static bool MailingListCanManage(JsonObject row, long characterId) =>
        MailingListRole(row, characterId) is 2 or 3;
    static (string Name, string DisplayName) NormalizeMailingListName(string raw)
    {
        var display = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        var name = new string(display.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return (name, display);
    }

    async Task PersistMailingListRowAsync(
        long listId, JsonObject row, CancellationToken cancellationToken)
    {
        var key = MailingListKey(listId);
        var result = mailCache.Write("mail", $"/mailingLists/{key}", row);
        if (!result.Success)
            throw new InvalidOperationException($"Could not persist mailing list {listId}: {result.ErrorMsg}");

        for (var attempt = 0; attempt < 500; attempt++)
        {
            await mailCache.FlushTableAsync("mail", cancellationToken);
            var persisted = await mailStore.LoadTableAsync("mail", createIfMissing: false, cancellationToken);
            if ((persisted["mailingLists"] as JsonObject)?[key] is JsonObject actual &&
                JsonNode.DeepEquals(actual, row))
                return;
            await Task.Delay(10, cancellationToken);
        }
        throw new TimeoutException($"Mailing list {listId} was not durable after its GameStore flush.");
    }

    Eve.Services.Core.MailingListMutationCallback mutateMailingList =
        async (mutation, cancellationToken) =>
        {
            var caller = mutation.Caller;
            if (caller.CharacterId is not { } characterId || characterId <= 0)
                throw new UnauthorizedAccessException("An authenticated character is required for mailing-list mutations.");
            var session = characterSessions.Of(characterId);
            if (session is null || session.SessionId != caller.SessionId ||
                session.ClientId != caller.ClientId || session.UserId != caller.UserId ||
                session.CharacterId != characterId)
                throw new UnauthorizedAccessException("The mailing-list caller does not match the current character session.");

            await mailingListMutationGate.WaitAsync(cancellationToken);
            try
            {
                var lists = mailCache.Read("mail", "/mailingLists").Data as JsonObject ?? new JsonObject();
                var args = mutation.PositionalArguments;

                if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.Create)
                {
                    if (args.Count < 1 || MailingListPyText(args[0]) is not { } requestedName)
                        return Eve.Marshal.PyNone.Instance;
                    var (name, displayName) = NormalizeMailingListName(requestedName);
                    if (name.Length is 0 or > 64 ||
                        lists.Any(entry => entry.Value is JsonObject existing &&
                            !MailingListDeleted(existing) &&
                            string.Equals(MailingListJsonText(existing["name"]), name, StringComparison.OrdinalIgnoreCase)))
                        return Eve.Marshal.PyNone.Instance;

                    var nextId = Math.Max(500_000_000L, lists
                        .Select(entry => long.TryParse(entry.Key, out var existingId) ? existingId : 0)
                        .DefaultIfEmpty(499_999_999L)
                        .Max() + 1);
                    var defaultAccess = args.Count > 1 ? MailingListPyLong(args[1]) ?? 0 : 0;
                    var defaultMemberAccess = args.Count > 2 ? MailingListPyLong(args[2]) ?? 0 : 0;
                    var cost = args.Count > 3 ? MailingListPyLong(args[3]) ?? 0 : 0;
                    if (cost < 0)
                        return Eve.Marshal.PyNone.Instance;
                    var row = new JsonObject
                    {
                        ["listID"] = nextId,
                        ["name"] = name,
                        ["displayName"] = displayName,
                        ["ownerID"] = characterId,
                        ["members"] = new JsonObject { [MailingListKey(characterId)] = 0 },
                        ["defaultAccess"] = defaultAccess,
                        ["defaultMemberAccess"] = defaultMemberAccess,
                        ["cost"] = cost,
                        ["access"] = new JsonObject(),
                        ["welcomeMail"] = null,
                    };
                    await PersistMailingListRowAsync(nextId, row, cancellationToken);
                    return new Eve.Marshal.PyLong(nextId);
                }

                if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.Join)
                {
                    if (args.Count < 1 || MailingListPyText(args[0]) is not { } requestedName)
                        return Eve.Marshal.PyNone.Instance;
                    var (normalized, _) = NormalizeMailingListName(requestedName);
                    var found = lists.FirstOrDefault(entry => entry.Value is JsonObject row &&
                        !MailingListDeleted(row) &&
                        string.Equals(MailingListJsonText(row["name"]), normalized, StringComparison.OrdinalIgnoreCase));
                    if (found.Value is not JsonObject list)
                        return Eve.Marshal.PyNone.Instance;
                    var id = MailingListJsonLong(list["listID"]);
                    var members = list["members"] as JsonObject ?? new JsonObject();
                    var memberKey = MailingListKey(characterId);
                    var role = MailingListRole(list, characterId);
                    if (members.ContainsKey(memberKey))
                        return MailingListJoinValue(id, list, characterId, role);

                    var defaultAccess = MailingListJsonLong(list["defaultAccess"]);
                    var explicitAccess = list["access"] as JsonObject;
                    var corporationId = session.GetInt("corpid") ?? 0;
                    var allianceId = session.GetInt("allianceid") ?? 0;
                    var entityAccess = corporationId > 0 ? explicitAccess?[MailingListKey(corporationId)] : null;
                    entityAccess ??= allianceId > 0 ? explicitAccess?[MailingListKey(allianceId)] : null;
                    var allowed = defaultAccess == 0 || entityAccess is not null && MailingListJsonLong(entityAccess) == 0;
                    if (!allowed)
                        return Eve.Marshal.PyNone.Instance;

                    var updated = (JsonObject)list.DeepClone();
                    var updatedMembers = updated["members"] as JsonObject ?? new JsonObject();
                    var memberRole = MailingListJsonLong(updated["defaultMemberAccess"]);
                    updatedMembers[memberKey] = memberRole;
                    updated["members"] = updatedMembers;
                    await PersistMailingListRowAsync(id, updated, cancellationToken);
                    return MailingListJoinValue(id, updated, characterId, memberRole);
                }

                if (args.Count == 0 || MailingListPyLong(args[0]) is not { } listId || listId <= 0)
                    return Eve.Marshal.PyNone.Instance;
                var key = MailingListKey(listId);
                if (lists[key] is not JsonObject current || MailingListDeleted(current))
                    return Eve.Marshal.PyNone.Instance;

                var currentRole = MailingListRole(current, characterId);
                var membersNow = current["members"] as JsonObject ?? new JsonObject();
                switch (mutation.Method)
                {
                    case Eve.Services.Core.MailingListManagerMethod.Leave:
                    {
                        if (!membersNow.ContainsKey(MailingListKey(characterId)) ||
                            MailingListJsonLong(current["ownerID"]) == characterId)
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        (updated["members"] as JsonObject)?.Remove(MailingListKey(characterId));
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        return Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.Delete:
                    {
                        if (MailingListJsonLong(current["ownerID"]) != characterId)
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        updated["deleted"] = true;
                        updated["deletedAt"] = Py.FileTimeFromUnixMs(now());
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        return Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.KickMembers:
                    case Eve.Services.Core.MailingListManagerMethod.SetMembersMuted:
                    case Eve.Services.Core.MailingListManagerMethod.SetMembersOperator:
                    case Eve.Services.Core.MailingListManagerMethod.SetMembersClear:
                    {
                        if (args.Count < 2 || !MailingListCanManage(current, characterId) ||
                            args[1] is not Eve.Marshal.PyList targetList)
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        var members = updated["members"] as JsonObject ?? new JsonObject();
                        var defaultRole = MailingListMemberRole(MailingListJsonLong(updated["defaultMemberAccess"]));
                        var seen = new HashSet<long>();
                        var changed = new List<long>();
                        foreach (var targetValue in targetList.Items)
                        {
                            if (MailingListPyLong(targetValue) is not { } targetId || targetId <= 0 ||
                                !seen.Add(targetId))
                                continue;
                            if (seen.Count > 1000)
                                break;
                            var targetKey = MailingListKey(targetId);
                            if (!members.ContainsKey(targetKey))
                                continue;
                            var targetRole = MailingListRole(current, targetId);
                            if (!MailingListCanManageTarget(currentRole, targetRole))
                                continue;

                            if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.KickMembers)
                            {
                                members.Remove(targetKey);
                                changed.Add(targetId);
                                continue;
                            }

                            var nextRole = mutation.Method switch
                            {
                                Eve.Services.Core.MailingListManagerMethod.SetMembersMuted => 0,
                                Eve.Services.Core.MailingListManagerMethod.SetMembersOperator => 2,
                                _ => defaultRole,
                            };
                            if (targetRole == nextRole)
                                continue;
                            members[targetKey] = nextRole;
                            changed.Add(targetId);
                        }
                        if (changed.Count == 0)
                            return Eve.Marshal.PyNone.Instance;

                        updated["members"] = members;
                        updated["updatedAt"] = MailingListTimestamp(now());
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.KickMembers)
                        {
                            foreach (var targetId in changed)
                                characterSessions.Of(targetId)?.SendNotification(
                                    "OnMailingListLeave", "clientID", new Eve.Marshal.PyLong(listId), new Eve.Marshal.PyLong(targetId));
                        }
                        else
                        {
                            var notification = mutation.Method switch
                            {
                                Eve.Services.Core.MailingListManagerMethod.SetMembersMuted => "OnMailingListSetMuted",
                                Eve.Services.Core.MailingListManagerMethod.SetMembersOperator => "OnMailingListSetOperator",
                                _ => "OnMailingListSetClear",
                            };
                            foreach (var targetId in changed)
                                characterSessions.Of(targetId)?.SendNotification(
                                    notification, "clientID", new Eve.Marshal.PyLong(listId));
                        }
                        return Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.SetEntitiesAccess:
                    {
                        if (args.Count < 2 || !MailingListCanManage(current, characterId) ||
                            args[1] is not Eve.Marshal.PyDict accessByEntity)
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        var access = updated["access"] as JsonObject ?? new JsonObject();
                        var updates = 0;
                        foreach (var pair in accessByEntity.Entries)
                        {
                            if (MailingListPyLong(pair.Key) is not { } entityId || entityId <= 0 ||
                                MailingListPyLong(pair.Value) is not { } mode)
                                continue;
                            var entityKey = MailingListKey(entityId);
                            var nextAccess = MailingListAccess(mode);
                            if (access.ContainsKey(entityKey) && MailingListJsonLong(access[entityKey]) == nextAccess)
                                continue;
                            access[entityKey] = nextAccess;
                            if (++updates >= 1000)
                                break;
                        }
                        if (updates == 0)
                            return Eve.Marshal.PyNone.Instance;
                        updated["access"] = access;
                        updated["updatedAt"] = MailingListTimestamp(now());
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        return Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.ClearEntityAccess:
                    {
                        if (args.Count < 2 || !MailingListCanManage(current, characterId) ||
                            MailingListPyLong(args[1]) is not { } entityId || entityId <= 0)
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        if ((updated["access"] as JsonObject)?.Remove(MailingListKey(entityId)) != true)
                            return Eve.Marshal.PyNone.Instance;
                        updated["updatedAt"] = MailingListTimestamp(now());
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        return Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.SetDefaultAccess:
                    {
                        if (args.Count < 3 || !MailingListCanManage(current, characterId) ||
                            MailingListPyLong(args[1]) is not { } defaultAccess ||
                            MailingListPyLong(args[2]) is not { } defaultMemberAccess)
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        updated["defaultAccess"] = MailingListAccess(defaultAccess);
                        updated["defaultMemberAccess"] = MailingListMemberRole(defaultMemberAccess);
                        var cost = args.Count > 3 ? MailingListPyLong(args[3]) ?? 0 : 0;
                        updated["cost"] = Math.Max(0, cost);
                        updated["updatedAt"] = MailingListTimestamp(now());
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        return Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.SaveWelcomeMail:
                    case Eve.Services.Core.MailingListManagerMethod.SendWelcomeMail:
                    {
                        if (args.Count < 3 || !MailingListCanManage(current, characterId))
                            return Eve.Marshal.PyNone.Instance;
                        var title = MailingListBoundedText(MailingListPyText(args[1]), 150);
                        var body = MailingListBoundedText(MailingListPyText(args[2]), 8000);
                        var timestamp = MailingListTimestamp(now());
                        var updated = (JsonObject)current.DeepClone();
                        updated["welcomeMail"] = new JsonObject
                        {
                            ["title"] = title,
                            ["body"] = body,
                            ["savedAt"] = timestamp,
                        };
                        updated["updatedAt"] = timestamp;
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        if (mutation.Method == Eve.Services.Core.MailingListManagerMethod.SaveWelcomeMail)
                            return Eve.Marshal.PyNone.Instance;

                        var messageId = await mailSendWriter.SendAsync(
                            new Eve.Services.Core.MailSendRequest(
                                SenderCharacterId: characterId,
                                CharacterRecipientIds: [],
                                MailingListId: listId,
                                CorporationOrAllianceRecipientIds: null,
                                Title: title,
                                Body: body,
                                ReplyToMessageId: null,
                                ForwardedFromMessageId: null),
                            session,
                            cancellationToken);
                        return messageId is > 0
                            ? new Eve.Marshal.PyLong(messageId.Value)
                            : Eve.Marshal.PyNone.Instance;
                    }
                    case Eve.Services.Core.MailingListManagerMethod.ClearWelcomeMail:
                    {
                        if (!MailingListCanManage(current, characterId))
                            return Eve.Marshal.PyNone.Instance;
                        var updated = (JsonObject)current.DeepClone();
                        updated["welcomeMail"] = null;
                        updated["updatedAt"] = MailingListTimestamp(now());
                        await PersistMailingListRowAsync(listId, updated, cancellationToken);
                        return Eve.Marshal.PyNone.Instance;
                    }
                    default:
                        return Eve.Marshal.PyNone.Instance;
                }
            }
            finally
            {
                mailingListMutationGate.Release();
            }
        };

    static Eve.Marshal.PyValue MailingListJoinValue(
        long listId, JsonObject list, long characterId, long role) =>
        Py.KeyVal([
            ("id", new Eve.Marshal.PyLong(listId)),
            ("name", new Eve.Marshal.PyStr(MailingListJsonText(list["name"]))),
            ("displayName", new Eve.Marshal.PyStr(MailingListJsonText(list["displayName"]))),
            ("isMuted", role == 0 ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
            ("isOperator", role == 2 || role == 3 ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
            ("isOwner", MailingListJsonLong(list["ownerID"]) == characterId ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False),
        ]);

    async ValueTask<PersistenceIndustryJobOperations.IndustryFacility?> ResolveIndustryFacilityAsync(
        long facilityId, long stationId, long solarSystemId, long characterId, CancellationToken token)
    {
        if (stations.Get(facilityId) is { } station)
        {
            if (station.SolarSystemId != solarSystemId || stationId > 0 && stationId != station.Id)
                return null;
            return new PersistenceIndustryJobOperations.IndustryFacility(
                station.Id, station.SolarSystemId, station.OwnerId, 0m, 1d, true);
        }

        if (stationId > 0)
            return null;
        var persisted = await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token);
        if (persisted is not { PersistentlyDeployed: true })
            return null;

        var structures = await ReadGameStoreTableAsync("structures", token);
        if (structures[facilityId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject structure)
            return null;
        if (IndustryLong(structure["solarSystemID"]) != solarSystemId)
            return null;

        var services = structure["services"] as JsonObject ?? structure["serviceStates"] as JsonObject;
        var industryOnline = IndustryTruthy(structure["industryServiceOnline"]) ||
            IndustryTruthy(structure["manufacturingServiceOnline"]) ||
            IndustryTruthy(structure["researchServiceOnline"]);
        if (!industryOnline && services is not null)
        {
            foreach (var (key, value) in services)
            {
                var name = key + " " + (value is JsonObject service
                    ? service["serviceName"]?.ToString() ?? service["name"]?.ToString() ?? ""
                    : "");
                if ((name.Contains("industry", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("manufactur", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("research", StringComparison.OrdinalIgnoreCase)) &&
                    IndustryOnline(value))
                {
                    industryOnline = true;
                    break;
                }
            }
        }
        if (!industryOnline)
            return null;

        var ownerCorporationId = IndustryLong(structure["ownerCorpID"] ?? structure["corporationID"]);
        if (ownerCorporationId <= 0)
            return null;
        var taxRate = IndustryDecimal(structure["industryTaxRate"] ?? structure["taxRate"]);
        if (taxRate > 1)
            taxRate /= 100m;
        var timeMultiplier = IndustryDouble(structure["industryTimeMultiplier"], 1d);
        if (!double.IsFinite(timeMultiplier) || timeMultiplier <= 0)
            return null;
        return new PersistenceIndustryJobOperations.IndustryFacility(
            facilityId, solarSystemId, ownerCorporationId, Math.Max(0, taxRate), timeMultiplier, true);
    }

    static long IndustryLong(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
    static decimal IndustryDecimal(JsonNode? value) =>
        decimal.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;
    static double IndustryDouble(JsonNode? value, double fallback) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
            ? result : fallback;
    static bool IndustryTruthy(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) ? flag : IndustryLong(value) != 0;
    static bool IndustryOnline(JsonNode? value) =>
        value is JsonObject service
            ? IndustryTruthy(service["online"] ?? service["isOnline"] ?? service["active"])
            : IndustryTruthy(value);

    static PyValue? FacilityField(PyValue? source, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = source switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict args } => args.Entries,
            PyObjectEx value => value.Dict,
            _ => Array.Empty<KeyValuePair<PyValue, PyValue>>(),
        };
        foreach (var (key, value) in entries)
            if (Eve.Macho.JsSemantics.StringValue(key) == name) return value;
        return null;
    }
    static long FacilityNumber(PyValue? value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => 0,
    };
    async ValueTask<PyValue?> FacilityManagerRpcAsync(ServiceCall call, CancellationToken token)
    {
        async ValueTask<PyValue?> ReadFacilityAsync(long facilityId)
        {
            if (facilityId <= 0) return null;
            var station = stations.Get(facilityId);
            var systemId = station?.SolarSystemId ?? 0;
            var typeId = station?.TypeId ?? 0;
            if (station is null)
            {
                var info = await structureDirectoryReadModel.GetStructureInfoAsync(facilityId, token);
                if (info is not { PersistentlyDeployed: true }) return null;
                systemId = (int)FacilityNumber(FacilityField(info.Info, "solarSystemID"));
                typeId = (int)FacilityNumber(FacilityField(info.Info, "typeID"));
            }
            if (systemId <= 0 || typeId <= 0) return null;
            var facility = await ResolveIndustryFacilityAsync(
                facilityId, 0, systemId, call.Session.CharacterId ?? 0, token);
            if (facility is null) return null;

            var activities = new PyDict([]);
            return Py.KeyVal(
                ("facilityID", new PyLong(facility.FacilityId)),
                ("typeID", new PyInt(typeId)),
                ("ownerID", new PyLong(facility.OwnerId)),
                ("tax", Py.Number((double)facility.TaxRate)),
                ("solarSystemID", new PyInt(facility.SolarSystemId)),
                ("online", PyValue.Of(facility.IsOnline)),
                ("serviceAccess", new PyDict([])),
                ("sccTaxModifier", Py.Number(1)),
                ("rigModifiers", new PyDict([])),
                ("globalModifiers", Py.Dict(("faction", Py.Number(1)), ("system", new PyDict([])))),
                ("activities", activities));
        }

        if (call.Method == "GetFacility")
            return await ReadFacilityAsync(FacilityNumber(call.Arg(0)));
        if (call.Method != "GetFacilitiesByID")
            return null;

        var requested = call.Arg(0) switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        var facilities = new List<PyValue>();
        foreach (var raw in requested)
            if (await ReadFacilityAsync(FacilityNumber(raw)) is { } facility)
                facilities.Add(facility);
        return new PyList(facilities);
    }
    var industryJobOperations = new PersistenceIndustryJobOperations(
        source, industrySchema, items, economyWallets, skills, sde, corporations,
        foundations.Events, stations, ResolveIndustryFacilityAsync, now);
    var mapSolarSystems = new Lazy<Task<IReadOnlyList<Eve.Sde.SdeRow>>>(() =>
        sde.ReadAsync("mapSolarSystems", cancellationToken: CancellationToken.None));
    var mapIndustrySystems = new Lazy<Task<IReadOnlyList<Eve.Sde.SdeRow>>>(() =>
        sde.ReadAsync("industrySystems", cancellationToken: CancellationToken.None));

    static long PositiveMapRpcId(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 &&
            number.Value <= long.MaxValue && Math.Truncate(number.Value) == number.Value => (long)number.Value,
        PyStr text when long.TryParse(text.Value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        _ => 0,
    };
    static int MapActivityId(PyValue value)
    {
        var id = PositiveMapRpcId(value);
        return id > 0 && id <= int.MaxValue ? (int)id : 0;
    }

    async ValueTask<PyValue?> ReadMapRpcDataAsync(ServiceCall call)
    {
        switch (call.Method)
        {
            case "GetCurrentSovData":
                return await sovereigntyReadModel.ReadCurrentMapDataAsync(
                    PositiveMapRpcId(call.Arg(0)),
                    await mapSolarSystems.Value.WaitAsync(call.Cancellation),
                    call.Cancellation);
            case "GetRecentSovActivity":
                return await sovereigntyReadModel.ReadRecentSovActivityAsync(call.Cancellation);
            case "GetIndustryCostModifier":
                return industryJobOperations.ReadMapIndustryCostModifier(
                    MapActivityId(call.Arg(0)),
                    await mapIndustrySystems.Value.WaitAsync(call.Cancellation));
            case "GetIndustryJobsOverLast24Hours":
                return await industryJobOperations.ReadMapIndustryJobsOverLast24HoursAsync(
                    MapActivityId(call.Arg(0)), now(), call.Cancellation);
            default:
                return null;
        }
    }
    foundations.Events.Subscribe(
        "industry.jobs.client-notifications",
        [Eve.Services.Core.IndustryJobEvents.Changed],
        (context, token) =>
        {
            token.ThrowIfCancellationRequested();
            var change = context.Event.Payload<Eve.Services.Core.IndustryJobChangedEvent>();
            if (change is null)
                return Task.CompletedTask;
            foreach (var characterId in new[] { change.OwnerId, change.InstallerId }.Distinct())
            {
                if (characterSessions.Of(characterId) is not { } session)
                    continue;
                session.SendNotification(
                    "OnIndustryJob", "charid",
                    new PyInt(change.JobId), new PyInt(change.OwnerId), new PyInt(change.BlueprintId),
                    new PyInt(change.InstallerId), new PyInt(change.Status),
                    change.SuccessfulRuns is { } runs ? new PyInt(runs) : PyNone.Instance);
            }
            return Task.CompletedTask;
        });

    var corporationOfficeStore = new Eve.Corporations.CorporationOfficeStore(source, corporationSchema);
    var officeManager = new Eve.Services.Corporations.OfficeManagerService(
        corporationOfficeStore, corporations, characters, items,
        economyWallets,
        id => stations.Get(id) is { } station
            ? new Eve.Corporations.CorporationOfficeStation(
                station.Id, station.TypeId, station.OwnerId, station.SolarSystemId, station.Security)
            : null,
        now);
    var skinCosmeticsCatalog = await ShipCosmeticsCatalog.LoadAsync(sde);
    var agentMissionAgentById = agentMissionAgentRows.ToDictionary(agent => agent.AgentId);
    var agentMissionStaticAuthority = EveJsMissionAuthorityDataSource.Shared;
    var agentMissionRpcCallbacks = new AgentMissionRpcCallbacks(
        ReadAgents: _ => ValueTask.FromResult<IReadOnlyList<AgentMissionAgentData>>(agentMissionAgentRows),
        ReadDisabledMissions: _ => ValueTask.FromResult(agentMissionStaticAuthority.DisabledMissionIds),
        ReadPlausibleMissionIds: (agentId, _) => ValueTask.FromResult(
            agentMissionStaticAuthority.PlausibleMissionIds(agentId)),
        ReadEpicArcMessages: _ => ValueTask.FromResult<JsonObject?>(agentMissionStaticAuthority.EpicArcMessageMaps),
        ReadSolarSystemOfAgent: (agentId, _) => ValueTask.FromResult<long?>(
            agentMissionAgentById.TryGetValue(agentId, out var agent) ? agent.SolarSystemId : null));
    var agentMissionService = new AgentMissionService(
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
        },
        rpcCallbacks: agentMissionRpcCallbacks);
    async Task<JsonObject?> ReadCharacterGameStoreRowAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0) return null;
        var charactersState = await ReadGameStoreTableAsync("characters", cancellationToken);
        return charactersState[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
    }
    async Task<(JsonObject? MissionRecord, JsonObject? DungeonState, bool? InActiveDungeon)> ReadMissionTrackerContextAsync(
        long characterId, long agentId, ClientSession session, CancellationToken cancellationToken)
    {
        var missionRecord = await agentMissionService.ReadMissionRecordAsync(characterId, agentId, cancellationToken);
        if (missionRecord is null) return (null, null, false);
        var dungeonState = await ReadGameStoreTableAsync("dungeonRuntimeState", cancellationToken);
        if (spaceSessions is null) return (missionRecord, dungeonState, false);
        var shipId = spaceSessions.ShipIdOf(session);
        if (shipId is not > 0) return (missionRecord, dungeonState, null);
        var position = await spaceSessions.PositionOfAsync(session, shipId.Value, cancellationToken);
        var currentSystemId = position?.SolarSystemId ??
            session.GetInt("solarsystemid2") ?? session.GetInt("solarsystemid") ?? 0;
        var active = MissionTrackerGameStoreProjection.IsInActiveMissionDungeon(
            missionRecord,
            dungeonState,
            characterId,
            currentSystemId,
            shipId.Value,
            position?.X,
            position?.Y,
            position?.Z,
            spaceSessions.IsWarping(session));
        return (missionRecord, dungeonState, active);
    }
    async Task<AirNpeCharacterState?> ReadAirNpeCharacterStateAsync(long characterId, CancellationToken cancellationToken)
    {
        var character = await characters.GetAsync(characterId, cancellationToken);
        return character is null ? null : new AirNpeCharacterState(character.TutorialEntryMode, character.AirNpeState);
    }

    async ValueTask<Eve.Services.Core.ShipStanceChange?> SetShipStanceAsync(
        long shipId, int stanceId, long characterId, CancellationToken cancellationToken)
    {
        var ship = await items.GetAsync(shipId, cancellationToken);
        if (ship is null)
            return new(false, ErrorCode: "SHIP_NOT_FOUND");
        if (ship.OwnerId != characterId)
            return new(false, ErrorCode: "SHIP_NOT_OWNED");
        if (!Eve.Services.Core.ShipStanceMgrService.StanceModifiersByShipType.TryGetValue(ship.TypeId, out var stanceTypes))
            return new(false, ErrorCode: "SHIP_STANCE_UNSUPPORTED");
        if (!stanceTypes.TryGetValue(stanceId, out var modifierTypeId))
            return new(false, ErrorCode: "SHIP_STANCE_INVALID");

        await dogma.EnsureTypesAsync([ship.TypeId], cancellationToken);
        var cooldownMs = dogma.Type(ship.TypeId)?.Attributes.GetValueOrDefault(1985, 0d) ?? 0d;
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var lockedShip = await items.LockAndGetAsync(connection, transaction, shipId, cancellationToken);
        if (lockedShip is null)
            return new(false, ErrorCode: "SHIP_NOT_FOUND");
        if (lockedShip.OwnerId != characterId)
            return new(false, ErrorCode: "SHIP_NOT_OWNED");
        if (lockedShip.TypeId != ship.TypeId)
            return new(false, ErrorCode: "SHIP_NOT_FOUND");

        var modifiers = await items.LockInLocationAsync(
            connection, transaction, shipId, characterId, Eve.Inventory.ItemFlags.HiddenModifiers,
            stanceTypes.Values.ToArray(), cancellationToken);
        var currentStanceId = modifiers.OrderBy(item => item.Id)
            .Select(item => stanceTypes.FirstOrDefault(pair => pair.Value == item.TypeId).Key)
            .FirstOrDefault(id => id > 0);
        if (currentStanceId <= 0)
            currentStanceId = 1;

        var exactTarget = modifiers.Count == 1 && modifiers[0].TypeId == modifierTypeId;
        var changed = currentStanceId != stanceId;
        var lastSwitchMs = modifiers.Select(ParseShipStanceSwitchTime)
            .Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max();
        if (!changed && exactTarget)
        {
            await transaction.CommitAsync(cancellationToken);
            return new(true, currentStanceId);
        }

        var nowMs = now();
        if (changed && cooldownMs <= 0)
            return new(false, ErrorCode: "SHIP_STANCE_COOLDOWN_UNAVAILABLE");
        if (changed && lastSwitchMs > 0 && nowMs < lastSwitchMs + cooldownMs)
            return new(false, ErrorCode: "SHIP_STANCE_COOLDOWN");

        foreach (var modifier in modifiers)
            await items.DeleteAsync(connection, transaction, modifier.Id, cancellationToken);
        var switchedAt = changed ? nowMs : lastSwitchMs > 0 ? lastSwitchMs : (long?)null;
        var customInfo = switchedAt is { } timestamp
            ? $"shipStance:{stanceId}:switchedAtMs:{timestamp}"
            : $"shipStance:{stanceId}";
        await items.CreateAsync(connection, transaction,
            new Eve.Inventory.NewItem(modifierTypeId, characterId, shipId,
                Eve.Inventory.ItemFlags.HiddenModifiers, CustomInfo: customInfo), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, currentStanceId);
    }

    static long? ParseShipStanceSwitchTime(Eve.Inventory.Item item)
    {
        var parts = item.CustomInfo?.Split(':');
        return parts is { Length: 4 } && parts[0] == "shipStance" && parts[2] == "switchedAtMs" &&
               long.TryParse(parts[3], System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.InvariantCulture, out var timestamp) && timestamp >= 0
            ? timestamp
            : null;
    }

    async ValueTask<JsonObject?> ReadKillmailAsync(long killId, CancellationToken cancellationToken)
    {
        var state = await ReadGameStoreTableAsync("killmails", cancellationToken);
        var key = killId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var record = (state["records"] as JsonObject)?[key] as JsonObject;
        return record?.DeepClone() as JsonObject;
    }

    Eve.Services.Slash.SlashService? publicQaSlashService = null;
    var gameRpcCallbacks = new Eve.Services.Core.GameRpcCallbacks(
        ReadSharedSettings: () => mailCache.Read("sharedSettings", "/").Data as JsonObject,
                ReadOverviewPresets: () => mailCache.Read("overviewSharedPresets", "/").Data as JsonObject,
                WriteOverviewPresets: state => mailCache.Write("overviewSharedPresets", "/", state).Success,
        WriteSharedSettingsAsync: async (state, cancellationToken) =>
        {
            var write = mailCache.Write("sharedSettings", "/", state);
            if (!write.Success)
                return false;
            await mailCache.FlushTableAsync("sharedSettings", cancellationToken);
            return true;
        },
        SetShipStanceAsync: SetShipStanceAsync,
        ReadInterferenceTableAsync: async cancellationToken =>
            await ReadGameStoreTableAsync("solarSystemInterferenceState", cancellationToken),
        ReadKillmailAsync: ReadKillmailAsync,
        ReadMiningScanResultsAsync: spaceSessions is null
            ? null
            : new Eve.Host.MiningScanRpcReadModel(spaceSessions, items, spaceItemStore, dogma).ReadAsync,
        ReadPeaceTreaties: async (ownerId, cancellationToken) =>
        {
            var state = await ReadGameStoreTableAsync("corporationRuntime", cancellationToken);
            var nowFiletime = DateTimeOffset.FromUnixTimeMilliseconds(now()).ToFileTime();
            return PersistencePeaceTreatyReadModel.Read(state, ownerId, nowFiletime);
        },
        PublicQaTools: new Eve.Services.Core.PublicQaToolsRpcCallbacks(
            IsStaff: Eve.Services.Core.PublicQaToolsServerService.HasStaffAccountRole,
            SlashCmdAsync: async (request, cancellationToken) =>
            {
                if (publicQaSlashService is null)
                    return null;
                return await publicQaSlashService.SlashCmd(new ServiceCall
                {
                    Method = "SlashCmd",
                    Args = request.Arguments,
                    Kwargs = request.KeywordArguments,
                    Session = request.Session,
                    CallId = request.CallId,
                    Cancellation = cancellationToken,
                });
            }));

    var stationGuestLedger = new Eve.Services.Station.StationGuestLedger();
    var characterEntryHooks = new List<ICharacterEntryHook>();
    if (spaceSessions is not null)
        characterEntryHooks.Add(spaceSessions);
    characterEntryHooks.Add(new SkillsLoginHook(training, skillCatalog, characterSessions, announcer, now));
    characterEntryHooks.Add(corporationLogin);
    characterEntryHooks.Add(new FleetLoginHook(fleetStore.MemberForCharacterAsync));
    characterEntryHooks.Add(dogmaSync);
    characterEntryHooks.Add(new Eve.Services.Station.GuestLoginHook(characterSessions, stationGuestLedger));
    IReadOnlyList<IService> services =
    [
        new CharUnboundMgrService(
            accounts, characters, items, training, wallets, creation, catalog, online, characterEntryHooks, Random.Shared,
            newCharacterExperienceMode == "tutorial",
            sendWelcomeMailAsync: SendNewCharacterWelcomeMailAsync,
            unreadMailCount: ReadUnreadMailCount,
            securityStatusOf: securityStatusOf),
        new MultiLoginBlockerService(),
        new PopulationCapService(characters),
        new Eve.Services.CharacterInfo.ConfigService(characters, corporations, stations, solarSystems),
        Eve.Services.Core.CoreServices.CreateLookupService(
            new Eve.Host.PersistenceLookupDirectory(characters, corporations, sde, stations)),
        new Eve.Services.Core.SecurityMgrService(),
        new Eve.Services.Core.RWManagerService(),
        new Eve.Services.Core.FwCharacterEnlistmentMgrService(new Eve.Services.Core.FwCharacterEnlistmentRpcCallbacks(
            ReadCharacterStateAsync: async (characterId, cancellationToken) =>
            {
                var character = await characters.GetAsync(characterId, cancellationToken);
                return character is null
                    ? null
                    : new Eve.Services.Core.FwCharacterWarfareState(character.DirectFactionId, character.DirectEnlistmentCooldownFileTime);
            },
            ReadCorporationFactionIdAsync: async (corporationId, cancellationToken) =>
                Eve.Services.Corporations.CorporationWire.FactionId(await corporations.GetAsync(corporationId, cancellationToken)),
            ReadAllowedFactionsAsync: async (corporationId, cancellationToken) =>
                Eve.Services.Corporations.CorporationWire.AllowedEnlistmentFactions(await corporations.GetAsync(corporationId, cancellationToken)),
            SetAllowedFactionsAsync: async (characterId, corporationId, factions, cancellationToken) =>
                await corporations.SetFactionWarfareAllowedEnlistmentFactionsAsync(characterId, corporationId, factions, cancellationToken),
            SaveCharacterStateAsync: async (characterId, factionId, cooldown, cancellationToken) =>
                await characters.SetFactionWarfareStateAsync(characterId, factionId, cooldown, cancellationToken))),
        new Eve.Services.Core.FwVictoryPointMgrService(),
        new Eve.Services.Core.InsurgencySolarsystemService(),
        new Eve.Services.Core.AlertService(),
        new Eve.Services.Core.FighterMgrService { Provider = new Eve.Services.Space.SpaceFighters(sim, ships, dogma, session => spaceSessions?.SolarSystemOf(session), message => Console.Error.WriteLine($"eve-host: fighters: {message}")) },
        new Eve.Services.Space.EssMgrService(),
        new Eve.Services.Space.DynamicResourceCacheMgrService(),
        new Eve.Services.Core.ShipKillCounterService(ct => ReadGameStoreTableAsync("shipKillCounters", ct)),
        // security status is the character's relation of that kind (Eve.Relations); crimewatch only asks for it
        new Eve.Services.Crimewatch.CrimewatchService(
            crimewatchStore, crimewatchRuntime, now,
            securityStatusOf),
        new Eve.Services.Crimewatch.KillRightMgrService(
            crimewatchStore, crimewatchRuntime, economyWallets, wallets, now,
            message => Console.Error.WriteLine($"eve-host: crimewatch: {message}"))
        {
            KillRightUsedNotification = (activation, ct) => mailSendWriter.WriteKillRightNotificationAsync(
                activation.OwnerCharacterId, activation.TargetCharacterId,
                Eve.Services.Crimewatch.KillRightNotificationTypes.Used,
                new JsonObject { ["charID"] = activation.TargetCharacterId },
                $"kill-right-used:{activation.ActivationId}", ct)
        },
        new Eve.Services.Crimewatch.BountyProxyKillRightService(
            store: crimewatchStore,
            notificationWriter: mailSendWriter.WriteKillRightNotificationAsync,
            nowUnixMs: now),

        new SkillMgr2Service(),
        skillHandler,
        new CharMgrService(
            characters,
            stations,
            charMgrReadModel.ReadRecentShipKillmailPageAsync,
            charMgrReadModel.ReadCharacterDescriptionAsync,
            charMgrReadModel.WriteCharacterDescriptionAsync,
            ReadCharacterSecurityStatusAsync,
            charMgrReadModel.ReadMedal1GraphicIdAsync,
            charMgrReadModel.ReadAsync,
            charMgrReadModel.UpdateAsync,
            charMgrReadModel.WriteActivityStatusAsync,
            charMgrReadModel.WriteLogSettingsAsync,
            async (characterId, token) => (await corporations.MemberAsync(characterId, token))?.StartedMs,
            async (memberCharacters, token) =>
            {
                var corporationIds = memberCharacters
                    .Select(character => character.CorporationId)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToArray();
                var corporationsById = (await corporations.GetManyAsync(corporationIds, token))
                    .ToDictionary(corporation => corporation.Id);
                var characterState = await ReadGameStoreTableAsync("characters", token);
                return memberCharacters.ToDictionary(character => character.Id, character =>
                {
                    var corporation = corporationsById.GetValueOrDefault(character.CorporationId);
                    var key = character.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var row = characterState[key] as JsonObject;
                    var warFactionId = CharMgrJsonLong(row?["warFactionID"] ?? row?["militiaFactionID"]);
                    return new Eve.Services.CharacterInfo.CharMgrOrganizationInfo(
                        character.CorporationId,
                        character.AllianceId ?? corporation?.AllianceId ?? 0,
                        Math.Max(0, warFactionId));
                });
            }),
        new Eve.Services.CharacterInfo.OnlineStatusService(characterSessions, ReadWatchlistAsync),
        new StationService(stations, characters, online, now, sovereigntyReadModel.ReadAllianceSystemsAsync, officeManager: officeManager, seedObserverGuestLedger: stationGuestLedger.SeedObserverGuestLedger, characterSessions: characterSessions),
        new Eve.Services.Station.TradeMgrService(characterSessions),
        new Eve.Host.DevIndexManagerRpcService(sovereigntyReadModel.ReadAllDevelopmentIndicesAsync),
        new Eve.Host.EjectRpcService(),
        new Eve.Host.EncodedItemsRpcService(
            async (itemId, cancellationToken) => await items.GetAsync(itemId, cancellationToken),
            async (_, item, cancellationToken) =>
            {
                var itemType = await itemTypes.GetAsync(item.TypeId, cancellationToken);
                if (itemType is null)
                    return false;
                var lists = await sde.ReadAsync("typeLists", [492L], cancellationToken);
                return lists.FirstOrDefault() is { } list &&
                       Eve.Host.EncodedItemsRpcService.IsInTypeList(list.Doc, itemType);
            }),
        new Eve.Host.InSpaceCompressionMgrRpcService(async (itemId, cancellationToken) =>
            await items.GetAsync(itemId, cancellationToken)),
        .. Eve.Services.Core.SovFwService.Create(
            sovereigntyReadModel.ReadSnapshotAsync,
            sovereigntyReadModel.IsOnLocalFuelAccessGroupAsync,
            new Eve.Services.Core.SovHubWriteOperations(
                sovWriteOperations.CanManageHubAsync,
                sovWriteOperations.SetFuelAccessGroupAsync,
                sovWriteOperations.SaveUpgradeConfigurationAsync,
                sovWriteOperations.SelfDestructUpgradeAsync,
                sovWriteOperations.SaveWorkforceConfigurationAsync),
            new Eve.Services.Core.SovSkyhookWriteOperations(
                sovWriteOperations.DestroySkyhooksAsync,
                sovWriteOperations.AcquireSkyhooksAsync)),
        new Eve.Services.Core.IncursionAnomalyService(
            "map",
            incursionAnomalyReadModel.ReadIncursionsAsync,
            incursionAnomalyReadModel.ReadAnomaliesAsync,
            new MapService(foundations.Reads, now) { ReadDataHook = ReadMapRpcDataAsync },
            now),
        new Eve.Services.Core.DungeonService(dungeonReadModel.ReadEditorCatalogAsync),
        new Eve.Services.Core.DungeonInstanceCacheMgrService(dungeonReadModel.ReadCacheSnapshotAsync, now),
        new Eve.Services.Core.IncursionAnomalyService(
            "taleMgr",
            incursionAnomalyReadModel.ReadIncursionsAsync,
            incursionAnomalyReadModel.ReadAnomaliesAsync,
            nowUnixMs: now),
        new Eve.Services.Core.IncursionAnomalyService(
            "scanMgr",
            incursionAnomalyReadModel.ReadIncursionsAsync,
            incursionAnomalyReadModel.ReadAnomaliesAsync,
            nowUnixMs: now,
            signalTrackerRegister: signalTrackerRegistrationProvider.RegisterAsync),
          HostInventoryServiceFactory.CreateInvBroker(items, itemTypes, stations, ships, dogma, now, spaceContainers, spaceDrones, corporations, characterEntryHooks),
          new Eve.Services.Core.CrateService(new PersistenceCrateServiceBackend(source, items, itemTypes, skills, skillCatalog, mailCache)),
          new Eve.Host.ShipCosmeticsMgrRpcService(mailCache, mailCache, items, source, skinCosmeticsCatalog, now,
              (characterId, shipId, skinId) =>
              {
                  if (characterSessions.Of(characterId) is not { } session) return;
                  session.SendNotification("OnShipCosmeticsChanged", "clientID", InventoryWire.Id(shipId), new PyDict([]));
                  session.SendNotification("OnCurrentShipSkinChange", "clientID", InventoryWire.Id(shipId), new PyInt(skinId ?? 0));
              },
              (characterId, skinId) =>
              {
                  if (characterSessions.Of(characterId) is { } session)
                      session.SendNotification("OnSkinLicenseActivated", "clientID",
                          new PyList([new PyInt(skinId), new PyInt(characterId)]));
              },
              inventoryItemConsumed: async (characterId, before, remainingQuantity, ct) =>
              {
                  if (characterSessions.Of(characterId) is not { } session) return;
                  var type = await itemTypes.GetAsync(before.TypeId, ct);
                  var row = new ItemRow(before.Id, before.TypeId, before.OwnerId, before.LocationId, before.Flag, remainingQuantity, type?.GroupId ?? 0, type?.CategoryId ?? 0);
                  var changes = new PyDict([new KeyValuePair<PyValue, PyValue>(new PyInt(9), InventoryWire.Id(before.Quantity))]);
                  session.SendNotification("OnItemChange", "clientID", InventoryWire.PackedRow(row), changes, PyNone.Instance);
              }),
          new Eve.Host.DynamicItemRpcService(items, itemMutations, dogma, itemTypes),
          new Eve.Host.CharacterEnergyMgrRpcService(mailCache, now),
          new Eve.Host.PaperDollServerRpcService(characters),
          new Eve.Host.PlanetMgrRpcService(mailCache, sde, now),
          new Eve.Host.IndustryMonitorRpcService(mailCache, items, nowUnixMs: now, itemTypes: itemTypes),
          new DogmaIMService(
            dogma, ships, now,
            skillHandler.TryActivateBoosterAsync,
            sync: dogmaSync,
            space: spaceSessions,
            modules: spaceSessions,
            health: spaceSessions,
            rounds: spaceSessions,
            log: message => Console.Error.WriteLine($"eve-host: ammo: {message}"),
            localTimers: foundations.LocalTimers),
        new AccountService(wallets, corporations, economyWallets),
        new Eve.Services.Market.MarketProxyService(market, marketTrading, marketTopology, characters, economyWallets, marketCatalog, now, characterSessions),
        new Eve.Services.Corporations.CorpMgrService(
            corporations, characters, items, itemTypes, corporationOfficeStore,
            resolveAssetLocation: id => stations.Get(id) is { } station
                ? (station.SolarSystemId, (int?)station.TypeId)
                : id is >= 0 and <= int.MaxValue && solarSystems.Get((int)id) is { } system
                    ? ((long)system.Id, (int?)null)
                    : null),
        new Eve.Services.Corporations.MutualWarInviteMgrService(),
        new Eve.Services.Corporations.CorporationService(corporations, characters, characterSessions),
        new Eve.Services.Corporations.CorpRegistryService(corporations, characters, now, characterSessions)
        {
            AllianceAffiliationWriter = allianceDue.WriteAffiliationAsync
        },
        officeManager,
        new Eve.Services.Corporations.StandingMgrService(foundations.Reads, corporations, now),
        new Eve.Services.Corporations.WarsInfoMgrService(foundations.Relations, now),
        new Eve.Services.Corporations.VoteManagerService(corporations),
        new Eve.Services.Space.FleetObjectService(
            fleetStore, now,
            loadFleetSetup: async (fleetId, characterId, setupName, _, cancellationToken) =>
            {
                var characters = await ReadGameStoreTableAsync("characters", cancellationToken);
                var setup = Eve.Host.PersistenceFleetSettings.FindSetup(characters, characterId, setupName)
                    ?? throw new InvalidOperationException("FleetError");
                if (!await fleetStore.ApplyFleetSetupAsync(fleetId, characterId, setup, cancellationToken))
                    throw new InvalidOperationException("FleetError");
                return PyNone.Instance;
            },
            createFleetAdvert: (fleetId, characterId, advertData, session, cancellationToken) =>
                fleetStore.FleetFinderAdvertOperationAsync(
                    "AddFleetFinderAdvert", characterId, [advertData], session,
                    FleetAdvertLeaderProfileForAsync, cancellationToken),
            sessionForCharacter: FleetSessionForCharacter,
            compositionForMembers: FleetCompositionForMembersAsync,
            sameBubble: SameFleetBubble),
        new Eve.Services.Space.FleetManagerService(
            fleetStore,
            sessionOf: FleetSessionForCharacter,
            sameBubble: SameFleetBubble),
        new Eve.Services.Space.FleetProxyService(
            fleetStore,
            fleetAdvertOperation: (operation, characterId, args, session, cancellationToken) =>
                fleetStore.FleetFinderAdvertOperationAsync(operation, characterId, args, session,
                    FleetAdvertLeaderProfileForAsync, cancellationToken),
            sessionForCharacter: FleetSessionForCharacter),
        insuranceService,
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
            },
            mailSendWriter.SendAsync),
        new NotificationMgrService(
            async (characterId, ct) =>
            {
                var notifications = await ReadGameStoreTableAsync("notifications", ct);
                return (notifications["boxes"] as JsonObject)?[characterId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            },
            now),
        new MailingListService(
            async ct =>
            {
                var mailState = await ReadGameStoreTableAsync("mail", ct);
                return mailState["mailingLists"] as JsonObject;
            },
            mutateMailingList),
        new RewardsService(),
        new Eve.Services.Core.LoginCampaignMgrService(storeRuntime),
        new Eve.Services.Core.SeasonalLoginCampaignMgrService(storeRuntime),
        new StoreOffersService(
            async ct => await ReadGameStoreTableAsync("newEdenStore", ct),
            storeRuntime.PurchaseAsync),
        new Eve.Services.Core.RedeemService(
            storeRuntime,
            async (userId, ct) => (await accounts.FindAsync(checked((uint)userId), ct))?.UserName,
            async (characterId, ct) =>
            {
                var character = await characters.GetAsync(characterId, ct);
                return character?.CreatedAt.UtcDateTime.ToFileTimeUtc();
            },
            async (call, ct) =>
            {
                if (call.Method == "UserLogOffCharacter")
                {
                    call.Session.AfterResponse(call.Session.Close);
                    return PyBool.True;
                }
                if (call.Method == "CreateRedeemTokenQA")
                    return await storeRuntime.CreateRedeemTokenQAAsync(call, ct).ConfigureAwait(false);
                return null;
            }),
        new Eve.Services.Core.VaultManagerService(storeRuntime),
        new Eve.Services.Core.FacilityManagerService(FacilityManagerRpcAsync),
        new IndustryJobsService(
            industryJobOperations.ReadJobsAsync,
            industryJobOperations,
            currentFileTime: () => checked(116444736000000000L + now() * 10_000L)),
        new Eve.Services.Core.CharacterMiningLedgerService(miningLedgerReads, () => Eve.Ships.MiningLedgerStore.FileTimeOf(now())),
        new Eve.Services.Core.CorpMiningLedgerService(miningLedgerReads, () => Eve.Ships.MiningLedgerStore.FileTimeOf(now())),
        new Eve.Services.Core.StructureDirectoryService(structureDirectoryReadModel),
        // Mutation needs structure-control/drill authority and the space timer/settlement backend.
        new Eve.Services.Core.MoonExtractionsRpcService(
            readState: () => mailCache.Read("moonExtractions", "/").Data as JsonObject ?? new JsonObject(),
            mutationBackend: null),
        new Eve.Services.Space.StructureTravelRpcService(
            "structureJumpBridgeMgr",
            jumpBridgeDestination: structureDirectoryReadModel.GetJbStructureDestinationAsync,
            linkedStructure: structureDirectoryReadModel.GetLinkedStructureAsync),
        new Eve.Services.Core.StructureRpcService("structureCargoDelivery"),
        new Eve.Services.Core.StructureRpcService("structureCompressionMgr"),
        new Eve.Services.Core.StructureRpcService("structureControl"),
        new Eve.Services.Core.StructureRpcService("structureDeliveries"),
        new Eve.Services.Core.StructureRpcService("structureDeployment"),
        new Eve.Services.Core.StructureRpcService("structureDocking"),
        new Eve.Services.Core.StructureRpcService("structureHangarViewMgr", structureRpcCallbacks),
        new Eve.Services.Core.StructureRpcService("structureProfiles", structureRpcCallbacks),
        new Eve.Services.Core.StructureRpcService("structureVulnerability"),
        new Eve.Services.Core.StructureAssetSafetyService(structureAssetSafetyReadModel),
        structureSettingsService,
        skyhookResourceMgrService,
        new AchievementTrackerMgrService(
            readState: async (characterId, ct) =>
                LoginInitializationGameStoreProjection.ReadAchievementState(
                    await ReadCharacterGameStoreRowAsync(characterId, ct)),
            hasEverWarped: async (characterId, ct) =>
                LoginInitializationGameStoreProjection.ReadHasEverWarped(
                    await ReadCharacterGameStoreRowAsync(characterId, ct)) ?? true),
        new MilestoneMgrService(),
        new OperationsManagerService(
            readCharacterProgress: async (call, ct) =>
                LoginInitializationGameStoreProjection.ReadOperationsProgress(
                    await ReadCharacterGameStoreRowAsync(LoginInitializationGameStoreProjection.CharacterId(call), ct)),
            newCharacterExperienceMode: () => newCharacterExperienceMode,
            readTutorialState: ReadAirNpeCharacterStateAsync),
        new MissionTrackerMgrService(
            readMissionInfoItems: agentMissionService.ReadMissionInfoItemsAsync,
            readAllMissionObjectives: async (characterId, agentId, session, ct) =>
            {
                var context = await ReadMissionTrackerContextAsync(characterId, agentId, session, ct);
                return MissionTrackerGameStoreProjection.ReadAllObjectives(
                    context.MissionRecord, agentId, context.InActiveDungeon == true, context.DungeonState);
            },
            isInActiveDungeon: async (call, ct) =>
            {
                var characterId = call.Session.GetInt("charid") ?? call.Session.GetInt("characterID") ?? 0;
                var agentId = LoginInitializationGameStoreProjection.PositiveId(call.Arg(0));
                if (characterId <= 0 || agentId <= 0) return false;
                return (await ReadMissionTrackerContextAsync(characterId, agentId, call.Session, ct)).InActiveDungeon;
            }),
        new AbyssalMgrService(),
        new VoidSpaceMgrService(),
        new AirNpeService(
            () => newCharacterExperienceMode,
            ReadAirNpeCharacterStateAsync,
            (characterId, tutorialEnabled, ct) => characters.MarkTutorialSkippedAsync(characterId, tutorialEnabled, ct)),
        agentMissionService,
        allianceRegistry,
        publicQaSlashService = new SlashService(new ShipHealthCommands(ships, dogma, now, spaceSessions), spaceSessions is null ? null : new DeathTestCommands(spaceSessions, new ShipTypeNames(sde)), spaceSessions is null ? null : new SuicideCommands(spaceSessions), spaceSessions is null ? null : new NpcCommands(spaceSessions), new ItemCommands(new ItemTypeNames(sde), ships, dogma, now), new SkillCommands(skillCatalog, skills, source, dogmaSync, now)),
        .. spaceSessions is null ? [] : new IService[] { new ShipService(spaceSessions, now, spaceContainers, spaceDrones), new BeyonceService(spaceSessions, now, fleetStore, FleetSessionForCharacter), new StructureTravelRpcService("structureCynoBeaconMgr"), new ShipcasterRpcService("shipCasterLandingPadMgr", solarSystems), new ShipcasterRpcService("shipCasterLauncherMgr", solarSystems), new ShipcasterRpcService("shipcasterTravelMgr", solarSystems), new ShipcasterRpcService("starterShipcasterTravelMgr", solarSystems), new EntityService(spaceDrones!, now, spaceContainers) },
    ];
    async ValueTask<Eve.Services.Space.SpaceRpcItem?> ReadTravelRpcItemAsync(long itemId, CancellationToken cancellationToken)
    {
        var item = await items.GetAsync(itemId, cancellationToken);
        if (item is null) return null;
        var type = await itemTypes.GetAsync(item.TypeId, cancellationToken);
        return new Eve.Services.Space.SpaceRpcItem(item.Id, item.OwnerId, item.TypeId, type?.GroupId ?? 0, item.Quantity);
    }
    services = [.. services,
        new Eve.Services.Space.PvpFilamentMgrService(),
        new Eve.Services.Space.RandomJumpMgrService(ReadTravelRpcItemAsync),
        new Eve.Services.Space.WarpVectorMgrService(ReadTravelRpcItemAsync),
        new Eve.Services.Space.WormholeMgrService(spaceSessions),
        new Eve.Services.Core.StructureGuestsService(
        (structureId, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var guests = characterSessions.InStructure(structureId)
                .Where(session => session.CharacterId is > 0)
                .Select(session => new Eve.Services.Core.StructureGuestSnapshot(
                    session.CharacterId!.Value,
                    Math.Max(0, session.GetInt("corpid") ?? 0),
                    Math.Max(0, session.GetInt("allianceid") ?? 0),
                    Math.Max(0, session.GetInt("warfactionid") ?? 0)))
                .ToArray();
            return ValueTask.FromResult<IReadOnlyList<Eve.Services.Core.StructureGuestSnapshot>>(guests);
        }, seedObserverGuestLedger: stationGuestLedger.SeedObserverGuestLedger)];
    services = [.. services, new RepackagingService(services.OfType<InvBrokerService>().Single())];
      services = [.. services, new Eve.Services.Corporations.BillMgrService(
          corporations,
          corporationOfficeStore,
          economyWallets,
          stationId => stations.Get(stationId)?.OwnerId,
          now)];
    return (services, spaceSessions, sim, crimewatchRuntime, calendarStore, calendarAudience, gameRpcCallbacks, keeperRpcCallbacks);
}
