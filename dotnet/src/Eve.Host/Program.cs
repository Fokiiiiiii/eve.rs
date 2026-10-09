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


public sealed class PersistenceNewEdenStoreRuntime(
    NpgsqlDataSource source,
    PersistenceStore store,
    OwnerFence ownerFence,
    PgWalletStore plexWallets,
    PgCharacterStore characters,
    PgItemStore items,
    ItemTypeCatalog itemTypes,
    CharacterSessions sessions,
    Func<long> nowUnixMs,
    Func<CancellationToken, Task<JsonObject>> readStoreState)
    : Eve.Services.Core.IRedeemCallbacks, Eve.Services.Core.IVaultManagerCallbacks, Eve.Services.Core.ILoginRewardCallbacks
{
    private const string ReceiptsTable = "newEdenStorePurchaseSettlements";
    private const string ReceiptsRow = "purchasesByOperationID";
    private const string RedeemTable = "redeemTokens";
    private const string RedeemRow = "accounts";
    private const string LoginCampaignTable = "dailyLoginCampaignProgress";
    private const string LoginCampaignRow = "accountsByUserID";
    private const int DailyLoginCampaignId = 2187;
    private const int DailyLoginBucketId = 4;
    private const int DailyLoginTrackLength = 15;
    private const int DailyLoginResetHourUtc = 11;
    private const int DailyLoginRewardTypeId = 49810;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<PyValue> PurchaseAsync(
        Eve.Services.Core.StoreOfferPurchaseRequest request,
        CancellationToken cancellationToken)
    {
        if (request.AuthenticatedUserId <= 0 || request.AuthenticatedCharacterId <= 0)
            throw new UnauthorizedAccessException("An authenticated account and character are required.");
        var operationId = Text(request.OperationId);
        if (operationId.Length == 0)
            throw new InvalidOperationException("A store purchase operation ID is required.");
        var operationKey = $"{request.AuthenticatedUserId}:{operationId}";
        var storeId = Positive(request.Store);
        var currency = Text(request.Currency).Trim().ToUpperInvariant();
        var purchaseQuantity = Positive(request.Qty);
        if (storeId <= 0 || purchaseQuantity <= 0)
            throw new InvalidOperationException("The store or purchase quantity is invalid.");

        var state = await readStoreState(cancellationToken);
        var storeCatalog = (state["stores"] as JsonObject)?[storeId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
        var offer = FindOffer(storeCatalog?["offers"] as JsonArray, request.Offer);
        if (offer is null || offer["products"] is not JsonArray products || products.Count == 0)
            throw new InvalidOperationException("The selected store offer is unavailable.");

        var unitPrice = PriceFor(offer, currency);
        var cashPurchase = IsFiat(currency) && unitPrice is > 0;
        if (request.SkipPayment && !cashPurchase)
            throw new InvalidOperationException("Payment may only be skipped for a positive fiat-priced offer.");
        long plexCost = 0;
        if (!cashPurchase)
        {
            if (currency != "PLX" || unitPrice is not { } plexPrice || plexPrice <= 0)
                throw new NotSupportedException($"The store currency {currency} has no normal settlement provider.");
            var total = checked(plexPrice * purchaseQuantity);
            if (decimal.Truncate(total) != total || total > long.MaxValue)
                throw new InvalidOperationException("PLEX prices must be positive whole units.");
            plexCost = decimal.ToInt64(total);
        }

        var recipientCharacterId = request.Recipient is PyNone
            ? request.AuthenticatedCharacterId
            : Positive(request.Recipient);
        var recipient = await characters.GetAsync(recipientCharacterId, cancellationToken);
        if (recipient is null)
            throw new InvalidOperationException("The recipient character does not exist.");

        var createdAt = ToFileTime(nowUnixMs());
        var newTokens = new JsonObject();
        var tokenIds = new List<long>();
        var productIndex = 0;
        foreach (var product in products.OfType<JsonObject>())
        {
            var typeId = checked((int)Integer(product["typeId"]));
            var quantity = checked(Integer(product["quantity"]) * purchaseQuantity);
            if (typeId <= 0 || quantity <= 0)
                throw new InvalidOperationException("The selected offer contains an invalid product.");
            var productName = String(product["productName"]);
            var description = Text(request.Message);
            if (description.Length == 0)
                description = productName;
            var tokenId = TokenId(operationKey, productIndex++);
            tokenIds.Add(tokenId);
            newTokens[tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
            {
                ["tokenID"] = tokenId,
                ["massTokenID"] = 0,
                ["typeID"] = typeId,
                ["quantity"] = quantity,
                ["stationID"] = 0,
                ["expireDateTime"] = 0,
                ["dateTime"] = createdAt,
                ["description"] = description,
                ["isAutoInject"] = false,
                ["status"] = "pending",
            };
        }
        if (!newTokens.Any())
            throw new InvalidOperationException("The selected offer contains no fulfillable products.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var receiptState = await PersistenceReadHelpers.ReadOptionalTableAsync(store, ReceiptsTable, cancellationToken);
            var receipts = receiptState[ReceiptsRow] as JsonObject ?? new JsonObject();
            if (receipts[operationKey] is JsonObject)
                return PyBool.True;

            var redeemState = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var accounts = redeemState[RedeemRow] as JsonObject ?? new JsonObject();
            var recipientAccountId = recipient.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accountTokens = accounts[recipientAccountId] as JsonObject ?? new JsonObject();
            foreach (var (key, token) in newTokens)
            {
                if (accountTokens.ContainsKey(key))
                    throw new InvalidOperationException("A store fulfillment token already exists.");
                accountTokens[key] = token?.DeepClone();
            }
            accounts[recipientAccountId] = accountTokens;

            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (plexCost > 0)
            {
                var accountCharacters = await characters.ForAccountAsync(
                    checked((int)request.AuthenticatedUserId), cancellationToken);
                var characterIds = accountCharacters.Select(character => character.Id).Order().ToArray();
                var balances = await plexWallets.ReadPlexBalancesAsync(
                    connection, transaction, characterIds, cancellationToken);
                var totalBalance = balances.Values.Aggregate(0L, (sum, balance) => checked(sum + balance));
                if (totalBalance < plexCost)
                    throw new Eve.Wallet.InsufficientFundsException(request.AuthenticatedCharacterId);

                var remaining = plexCost;
                foreach (var characterId in characterIds)
                {
                    if (!balances.TryGetValue(characterId, out var balance) || balance <= 0)
                        continue;
                    var debit = Math.Min(balance, remaining);
                    await plexWallets.AdjustPlexAsync(
                        connection, transaction, characterId, -debit, cancellationToken);
                    remaining -= debit;
                    if (remaining == 0)
                        break;
                }
            }

            receipts[operationKey] = new JsonObject
            {
                ["accountID"] = request.AuthenticatedUserId,
                ["characterID"] = request.AuthenticatedCharacterId,
                ["recipientCharacterID"] = recipientCharacterId,
                ["offerID"] = Text(request.Offer),
                ["currency"] = currency,
                ["quantity"] = purchaseQuantity,
                ["plexDebited"] = plexCost,
                ["tokenIDs"] = new JsonArray([.. tokenIds.Select(tokenId => (JsonNode?)JsonValue.Create(tokenId))]),
                ["createdAt"] = createdAt,
            };
            await store.ApplyChangesAsync(
                connection, transaction, ReceiptsTable,
                [new RowUpsert(ReceiptsRow, receipts.ToJsonString())], [], ownerFence, cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return PyBool.True;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.LoginCampaignSnapshot?> GetLoginCampaignStateAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken = default)
    {
        if (!await HasLoginRewardCharacterAsync(player, cancellationToken))
            return null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(
                store, LoginCampaignTable, cancellationToken);
            var accounts = state[LoginCampaignRow] as JsonObject;
            var accountKey = player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var progress = LoginCampaignProgress(accounts?[accountKey] as JsonObject);
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs());
            var resetId = LoginCampaignResetId(now);
            var resetStart = LoginCampaignResetStart(now);
            var nextRewardTime = progress.LastClaimReset == resetId
                ? resetStart.AddDays(1)
                : resetStart;
            var nextRewardIndex = progress.Day >= DailyLoginTrackLength ? 1 : progress.Day + 1;
            var clientState = LoginCampaignClientState(progress.Day, progress.LastClaimReset, now);

            return new Eve.Services.Core.LoginCampaignSnapshot(
                DailyLoginCampaignId,
                clientState,
                nextRewardIndex,
                Py.FileTimeFromUnixMs(nextRewardTime.ToUnixTimeMilliseconds()));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.LoginCampaignClaimResult?> TryClaimLoginCampaignRewardAsync(
        Eve.Services.Core.LoginCampaignClaimRequest request,
        CancellationToken cancellationToken = default)
    {
        var player = request.Player;
        if (request.CampaignId != DailyLoginCampaignId ||
            !await HasLoginRewardCharacterAsync(player, cancellationToken))
            return null;
        if (await itemTypes.GetAsync(DailyLoginRewardTypeId, cancellationToken) is null)
            throw new InvalidOperationException($"Daily login reward type {DailyLoginRewardTypeId} is missing from the item catalog.");

        var accountKey = player.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var nowUnix = nowUnixMs();
            var now = DateTimeOffset.FromUnixTimeMilliseconds(nowUnix);
            var resetId = LoginCampaignResetId(now);
            var resetStart = LoginCampaignResetStart(now);
            var availableAt = Py.FileTimeFromUnixMs(resetStart.ToUnixTimeMilliseconds());

            var loginTable = await PersistenceReadHelpers.ReadOptionalTableAsync(
                store, LoginCampaignTable, cancellationToken);
            var loginAccounts = loginTable[LoginCampaignRow] as JsonObject ?? new JsonObject();
            var progress = LoginCampaignProgress(loginAccounts[accountKey] as JsonObject);
            if (progress.LastClaimReset == resetId)
                return null;

            var rewardIndex = progress.Day >= DailyLoginTrackLength ? 1 : progress.Day + 1;
            if (request.ExpectedNextRewardIndex != rewardIndex ||
                request.ExpectedNextRewardTimestampFiletime != availableAt ||
                availableAt >= Py.FileTimeFromUnixMs(nowUnix))
                return null;

            var redeemTable = await PersistenceReadHelpers.ReadOptionalTableAsync(
                store, RedeemTable, cancellationToken);
            var redeemAccounts = redeemTable[RedeemRow] as JsonObject ?? new JsonObject();
            var accountTokens = redeemAccounts[accountKey] as JsonObject ?? new JsonObject();
            var tokenId = TokenId($"daily-login:{accountKey}:{resetId}", 0);
            var tokenKey = tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (accountTokens.ContainsKey(tokenKey))
                throw new InvalidOperationException("The daily login reward token already exists.");

            accountTokens[tokenKey] = new JsonObject
            {
                ["tokenID"] = tokenId,
                ["massTokenID"] = null,
                ["typeID"] = DailyLoginRewardTypeId,
                ["quantity"] = 1,
                ["stationID"] = 0,
                ["dateTime"] = Py.FileTimeFromUnixMs(nowUnix).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["expireDateTime"] = "0",
                ["label"] = "Daily Login Reward",
                ["description"] = "Daily login campaign reward",
                ["available"] = true,
                ["addedByContext"] = 0,
                ["addedByExtra"] = null,
            };
            redeemAccounts[accountKey] = accountTokens;
            loginAccounts[accountKey] = new JsonObject
            {
                ["day"] = rewardIndex,
                ["lastClaimReset"] = resetId,
            };

            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, LoginCampaignTable,
                [new RowUpsert(LoginCampaignRow, loginAccounts.ToJsonString())], [], ownerFence, cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, redeemAccounts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            sessions.Of(player.CharacterId!.Value)?.SendNotification("OnRedeemingQueueUpdated", "clientID");
            var claimedProgress = LoginCampaignItemProgress(rewardIndex, resetId, now);
            return new Eve.Services.Core.LoginCampaignClaimResult(
                LoginCampaignReward(rewardIndex),
                claimedProgress,
                PyNone.Instance,
                LoginCampaignBucketProgress(rewardIndex));
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<Eve.Services.Core.SeasonalLoginCampaignSnapshot?> GetSeasonalLoginCampaignAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<Eve.Services.Core.SeasonalLoginCampaignSnapshot?>(null);

    public ValueTask<bool> TryClaimSeasonalLoginCampaignAsync(
        Eve.Services.Core.SeasonalLoginCampaignClaimRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);

    private async ValueTask<bool> HasLoginRewardCharacterAsync(
        Eve.Services.Core.LoginRewardPlayer player,
        CancellationToken cancellationToken)
    {
        if (player.UserId <= 0 || player.UserId > int.MaxValue ||
            player.CharacterId is not { } characterId || characterId <= 0)
            return false;
        var character = await characters.GetAsync(characterId, cancellationToken);
        return character is not null && character.AccountId == (int)player.UserId;
    }

    private static (int Day, string LastClaimReset) LoginCampaignProgress(JsonObject? state)
    {
        var storedDay = Integer(state?["day"]);
        var day = storedDay is >= 0 and <= DailyLoginTrackLength ? (int)storedDay : 0;
        return (day, String(state?["lastClaimReset"]));
    }

    private static DateTimeOffset LoginCampaignResetStart(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var reset = new DateTimeOffset(
            utc.Year, utc.Month, utc.Day, DailyLoginResetHourUtc, 0, 0, TimeSpan.Zero);
        return utc < reset ? reset.AddDays(-1) : reset;
    }

    private static string LoginCampaignResetId(DateTimeOffset value) =>
        value.ToUniversalTime().AddHours(-DailyLoginResetHourUtc)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static PyValue LoginCampaignClientState(
        int day,
        string lastClaimReset,
        DateTimeOffset now) =>
        LoginCampaignKeyVal(
            ("static_data", LoginCampaignStaticData()),
            ("bucket_progress", LoginCampaignBucketProgress(day)),
            ("item_progress", LoginCampaignItemProgress(day, lastClaimReset, now)));

    private static PyValue LoginCampaignStaticData()
    {
        var rewards = new List<KeyValuePair<PyValue, PyValue>>(DailyLoginTrackLength);
        for (var day = 1; day <= DailyLoginTrackLength; day++)
            rewards.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(day), LoginCampaignReward(day)));
        return LoginCampaignKeyVal(
            ("campaign_duration", new PyInt(DailyLoginTrackLength)),
            ("subtitleMessageID", new PyInt(598039)),
            ("campaign_id", new PyInt(DailyLoginCampaignId)),
            ("titleMessageID", new PyInt(598040)),
            ("is_rookie_campaign", PyBool.True),
            ("windowHeaderBackgroundNarrow", new PyStr("res:/UI/Texture/classes/LoginCampaign/backgrounds/dliBackground.png")),
            ("windowHeaderBackgroundWide", new PyStr("res:/UI/Texture/classes/LoginCampaign/backgrounds/dliBackground.png")),
            ("item_rewards_by_day", new PyDict(rewards)));
    }

    private static PyValue LoginCampaignReward(int day) =>
        LoginCampaignKeyVal(
            ("typeID", new PyInt(DailyLoginRewardTypeId)),
            ("blueprintProductivityLevel", PyNone.Instance),
            ("blueprintMaterialLevel", PyNone.Instance),
            ("labelMessageID", new PyInt(598041 + day - 1)),
            ("tier", new PyInt(day == DailyLoginTrackLength ? 4 : 1)),
            ("quantity", new PyInt(1)),
            ("icon", PyNone.Instance));

    private static PyValue LoginCampaignBucketProgress(int day) =>
        LoginCampaignKeyVal(
            ("bucketID", new PyInt(DailyLoginBucketId)),
            ("fill_level", new PyReal((double)day / DailyLoginTrackLength)),
            ("next_fill_level", new PyReal((double)Math.Min(day + 1, DailyLoginTrackLength) / DailyLoginTrackLength)));

    private static PyValue LoginCampaignItemProgress(int day, string lastClaimReset, DateTimeOffset now)
    {
        var resetStart = LoginCampaignResetStart(now);
        var claimedToday = lastClaimReset == LoginCampaignResetId(now);
        var nextRewardTime = claimedToday ? resetStart.AddDays(1) : resetStart;
        var nextRewardIndex = day >= DailyLoginTrackLength ? 1 : day + 1;
        return LoginCampaignKeyVal(
            ("next_reward_index", new PyInt(nextRewardIndex)),
            ("num_claimed_rewards", new PyInt(day)),
            ("next_reward_timestamp", new PyLong(Py.FileTimeFromUnixMs(nextRewardTime.ToUnixTimeMilliseconds()))));
    }

    private static PyValue LoginCampaignKeyVal(params (string Name, PyValue Value)[] entries) =>
        Eve.Services.Py.KeyVal(entries.Select(entry =>
            new KeyValuePair<PyValue, PyValue>(new PyStr(entry.Name), entry.Value)));

    public async ValueTask<long> GetPlexBalanceAsync(long authenticatedUserId, CancellationToken cancellationToken)
    {
        var accountCharacters = await characters.ForAccountAsync(checked((int)authenticatedUserId), cancellationToken);
        var balances = await plexWallets.ReadPlexBalancesAsync(
            accountCharacters.Select(character => character.Id).ToArray(), cancellationToken);
        return balances.Values.Aggregate(0L, (sum, balance) => checked(sum + balance));
    }

    public async ValueTask<PyValue> GetRedeemTokensAsync(
    long authenticatedUserId,
    CancellationToken cancellationToken)
{
    RequireAccount(authenticatedUserId);
    var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
    var rows = (state[RedeemRow] as JsonObject)?[
        authenticatedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
    if (rows is null)
        return new PyList([]);
    return new PyList([.. rows
        .Where(entry =>
            long.TryParse(entry.Key, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _) &&
            entry.Value is JsonObject row && String(row["status"]) is "" or "pending")
        .OrderBy(entry => Integer((entry.Value as JsonObject)?["dateTime"]))
        .Select(entry => ToRedeemToken((JsonObject)entry.Value!, entry.Key))]);
}

    public async ValueTask<PyValue> CreateRedeemTokenQAAsync(ServiceCall call, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var accountId = RedeemQaPositiveInteger(call.Kwarg("userID"));
        if (accountId <= 0)
            accountId = call.Session.UserId ?? 0;
        var typeId = RedeemQaPositiveInteger(call.Kwarg("typeID"));
        if (accountId is <= 0 or > int.MaxValue || typeId is <= 0 or > int.MaxValue)
            return PyBool.False;
        RequireAccount(accountId);

        var quantityNumber = call.Kwarg("quantity") is { } requestedQuantity
            ? Eve.Macho.JsSemantics.ToNumber(requestedQuantity)
            : 1;
        var quantity = !double.IsFinite(quantityNumber) || quantityNumber == 0
            ? 1
            : Math.Max(1, Math.Truncate(quantityNumber));
        var quantityLong = RedeemQaBoundedInteger(quantity);
        var blueprintRuns = Math.Max(0, RedeemQaInteger(call.Kwarg("blueprintRuns")));
        var materialLevel = RedeemQaInteger(call.Kwarg("blueprintMaterialLevel"));
        var productivityLevel = RedeemQaInteger(call.Kwarg("blueprintProductivityLevel"));
        var soulboundValue = call.Kwarg("soulbound");
        var soulbound = soulboundValue is not null && Eve.Macho.JsSemantics.Truthy(soulboundValue);
        var availableValue = call.Kwarg("available");
        var available = availableValue is null or PyNone || Eve.Macho.JsSemantics.Truthy(availableValue);
        var labelValue = call.Kwarg("label");
        var label = labelValue is not null && Eve.Macho.JsSemantics.Truthy(labelValue)
            ? RedeemQaText(labelValue)
            : string.Empty;
        var descriptionValue = call.Kwarg("description");
        var description = descriptionValue is not null && Eve.Macho.JsSemantics.Truthy(descriptionValue)
            ? RedeemQaText(descriptionValue)
            : string.Empty;
        var expiry = RedeemQaFileTime(call.Kwarg("expiry"));
        var addedByContext = RedeemQaInteger(call.Kwarg("addedByContext"));
        var addedByExtra = RedeemQaJson(call.Kwarg("addedByExtra"));
        var nowFileTime = ToFileTime(nowUnixMs());
        var accountKey = accountId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var typeKey = typeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken)
                .ConfigureAwait(false);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();
            var maximumId = MaximumRedeemTokenId(accountTokens);
            var tokenId = maximumId >= 700_000_000
                ? checked(maximumId + 1)
                : 700_000_000 + Math.Abs(nowUnixMs() % 100_000_000);
            var tokenKey = tokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var isAutoInject = soulbound || typeId is 52996 or 73277 or 54652 or 54653 or 63303 or 63304 or
                54650 or 63305 or 63306 or 63307 or 63308 or 54654 or 63309;
            var token = new JsonObject
            {
                ["tokenID"] = tokenId,
                ["massTokenID"] = tokenId,
                ["typeID"] = checked((int)typeId),
                ["quantity"] = quantityLong,
                ["stationID"] = 0,
                ["dateTime"] = nowFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["expireDateTime"] = expiry.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["availableDateTime"] = nowFileTime.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["label"] = label,
                ["description"] = description,
                ["blueprintRuns"] = blueprintRuns,
                ["blueprintMaterialLevel"] = materialLevel,
                ["blueprintProductivityLevel"] = productivityLevel,
                ["soulbound"] = soulbound,
                ["available"] = available,
                ["addedByContext"] = addedByContext,
                ["addedByExtra"] = addedByExtra,
                ["isAutoInject"] = isAutoInject,
                ["status"] = "pending",
            };
            accountTokens[tokenKey] = token;
            accounts[accountKey] = accountTokens;

            await using var connection = await source.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var blueprintInfo = blueprintRuns > 0
                ? LoginCampaignKeyVal(
                    ("isCopy", PyBool.True),
                    ("runs", Py.Number(blueprintRuns)),
                    ("materialEfficiency", Py.Number(materialLevel)),
                    ("timeEfficiency", Py.Number(productivityLevel)))
                : PyNone.Instance;
            return LoginCampaignKeyVal(
                ("tokenID", Py.Number(tokenId)),
                ("massTokenID", Py.Number(tokenId)),
                ("typeID", Py.Number(typeId)),
                ("quantity", Py.Number(quantityLong)),
                ("stationID", new PyInt(0)),
                ("dateTime", new PyLong(nowFileTime)),
                ("expireDateTime", Py.Number(expiry)),
                ("label", new PyStr(label)),
                ("description", new PyStr(description)),
                ("blueprintRuns", Py.Number(blueprintRuns)),
                ("blueprintInfo", blueprintInfo),
                ("soulbound", soulbound ? PyBool.True : PyBool.False),
                ("isAutoInject", isAutoInject ? PyBool.True : PyBool.False));
        }
        finally
        {
            _gate.Release();
        }
    }

    private static long MaximumRedeemTokenId(JsonObject tokens)
    {
        var maximum = 0L;
        foreach (var (key, value) in tokens)
        {
            if (long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var tokenKey))
                maximum = Math.Max(maximum, tokenKey);
            if (value is JsonObject token)
            {
                maximum = Math.Max(maximum, Integer(token["tokenID"]));
                maximum = Math.Max(maximum, Integer(token["massTokenID"]));
            }
            if (key == "claimed" && value is JsonObject claimed)
            {
                foreach (var (claimedKey, claimedValue) in claimed)
                {
                    if (long.TryParse(claimedKey, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var claimedId))
                        maximum = Math.Max(maximum, claimedId);
                    if (claimedValue is JsonObject claimedToken)
                    {
                        maximum = Math.Max(maximum, Integer(claimedToken["tokenID"]));
                        maximum = Math.Max(maximum, Integer(claimedToken["massTokenID"]));
                    }
                }
            }
        }
        return maximum;
    }

    private static long RedeemQaPositiveInteger(PyValue? value)
    {
        var number = value is null ? double.NaN : Eve.Macho.JsSemantics.ToNumber(value);
        return double.IsFinite(number) && number > 0 && number == Math.Truncate(number) &&
            number < 9_223_372_036_854_775_808d ? (long)number : 0;
    }

    private static long RedeemQaInteger(PyValue? value)
    {
        var number = value is null ? 0 : Eve.Macho.JsSemantics.ToNumber(value);
        return double.IsFinite(number) ? RedeemQaBoundedInteger(Math.Truncate(number)) : 0;
    }

    private static long RedeemQaBoundedInteger(double number) =>
        number >= 9_223_372_036_854_775_808d ? long.MaxValue :
        number <= -9_223_372_036_854_775_808d ? long.MinValue : (long)number;

    private static long RedeemQaFileTime(PyValue? value)
    {
        if (value is null or PyNone)
            return 0;
        if (RedeemQaField(value, "value") is { } wrapped)
            return RedeemQaFileTime(wrapped);
        if (value is PyInt or PyLong or PyReal)
        {
            var number = Eve.Macho.JsSemantics.ToNumber(value);
            return double.IsFinite(number) && number > 0 ? RedeemQaBoundedInteger(Math.Truncate(number)) : 0;
        }
        var text = RedeemQaText(value).Trim();
        return long.TryParse(text, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static PyValue? RedeemQaField(PyValue source, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = source switch
        {
            PyDict dict => dict.Entries,
            PyObject { Args: PyDict dict } => dict.Entries,
            PyObjectEx objectEx => objectEx.Dict,
            _ => [],
        };
        return entries.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == name).Value;
    }

    private static string RedeemQaText(PyValue? value) => value switch
    {
        null or PyNone => string.Empty,
        PyStr text => text.Value,
        PyWString text => text.Value,
        PyBytes text => System.Text.Encoding.UTF8.GetString(text.Value.Span),
        PyBuffer buffer => System.Text.Encoding.UTF8.GetString(buffer.Value.Span),
        _ => Eve.Macho.JsSemantics.StringValue(value) ?? string.Empty,
    };

    private static JsonNode? RedeemQaJson(PyValue? value) => value switch
    {
        null or PyNone => null,
        PyBool boolean => JsonValue.Create(boolean == PyBool.True),
        PyInt number => JsonValue.Create(number.Value),
        PyLong number => JsonValue.Create(number.Value),
        PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        PyReal => null,
        PyStr text => JsonValue.Create(text.Value),
        PyWString text => JsonValue.Create(text.Value),
        PyToken text => JsonValue.Create(text.Value),
        PyBytes bytes => JsonValue.Create(System.Text.Encoding.UTF8.GetString(bytes.Value.Span)),
        PyBuffer buffer => JsonValue.Create(System.Text.Encoding.UTF8.GetString(buffer.Value.Span)),
        PyList list => new JsonArray(list.Items.Select(RedeemQaJson).ToArray()),
        PyTuple tuple => new JsonArray(tuple.Items.Select(RedeemQaJson).ToArray()),
        PyDict dict => RedeemQaJson(dict.Entries),
        PyObject { Args: PyDict dict } => RedeemQaJson(dict.Entries),
        PyObjectEx { Dict.Count: > 0 } objectEx => RedeemQaJson(objectEx.Dict),
        PyObjectEx { List.Count: > 0 } objectEx => new JsonArray(objectEx.List.Select(RedeemQaJson).ToArray()),
        _ => null,
    };

    private static JsonObject RedeemQaJson(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries)
        {
            var name = Eve.Macho.JsSemantics.StringValue(key);
            if (name is not null)
                result[name] = RedeemQaJson(value);
        }
        return result;
    }

    public async ValueTask<PyValue> ClaimRedeemTokensAsync(
        long authenticatedUserId,
        PyValue tokens,
        long characterId,
        bool useHomeStation,
        CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        var character = await characters.GetAsync(characterId, cancellationToken);
        if (character is null || character.AccountId != checked((int)authenticatedUserId))
            throw new UnauthorizedAccessException("The requested character does not belong to this account.");
        var stationId = useHomeStation
            ? character.HomeStationId
            : sessions.Of(characterId)?.GetInt("stationid") ?? sessions.Of(characterId)?.GetInt("structureid") ?? 0;
        if (stationId <= 0)
            throw new InvalidOperationException("Redeem items can only be claimed at a station or structure.");
        var requested = TokenPairs(tokens);
        if (requested.Length == 0)
            return RedeemResult(stationId, []);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountKey = authenticatedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();
            var claimed = accountTokens["claimed"] as JsonObject ?? new JsonObject();
            var pending = new List<(long TokenId, JsonObject Token, ItemType Type)>();
            var replayItems = new List<(long ItemId, int TypeId, long Quantity)>();
            foreach (var pair in requested.Distinct())
            {
                var key = pair.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (claimed[key] is JsonObject prior)
                {
                    foreach (var item in prior["itemsCreated"] as JsonArray ?? [])
                        if (item is JsonObject row)
                            replayItems.Add((Integer(row["itemID"]), checked((int)Integer(row["typeID"])), Integer(row["quantity"])));
                    continue;
                }
                if (accountTokens[key] is not JsonObject token ||
                    String(token["status"]) is not ("" or "pending") ||
                    (pair.MassTokenId > 0 && Integer(token["massTokenID"]) != pair.MassTokenId))
                    throw new InvalidOperationException("A redeem token is unavailable or does not belong to this account.");
                var typeId = checked((int)Integer(token["typeID"]));
                var type = await itemTypes.GetAsync(typeId, cancellationToken);
                if (type is null || Integer(token["quantity"]) <= 0)
                    throw new InvalidOperationException("A redeem token contains an invalid item.");
                pending.Add((pair.TokenId, token, type));
            }

            var newCreated = new List<(long ItemId, int TypeId, long Quantity)>();
            await using (var connection = await source.OpenConnectionAsync(cancellationToken))
            await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (var entry in pending)
                {
                    var quantity = Integer(entry.Token["quantity"]);
                    var singleton = entry.Type.CategoryId is 6 or 9;
                    var itemCount = singleton ? quantity : 1;
                    var stackQuantity = singleton ? 1 : quantity;
                    var tokenItems = new JsonArray();
                    for (var index = 0L; index < itemCount; index++)
                    {
                        var itemId = await items.CreateAsync(
                            connection, transaction,
                            new NewItem(entry.Type.TypeId, characterId, stationId, Eve.Inventory.ItemFlags.Hangar,
                                stackQuantity, singleton),
                            cancellationToken);
                        newCreated.Add((itemId, entry.Type.TypeId, stackQuantity));
                        tokenItems.Add(new JsonObject
                        {
                            ["itemID"] = itemId,
                            ["typeID"] = entry.Type.TypeId,
                            ["quantity"] = stackQuantity,
                        });
                    }
                    claimed[entry.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new JsonObject
                    {
                        ["stationID"] = stationId,
                        ["itemsCreated"] = tokenItems,
                        ["claimedAt"] = ToFileTime(nowUnixMs()),
                    };
                    accountTokens.Remove(entry.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                accountTokens["claimed"] = claimed;
                accounts[accountKey] = accountTokens;
                await store.ApplyChangesAsync(
                    connection, transaction, RedeemTable,
                    [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            return RedeemResult(stationId, [.. replayItems, .. newCreated]);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask ReverseRedeemAsync(
        long authenticatedUserId,
        PyValue itemId,
        CancellationToken cancellationToken)
    {
        await MutatePendingTokensAsync(authenticatedUserId, [new TokenPair(Positive(itemId), 0)], "reversed", cancellationToken);
    }

    public ValueTask TrashRedeemTokensAsync(
        long authenticatedUserId,
        PyValue tokens,
        CancellationToken cancellationToken) =>
        MutatePendingTokensAsync(authenticatedUserId, TokenPairs(tokens), "trashed", cancellationToken);

    private async ValueTask MutatePendingTokensAsync(
        long authenticatedUserId,
        IReadOnlyList<TokenPair> requested,
        string status,
        CancellationToken cancellationToken)
    {
        RequireAccount(authenticatedUserId);
        if (requested.Count == 0)
            return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = await PersistenceReadHelpers.ReadOptionalTableAsync(store, RedeemTable, cancellationToken);
            var accounts = state[RedeemRow] as JsonObject ?? new JsonObject();
            var accountKey = authenticatedUserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var accountTokens = accounts[accountKey] as JsonObject ?? new JsonObject();
            var changed = false;
            foreach (var pair in requested.Distinct())
            {
                var key = pair.TokenId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (accountTokens[key] is not JsonObject token || String(token["status"]) is not ("" or "pending"))
                    continue;
                token["status"] = status;
                token["updatedAt"] = ToFileTime(nowUnixMs());
                changed = true;
            }
            if (!changed)
                return;
            accounts[accountKey] = accountTokens;
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, RedeemTable,
                [new RowUpsert(RedeemRow, accounts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static TokenPair[] TokenPairs(PyValue value)
    {
        if (value is not PyList list)
            return [];
        var result = new List<TokenPair>();
        foreach (var item in list.Items)
        {
            if (item is PyTuple tuple && tuple.Items.Count >= 2)
            {
                var tokenId = Positive(tuple.Items[0]);
                var massTokenId = PositiveOrZero(tuple.Items[1]);
                if (tokenId > 0)
                    result.Add(new TokenPair(tokenId, massTokenId));
            }
            else if (item is PyDict dict)
            {
                var tokenId = Positive(Get(dict, "tokenID"));
                var massTokenId = PositiveOrZero(Get(dict, "massTokenID"));
                if (tokenId > 0)
                    result.Add(new TokenPair(tokenId, massTokenId));
            }
            else
            {
                var tokenId = Positive(item);
                if (tokenId > 0)
                    result.Add(new TokenPair(tokenId, 0));
            }
        }
        return [.. result];
    }

    private static PyValue ToRedeemToken(JsonObject row, string tokenKey) =>
        Py.Dict(
            ("tokenID", new PyLong(Integer(row["tokenID"], long.TryParse(tokenKey, out var key) ? key : 0))),
            ("massTokenID", Integer(row["massTokenID"]) > 0 ? new PyLong(Integer(row["massTokenID"])) : PyNone.Instance),
            ("typeID", new PyInt(checked((int)Integer(row["typeID"])))),
            ("quantity", new PyLong(Integer(row["quantity"]))),
            ("stationID", Integer(row["stationID"]) > 0 ? new PyLong(Integer(row["stationID"])) : PyNone.Instance),
            ("expireDateTime", Integer(row["expireDateTime"]) > 0 ? new PyLong(Integer(row["expireDateTime"])) : PyNone.Instance),
            ("dateTime", new PyLong(Integer(row["dateTime"]))),
            ("description", new PyStr(String(row["description"]))),
            ("isAutoInject", row["isAutoInject"] is JsonValue autoInject && autoInject.TryGetValue<bool>(out var enabled) && enabled
                ? PyBool.True : PyBool.False));

    private static PyDict RedeemResult(
        long stationId,
        IReadOnlyList<(long ItemId, int TypeId, long Quantity)> itemsCreated) =>
        Py.Dict(
            ("errors", new PyList([])),
            ("stationID", new PyLong(stationId)),
            ("itemsCreated", new PyList([.. itemsCreated.Select(item => Py.Dict(
                ("itemID", new PyLong(item.ItemId)),
                ("typeID", new PyInt(item.TypeId)),
                ("quantity", new PyLong(item.Quantity))))])));

    private static JsonObject? FindOffer(JsonArray? offers, PyValue requested)
    {
        var raw = Text(requested).Trim();
        if (raw.Length == 0)
            return null;
        var lookup = raw.StartsWith("VEVC-", StringComparison.OrdinalIgnoreCase) ? raw[5..] : raw;
        foreach (var offer in offers?.OfType<JsonObject>() ?? [])
        {
            var id = String(offer["id"]);
            var storeOfferId = String(offer["storeOfferID"]);
            if (id == lookup || storeOfferId.Equals(lookup, StringComparison.OrdinalIgnoreCase))
                return offer;
        }
        return null;
    }

    private static decimal? PriceFor(JsonObject offer, string currency)
    {
        foreach (var pricing in (offer["offerPricings"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (!String(pricing["currency"]).Equals(currency, StringComparison.OrdinalIgnoreCase))
                continue;
            if (decimal.TryParse(pricing["price"]?.ToString(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var price) && price > 0)
                return price;
        }
        return null;
    }

    private static bool IsFiat(string currency) =>
        currency is "CNY" or "JPY" or "EUR" or "GBP" or "USD" or "RUB";

    private static long TokenId(string operationKey, int productIndex)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{operationKey}:{productIndex}"));
        var value = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(bytes) & long.MaxValue;
        return value == 0 ? 1 : value;
    }

    private static long ToFileTime(long unixMilliseconds) =>
        checked(116444736000000000L + unixMilliseconds * 10_000L);

    private static void RequireAccount(long accountId)
    {
        if (accountId <= 0 || accountId > int.MaxValue)
            throw new UnauthorizedAccessException("A valid authenticated account is required.");
    }

    private static PyValue Get(PyDict dict, string key) =>
        dict.Entries.FirstOrDefault(entry => entry.Key is PyStr name && name.Value == key).Value ?? PyNone.Instance;

    private static long Positive(PyValue? value)
{
    var number = value switch
    {
        PyInt integer => (double)integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        _ => double.NaN,
    };
    return double.IsFinite(number) && number > 0 && number == Math.Truncate(number) && number < long.MaxValue
        ? (long)number
        : 0;
}

    private static long PositiveOrZero(PyValue? value)
{
    if (value is PyNone)
        return 0;
    var number = value switch
    {
        PyInt integer => (double)integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        _ => double.NaN,
    };
    return double.IsFinite(number) && number >= 0 && number == Math.Truncate(number) && number < long.MaxValue
        ? (long)number
        : 0;
}

    private static string Text(PyValue? value) => value switch
    {
        PyStr text => text.Value,
        PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    private static long Integer(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

    private static string String(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToString() ?? string.Empty;

    private readonly record struct TokenPair(long TokenId, long MassTokenId);
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


sealed class PersistenceInsuranceWriteModel(
    NpgsqlDataSource source,
    PgItemStore items,
    PersistenceStore store,
    OwnerFence ownerFence,
    WalletTransactions wallets,
    Eve.Services.Core.IInsuranceReadModel readModel,
    Func<long> nowUnixMs) : Eve.Services.Core.IInsuranceWriteModel, Eve.Inventory.IShipInsuranceInvalidator
{
    private const string Table = "insuranceContracts";
    private const string ContractsRow = "contractsByShipID";
    private const string SettlementsRow = "settlementsByLossID";
    private const int InsuranceRefType = 0;
    // ponytail: serialize the single GameStore contract document; shard rows if claim volume requires concurrency.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> InsureShipAsync(
        Eve.Services.Core.InsurancePurchaseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ShipId <= 0 || command.PremiumCents <= 0 ||
            command.StartDateFileTime <= 0 || command.EndDateFileTime <= command.StartDateFileTime ||
            !command.Actor.IsDocked || command.Actor.CharacterId <= 0 ||
            (command.IsCorporation && !CanInsureForCorporation(command.Actor)))
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ship = await items.LockAndGetAsync(connection, transaction, command.ShipId, cancellationToken);
            var ownerKind = command.IsCorporation
                ? Eve.Services.Core.InsuranceOwnerKind.Corporation
                : Eve.Services.Core.InsuranceOwnerKind.Character;
            var ownerId = command.IsCorporation ? command.Actor.CorporationId : command.Actor.CharacterId;
            if (ship is null || ship.OwnerId != ownerId || !AtActorLocation(ship.LocationId, command.Actor) ||
                !CorrectHangar(command.IsCorporation, ship.Flag))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible, ownerId);

            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var prices = await readModel.GetInsurancePricesCentsAsync([ship.TypeId], cancellationToken);
            if (!prices.TryGetValue(ship.TypeId, out var fullPayoutCents) || fullPayoutCents <= 0 ||
                Eve.Services.Core.InsuranceQuotes.ForPremium(fullPayoutCents, command.PremiumCents) is not { } quote)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible, ownerId);

            var state = await store.LoadTableAsync(Table, ct: cancellationToken);
            var contracts = state[ContractsRow] as JsonObject ?? new JsonObject();
            var shipKey = Key(command.ShipId);
            var current = contracts[shipKey] as JsonObject;
            var currentContract = Contract(current, command.ShipId);
            if (ActiveAt(currentContract, command.StartDateFileTime))
            {
                if (!command.ReplaceExisting)
                    return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyInsured, currentContract!.OwnerId);
                current!["status"] = "void";
            }

            var contractId = NextContractId(contracts);
            if (contractId <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible, ownerId);
            contracts[shipKey] = new JsonObject
            {
                ["contractID"] = contractId,
                ["shipID"] = command.ShipId,
                ["typeID"] = ship.TypeId,
                ["ownerID"] = ownerId,
                ["ownerKind"] = ownerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation ? "corporation" : "character",
                ["fraction"] = quote.FractionPercent / 100d,
                ["startDate"] = command.StartDateFileTime,
                ["endDate"] = command.EndDateFileTime,
                ["status"] = "active",
            };

            var balance = await wallets.AdjustAsync(
                connection, transaction, ownerId, -command.PremiumCents / 100m, 0,
                InsuranceRefType, "Insurance premium", nowUnixMs(),
                $"insurance:premium:{command.ShipId}:{contractId}", cancellationToken);
            await store.ApplyChangesAsync(
                connection, transaction, Table,
                [new RowUpsert(ContractsRow, contracts.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            wallets.AnnounceCommitted(new WalletBalanceChange(ownerId, 1000, balance));
            return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, ownerId, command.PremiumCents);
        }
        catch (Exception error) when (error is Eve.Wallet.InsufficientFundsException or
                                      Eve.Wallet.WalletNotFoundException or
                                      Eve.Wallet.MoneyLimitException or
                                      Eve.Wallet.WalletOperationConflictException)
        {
            return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> UnInsureShipAsync(
        Eve.Services.Core.InsuranceCancellationCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ShipId <= 0 || command.RequestedAtFileTime <= 0 || !command.Actor.IsDocked || command.Actor.CharacterId <= 0)
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ship = await items.LockAndGetAsync(connection, transaction, command.ShipId, cancellationToken);
            if (ship is null || !AtActorLocation(ship.LocationId, command.Actor))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);

            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var state = await store.LoadTableAsync(Table, ct: cancellationToken);
            var contracts = state[ContractsRow] as JsonObject;
            var key = Key(command.ShipId);
            if (contracts?[key] is not JsonObject row || Contract(row, command.ShipId) is not { } contract)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NoActiveContract);
            if (string.Equals(contract.Status, "void", StringComparison.OrdinalIgnoreCase))
                return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied, contract.OwnerId);
            if (!ActiveAt(contract, command.RequestedAtFileTime))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NoActiveContract, contract.OwnerId);
            if (contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Character &&
                contract.OwnerId != command.Actor.CharacterId ||
                contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation &&
                (contract.OwnerId != command.Actor.CorporationId || !CanInsureForCorporation(command.Actor)))
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotAuthorized, contract.OwnerId);

            row["status"] = "void";
            await store.ApplyChangesAsync(
                connection, transaction, Table,
                [new RowUpsert(ContractsRow, contracts!.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, contract.OwnerId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> SettleShipLossAsync(
        Eve.Services.Core.InsuranceLossSettlementRequest request,
        CancellationToken cancellationToken)
    {
        if (request.LossId <= 0 || request.ShipId <= 0 || request.LossDateFileTime <= 0)
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var ship = await items.LockAndGetAsync(connection, transaction, request.ShipId, cancellationToken);
            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var state = await store.LoadTableAsync(Table, ct: cancellationToken);
            var contracts = state[ContractsRow] as JsonObject ?? new JsonObject();
            var settlements = state[SettlementsRow] as JsonObject ?? new JsonObject();
            var lossKey = Key(request.LossId);
            if (settlements[lossKey] is JsonObject settled)
                return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied,
                    Number(settled["ownerID"]), Number(settled["amountCents"]));

            var shipKey = Key(request.ShipId);
            var contractRow = contracts[shipKey] as JsonObject;
            var contract = Contract(contractRow, request.ShipId);
            var typeId = ship?.TypeId ?? contract?.TypeId ?? 0;
            if (typeId <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);
            var prices = await readModel.GetInsurancePricesCentsAsync([typeId], cancellationToken);
            if (!prices.TryGetValue(typeId, out var fullPayoutCents) || fullPayoutCents <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);

            if (contract is not null && ship is not null &&
                (ship.OwnerId != contract.OwnerId ||
                 !CorrectHangar(contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation, ship.Flag)))
                contract = null;
            var payoutCents = Eve.Services.Core.InsuranceQuotes.PayoutForLoss(
                fullPayoutCents, contract, request.LossDateFileTime, request.IsConcordDestruction);
            var ownerId = ship?.OwnerId ?? contract?.OwnerId ??
                (request.FinalOwnerId > 0 ? request.FinalOwnerId : request.FinalOwnerCorporationId);
            if (payoutCents > 0 && ownerId <= 0)
                return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);

            decimal balance = 0;
            if (payoutCents > 0)
                balance = await wallets.AdjustAsync(
                    connection, transaction, ownerId, payoutCents / 100m, 0,
                    InsuranceRefType, "Insurance payout", nowUnixMs(),
                    $"insurance:payout:{request.LossId}", cancellationToken);

            if (contractRow is not null)
                contractRow["status"] = contract is not null && payoutCents > 0 ? "paid" : "void";
            settlements[lossKey] = new JsonObject
            {
                ["lossID"] = request.LossId,
                ["shipID"] = request.ShipId,
                ["ownerID"] = ownerId,
                ["amountCents"] = payoutCents,
                ["isConcordDestruction"] = request.IsConcordDestruction,
                ["settledAtFiletime"] = checked(116444736000000000L + nowUnixMs() * 10_000L),
                ["contractID"] = contract?.ContractId ?? 0,
            };
            await store.ApplyChangesAsync(
                connection, transaction, Table,
                [new RowUpsert(ContractsRow, contracts.ToJsonString()),
                 new RowUpsert(SettlementsRow, settlements.ToJsonString())], [], ownerFence, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (payoutCents > 0)
                wallets.AnnounceCommitted(new WalletBalanceChange(ownerId, 1000, balance));
            return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, ownerId, payoutCents);
        }
        catch (Exception error) when (error is Eve.Wallet.WalletNotFoundException or
                                      Eve.Wallet.MoneyLimitException or
                                      Eve.Wallet.WalletOperationConflictException)
        {
            return Result(Eve.Services.Core.InsuranceMutationStatus.NotEligible);
        }
        finally
        {
            _gate.Release();
        }
    }



    public async ValueTask InvalidateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long itemId,
        Eve.Inventory.ShipContractInvalidationReason reason,
        CancellationToken cancellationToken)
    {
        var insuranceReason = reason switch
        {
            Eve.Inventory.ShipContractInvalidationReason.Repackaged => Eve.Services.Core.InsuranceInvalidationReason.Repackaged,
            Eve.Inventory.ShipContractInvalidationReason.OwnerChanged => Eve.Services.Core.InsuranceInvalidationReason.OwnerChanged,
            Eve.Inventory.ShipContractInvalidationReason.CorporationHangar => Eve.Services.Core.InsuranceInvalidationReason.PlacedInCorporationHangar,
            _ => (Eve.Services.Core.InsuranceInvalidationReason?)null,
        };
        if (itemId <= 0 || insuranceReason is not { } mappedReason) return;

        await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
        await InvalidateShipInTransactionAsync(
            connection,
            transaction,
            new Eve.Services.Core.InsuranceInvalidationRequest(
                itemId, mappedReason, Py.FileTimeFromUnixMs(nowUnixMs())),
            cancellationToken);
    }

    private static async Task LockInsuranceContractsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        const long lockId = 0x455645494E535552L;
        await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock($1)", connection, transaction)
        {
            Parameters = { new() { Value = lockId } },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<Eve.Services.Core.InsuranceMutationResult> InvalidateShipInTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Eve.Services.Core.InsuranceInvalidationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ShipId <= 0 || request.InvalidatedAtFileTime <= 0 ||
            !Enum.IsDefined(request.Reason) ||
            (request.Reason == Eve.Services.Core.InsuranceInvalidationReason.Boarded &&
             (request.ActorCharacterId <= 0 || request.ActorCorporationId is null)))
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        var state = await store.LoadTableAsync(Table, ct: cancellationToken);
        var contracts = state[ContractsRow] as JsonObject;
        var key = Key(request.ShipId);
        if (contracts?[key] is not JsonObject row || Contract(row, request.ShipId) is not { } contract)
            return Result(Eve.Services.Core.InsuranceMutationStatus.NoActiveContract);
        if (!string.Equals(contract.Status, "active", StringComparison.OrdinalIgnoreCase))
            return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied, contract.OwnerId);

        var shouldInvalidate = request.Reason switch
        {
            Eve.Services.Core.InsuranceInvalidationReason.Repackaged => true,
            Eve.Services.Core.InsuranceInvalidationReason.OwnerChanged => true,
            Eve.Services.Core.InsuranceInvalidationReason.PlacedInCorporationHangar =>
                contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Character,
            Eve.Services.Core.InsuranceInvalidationReason.Boarded =>
                contract.OwnerKind == Eve.Services.Core.InsuranceOwnerKind.Corporation &&
                request.ActorCorporationId != contract.OwnerId,
            _ => false,
        };
        if (!shouldInvalidate)
            return Result(Eve.Services.Core.InsuranceMutationStatus.AlreadyApplied, contract.OwnerId);

        row["status"] = "void";
        row["invalidatedAt"] = request.InvalidatedAtFileTime;
        row["invalidationReason"] = request.Reason.ToString();
        await store.ApplyChangesAsync(
            connection, transaction, Table,
            [new RowUpsert(ContractsRow, contracts!.ToJsonString())], [], ownerFence, cancellationToken);
        return Result(Eve.Services.Core.InsuranceMutationStatus.Applied, contract.OwnerId);
    }

    public async ValueTask<Eve.Services.Core.InsuranceMutationResult> InvalidateShipAsync(
        Eve.Services.Core.InsuranceInvalidationRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ShipId <= 0 || request.InvalidatedAtFileTime <= 0 ||
            !Enum.IsDefined(request.Reason) ||
            (request.Reason == Eve.Services.Core.InsuranceInvalidationReason.Boarded &&
             (request.ActorCharacterId <= 0 || request.ActorCorporationId is null)))
            return Result(Eve.Services.Core.InsuranceMutationStatus.InvalidRequest);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await LockInsuranceContractsAsync(connection, transaction, cancellationToken);
            var result = await InvalidateShipInTransactionAsync(connection, transaction, request, cancellationToken);
            if (result.Status == Eve.Services.Core.InsuranceMutationStatus.Applied)
                await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Eve.Services.Core.InsuranceMutationResult Result(
        Eve.Services.Core.InsuranceMutationStatus status, long ownerId = 0, long amountCents = 0) =>
        new(status, ownerId, amountCents);

    private static bool CanInsureForCorporation(Eve.Services.Core.InsuranceActorSnapshot actor) =>
        actor.CorporationId > 0 &&
        (actor.CorporationRoleMask &
            (Eve.Corporations.CorporationRoles.Accountant | Eve.Corporations.CorporationRoles.JuniorAccountant)) != 0;

    private static bool AtActorLocation(long locationId, Eve.Services.Core.InsuranceActorSnapshot actor) =>
        locationId > 0 && (locationId == actor.StationId || locationId == actor.StructureId);


    private static bool CorrectHangar(bool corporationInsurance, int flag) =>
        Eve.Inventory.ItemFlags.IsCorporationHangar(flag) == corporationInsurance;

    private static long NextContractId(JsonObject contracts)
    {
        var highest = contracts.Select(pair => pair.Value as JsonObject)
            .Where(row => row is not null)
            .Select(row => Number(row!["contractID"]))
            .DefaultIfEmpty(0)
            .Max();
        return highest == long.MaxValue ? 0 : highest + 1;
    }

    private static Eve.Services.Core.InsuranceContractSnapshot? Contract(JsonObject? row, long fallbackShipId)
    {
        if (row is null)
            return null;
        var shipId = Number(row["shipID"]);
        if (shipId <= 0) shipId = fallbackShipId;
        var ownerKind = string.Equals(Text(row["ownerKind"]), "corporation", StringComparison.OrdinalIgnoreCase)
            ? Eve.Services.Core.InsuranceOwnerKind.Corporation
            : Eve.Services.Core.InsuranceOwnerKind.Character;
        var ownerId = Number(row["ownerID"]);
        var typeId = Number(row["typeID"]);
        if (shipId <= 0 || ownerId <= 0 || typeId <= 0)
            return null;
        return new Eve.Services.Core.InsuranceContractSnapshot(
            Number(row["contractID"]), shipId, typeId, ownerId, ownerKind,
            NumberDouble(row["fraction"]), Number(row["startDate"]), Number(row["endDate"]),
            Text(row["status"]) is { Length: > 0 } status ? status : "active");
    }

    private static bool ActiveAt(Eve.Services.Core.InsuranceContractSnapshot? contract, long fileTime) =>
        fileTime > 0 && contract is { ShipId: > 0, TypeId: > 0, OwnerId: > 0 } &&
        Enum.IsDefined(contract.OwnerKind) && contract.Fraction > 0 && contract.Fraction <= 1 &&
        string.Equals(contract.Status, "active", StringComparison.OrdinalIgnoreCase) &&
        contract.StartDateFileTime > 0 && contract.StartDateFileTime <= fileTime && contract.EndDateFileTime > fileTime;

    private static long Number(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static double NumberDouble(JsonNode? value) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number) && double.IsFinite(number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : "";

    private static string Key(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed class PersistenceStructureAssetSafetyReadModel : Eve.Services.Core.IStructureAssetSafetyReadCallbacks
{
    public const string DefaultSchema = "asset_safety";
    public const string DueJobKind = "structure_asset_safety.process_due";
    private const int AssetSafetyRecoveryTaxRefTypeId = 123; // ESI wallet ref type asset_safety_recovery_tax.
    private const long DayMs = 86_400_000L;
    private const int Stored = 0;
    private const int Delivered = 1;
    private const int Ejected = 2;
    private const short Personal = 1;
    private const short Corporation = 2;
    private const long DirectorRole = Eve.Corporations.CorporationRoles.Director;
    private const double HighSecurityMinimum = 0.45;
    private static readonly System.Collections.Generic.HashSet<string> AssetSafetyGroups = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Citadel",
        "Engineering Complex",
        "Refinery",
        "Observatory Array",
    };

    private readonly Npgsql.NpgsqlDataSource _source;
    private readonly Eve.Inventory.PgItemStore _items;
    private readonly Eve.Ships.PgSpaceItemStore _spaceItems;
    private readonly Eve.Wallet.WalletTransactions _wallets;
    private readonly Eve.Sde.SdeReader _sde;
    private readonly System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject>> _readTable;
    private readonly System.Func<long, System.Threading.CancellationToken, System.Threading.Tasks.Task<Eve.Relations.Affiliation>> _affiliationOf;
    private readonly System.Func<long> _now;
    private readonly string _inventorySchema;
    private readonly string _corporationSchema;
    private readonly string _schema;
    private readonly Eve.Services.Core.IStructureDirectoryReadCallbacks _structures;
    private readonly System.Action<string>? _log;
    private readonly System.Threading.SemaphoreSlim _catalogGate = new(1, 1);
    private StaticData? _catalog;

    public static System.Collections.Generic.IReadOnlyList<Eve.Data.Migration> MigrationSet() =>
    [
        new Eve.Data.Migration("0001_asset_safety", """
            CREATE TABLE asset_safety_wraps (
                wrap_id bigint PRIMARY KEY,
                scope smallint NOT NULL CHECK (scope IN (1, 2)),
                owner_id bigint NOT NULL CHECK (owner_id > 0),
                corporation_id bigint NOT NULL DEFAULT 0,
                source_structure_id bigint NOT NULL CHECK (source_structure_id > 0),
                source_system_id integer NOT NULL CHECK (source_system_id > 0),
                source_type_id integer NOT NULL CHECK (source_type_id > 0),
                source_name text NOT NULL,
                system_name text NOT NULL,
                wrap_name text NOT NULL,
                created_at_ms bigint NOT NULL,
                state smallint NOT NULL CHECK (state IN (0, 1, 2)),
                destination_id bigint,
                delivered_at_ms bigint
            );
            CREATE INDEX asset_safety_wraps_owner_state
                ON asset_safety_wraps (scope, owner_id, state, created_at_ms);
            CREATE INDEX asset_safety_wraps_due
                ON asset_safety_wraps (created_at_ms, wrap_id)
                WHERE state = 0;
            CREATE TABLE asset_safety_operation_receipts (
                operation_key text PRIMARY KEY,
                action text NOT NULL,
                wrap_id bigint NOT NULL DEFAULT 0,
                result_state integer NOT NULL,
                created_at_ms bigint NOT NULL
            );
            """),
    ];

    public static Task MigrateAsync(Npgsql.NpgsqlDataSource source, string schema = DefaultSchema) =>
        Eve.Data.Migrations.ApplyAsync(source, SchemaName(schema), MigrationSet());

    public PersistenceStructureAssetSafetyReadModel(
        Npgsql.NpgsqlDataSource source,
        Eve.Inventory.PgItemStore items,
        Eve.Ships.PgSpaceItemStore spaceItems,
        Eve.Wallet.WalletTransactions wallets,
        Eve.Sde.SdeReader sde,
        System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<System.Text.Json.Nodes.JsonObject>> readTable,
        System.Func<long, System.Threading.CancellationToken, System.Threading.Tasks.Task<Eve.Relations.Affiliation>> affiliationOf,
        System.Func<long> now,
        string inventorySchema,
        string corporationSchema,
        string schema,
        Eve.Services.Core.IStructureDirectoryReadCallbacks structures,
        System.Action<string>? log = null)
    {
        _source = source ?? throw new System.ArgumentNullException(nameof(source));
        _items = items ?? throw new System.ArgumentNullException(nameof(items));
        _spaceItems = spaceItems ?? throw new System.ArgumentNullException(nameof(spaceItems));
        _wallets = wallets ?? throw new System.ArgumentNullException(nameof(wallets));
        _sde = sde ?? throw new System.ArgumentNullException(nameof(sde));
        _readTable = readTable ?? throw new System.ArgumentNullException(nameof(readTable));
        _affiliationOf = affiliationOf ?? throw new System.ArgumentNullException(nameof(affiliationOf));
        _now = now ?? throw new System.ArgumentNullException(nameof(now));
        _inventorySchema = SchemaName(inventorySchema);
        _corporationSchema = SchemaName(corporationSchema);
        _schema = SchemaName(schema);
        _structures = structures ?? throw new System.ArgumentNullException(nameof(structures));
        _log = log;
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyList> GetItemsInSafetyForCharacterAsync(
        long characterId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return new Eve.Marshal.PyList([]);

        var data = await CatalogAsync(cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, source_system_id, wrap_name, created_at_ms
            FROM {_schema}.asset_safety_wraps
            WHERE scope = $1 AND owner_id = $2 AND state = $3
            ORDER BY created_at_ms, wrap_id
            """, connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Personal });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var rows = new System.Collections.Generic.List<Eve.Marshal.PyValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var station = NearestAutomaticStation(reader.GetInt32(1), data);
            rows.Add(WrapPayload(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), station));
        }
        return new Eve.Marshal.PyList(rows);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyList> GetItemsInSafetyForCorpAsync(
        long characterId,
        long corporationId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            return new Eve.Marshal.PyList([]);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        if (!await IsCorporationExecutiveAsync(connection, transaction, characterId, corporationId, cancellationToken))
        {
            await transaction.CommitAsync(cancellationToken);
            return new Eve.Marshal.PyList([]);
        }

        var data = await CatalogAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, source_system_id, wrap_name, created_at_ms
            FROM {_schema}.asset_safety_wraps
            WHERE scope = $1 AND owner_id = $2 AND state = $3
            ORDER BY created_at_ms, wrap_id
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Corporation });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var rows = new System.Collections.Generic.List<Eve.Marshal.PyValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var station = NearestAutomaticStation(reader.GetInt32(1), data);
            rows.Add(WrapPayload(reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetInt64(3), station));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return new Eve.Marshal.PyList(rows);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyDict> GetWrapNamesAsync(
        long characterId,
        long corporationId,
        System.Collections.Generic.IReadOnlyList<long> wrapIds,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || wrapIds.Count == 0)
            return new Eve.Marshal.PyDict([]);

        var mayReadCorp = corporationId > 0 && await IsCorporationExecutiveAsync(characterId, corporationId, cancellationToken);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, scope, owner_id, wrap_name
            FROM {_schema}.asset_safety_wraps
            WHERE wrap_id = ANY($1) AND state = $2
            ORDER BY wrap_id
            """, connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapIds.Distinct().ToArray() });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var entries = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var scope = reader.GetInt16(1);
            var owner = reader.GetInt64(2);
            var allowed = scope == Personal
                ? owner == characterId
                : scope == Corporation && owner == corporationId && mayReadCorp;
            if (allowed)
            {
                entries.Add(new(
                    new Eve.Marshal.PyLong(reader.GetInt64(0)),
                    new Eve.Marshal.PyStr(reader.GetString(3))));
            }
        }
        return new Eve.Marshal.PyDict(entries);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Marshal.PyTuple> GetStructuresICanDeliverToAsync(
        long characterId,
        long corporationId,
        long solarSystemId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || solarSystemId <= 0)
            return new Eve.Marshal.PyTuple([new Eve.Marshal.PyList([]), Eve.Marshal.PyNone.Instance]);

        var data = await CatalogAsync(cancellationToken);
        var destinations = new System.Collections.Generic.List<Eve.Marshal.PyValue>();
        foreach (var station in data.Stations.All.Where(s => s.SolarSystemId == solarSystemId).OrderBy(s => s.Id))
            destinations.Add(DestinationPayload(station.Id, station.TypeId, station.Name));

        var affiliation = await _affiliationOf(characterId, cancellationToken);
        if (corporationId > 0 && affiliation.Corporation == corporationId)
        {
            var rows = await ReadPersistedStructuresAsync(solarSystemId, data, cancellationToken);
            var snapshots = rows.Count == 0
                ? System.Array.Empty<Eve.Services.Core.StructureDockingAccessSnapshot>()
                : await _structures.GetDockingAccessSnapshotsAsync(
                    characterId, corporationId, affiliation.Alliance ?? 0, rows.Select(r => r.Id).ToArray(), cancellationToken);
            foreach (var row in rows)
            {
                var snapshot = snapshots.FirstOrDefault(s => s.StructureId == row.Id);
                if (snapshot is not null && AllowsDocking(snapshot, characterId, corporationId, affiliation.Alliance ?? 0))
                    destinations.Add(DestinationPayload(row.Id, row.TypeId, row.Name));
            }
        }

        return new Eve.Marshal.PyTuple([
            new Eve.Marshal.PyList(destinations),
            NearestStationPayload(NearestAutomaticStation((int)solarSystemId, data)),
        ]);
    }


    public async System.Threading.Tasks.ValueTask<bool> MoveEjectTimeGMAsync(
        Eve.Services.ServiceCall call,
        long assetWrapId,
        int days,
        System.Threading.CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const long roleGml = 1L << 54;
        if (assetWrapId <= 0 || days is not (5 or 30) ||
            ((call.Session?.GetInt("role") ?? 0) & roleGml) == 0)
            return false;

        var deltaMs = checked((long)days * DayMs);
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            UPDATE {_schema}.asset_safety_wraps
            SET created_at_ms = created_at_ms + $2
            WHERE wrap_id = $1 AND state = $3
            RETURNING wrap_id
            """,
            connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = assetWrapId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = deltaMs });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        var updated = await command.ExecuteScalarAsync(cancellationToken);
        return updated is not null && updated is not System.DBNull;
    }

    public System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> MovePersonalAssetsToSafetyAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long structureId,
        System.Threading.CancellationToken cancellationToken) =>
        TriggerAssetsAsync(call, solarSystemId, structureId, Personal, call.Session.CharacterId ?? 0, cancellationToken);

    public System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> MoveCorpAssetsToSafetyAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long structureId,
        System.Threading.CancellationToken cancellationToken)
    {
        var corporationId = call.Session.GetInt("corpid") ?? 0;
        if (corporationId <= 0)
            return System.Threading.Tasks.ValueTask.FromResult(Eve.Services.Core.AssetSafetyActionState.NotAuthorized);
        return TriggerAssetsAsync(call, solarSystemId, structureId, Corporation, corporationId, cancellationToken);
    }

    public async System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> MoveSafetyWrapToStructureAsync(
        Eve.Services.ServiceCall call,
        long assetWrapId,
        long solarSystemId,
        long destinationId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (assetWrapId <= 0 || solarSystemId <= 0 || destinationId <= 0 || call.Session.CharacterId is not { } characterId || characterId <= 0)
            return Eve.Services.Core.AssetSafetyActionState.InvalidDestination;

        var operationKey = RequestKey(call, "release", assetWrapId, solarSystemId, destinationId);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            var replay = await BeginReceiptAsync(connection, transaction, operationKey, "release", cancellationToken);
            if (replay is { } prior)
            {
                await transaction.CommitAsync(cancellationToken);
                return prior.State;
            }

            var wrap = await ReadWrapForUpdateAsync(connection, transaction, assetWrapId, cancellationToken);
            if (wrap is null)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.InvalidDestination, assetWrapId, cancellationToken);

            if (wrap.State == Delivered)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.WrapDelivered, assetWrapId, cancellationToken);
            if (wrap.State != Stored)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.InvalidDestination, assetWrapId, cancellationToken);

            if (wrap.Scope == Personal)
            {
                if (wrap.OwnerId != characterId)
                    return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NotAuthorized, assetWrapId, cancellationToken);
            }
            else if (wrap.Scope == Corporation)
            {
                if ((call.Session.GetInt("corpid") ?? 0) != wrap.OwnerId
                    || !await IsCorporationExecutiveAsync(connection, transaction, characterId, wrap.OwnerId, cancellationToken))
                    return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NotAuthorized, assetWrapId, cancellationToken);
            }
            else
            {
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.InvalidDestination, assetWrapId, cancellationToken);
            }

            if (wrap.SourceSystemId != solarSystemId)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.SolarSystemMismatch, assetWrapId, cancellationToken);
            if (_now() - wrap.CreatedAtMs < 5 * DayMs)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.DeliveryNotReady, assetWrapId, cancellationToken);

            var data = await CatalogAsync(cancellationToken);
            var target = await ValidateDestinationAsync(
                characterId, wrap.Scope == Corporation ? wrap.OwnerId : (call.Session.GetInt("corpid") ?? 0),
                destinationId, solarSystemId, data, cancellationToken);
            if (target.State != Eve.Services.Core.AssetSafetyActionState.WrapDelivered)
                return await FinishReceiptAsync(connection, transaction, operationKey, target.State, assetWrapId, cancellationToken);

            var contents = await LockItemTreeAsync(connection, transaction, assetWrapId, wrap.OwnerId, cancellationToken);
            var fee = await FeeAsync(contents, wrap.SourceSystemId == target.SystemId, cancellationToken);
            if (fee > 0)
            {
                await _wallets.AdjustAsync(
                    connection, transaction, wrap.OwnerId, -fee, destinationId, AssetSafetyRecoveryTaxRefTypeId,
                    "Asset Safety recovery", _now(), $"asset-safety-wrap:{assetWrapId}", cancellationToken, assetWrapId);
            }

            foreach (var item in contents.Where(i => i.LocationId == assetWrapId && i.OwnerId == wrap.OwnerId))
                await _items.MoveAsync(connection, transaction, item.Id, destinationId, item.Flag, cancellationToken);

            await _items.DeleteAsync(connection, transaction, assetWrapId, cancellationToken);
            await SetDeliveredAsync(connection, transaction, assetWrapId, destinationId, _now(), cancellationToken);
            return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.WrapDelivered, assetWrapId, cancellationToken);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"asset safety delivery for wrap {assetWrapId} failed: {error.Message}");
            return Eve.Services.Core.AssetSafetyActionState.PersistenceFailure;
        }
    }

    public async System.Threading.Tasks.ValueTask ProcessDueAutomaticDeliveriesAsync(
        System.Threading.CancellationToken cancellationToken)
    {
        var data = await CatalogAsync(cancellationToken);
        var now = _now();
        var due = new System.Collections.Generic.List<long>();
        await using (var connection = await _source.OpenConnectionAsync(cancellationToken))
        await using (var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id
            FROM {_schema}.asset_safety_wraps
            WHERE state = $1 AND created_at_ms <= $2
            ORDER BY created_at_ms, wrap_id
            """, connection))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = now - 20 * DayMs });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                due.Add(reader.GetInt64(0));
        }

        foreach (var wrapId in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DeliverAutomaticallyAsync(wrapId, data, cancellationToken);
            }
            catch (System.OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                _log?.Invoke($"automatic asset safety delivery for wrap {wrapId} failed: {error.Message}");
            }
        }
    }

    private async System.Threading.Tasks.ValueTask<Eve.Services.Core.AssetSafetyActionState> TriggerAssetsAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long structureId,
        short scope,
        long ownerId,
        System.Threading.CancellationToken cancellationToken)
    {
        var characterId = call.Session.CharacterId ?? 0;
        var corporationId = scope == Corporation ? ownerId : call.Session.GetInt("corpid") ?? 0;
        if (characterId <= 0 || ownerId <= 0 || solarSystemId <= 0 || structureId <= 0)
            return Eve.Services.Core.AssetSafetyActionState.NotAuthorized;
        if (scope == Corporation && corporationId != (call.Session.GetInt("corpid") ?? 0))
            return Eve.Services.Core.AssetSafetyActionState.NotAuthorized;

        var operationKey = RequestKey(call, scope == Corporation ? "corp-trigger" : "personal-trigger", structureId, solarSystemId, ownerId);
        try
        {
            await using var connection = await _source.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
            var replay = await BeginReceiptAsync(connection, transaction, operationKey, scope == Corporation ? "corp-trigger" : "personal-trigger", cancellationToken);
            if (replay is { } prior)
            {
                await transaction.CommitAsync(cancellationToken);
                return prior.State;
            }

            if (scope == Corporation
                && !await IsCorporationExecutiveAsync(connection, transaction, characterId, corporationId, cancellationToken))
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NotAuthorized, 0, cancellationToken);

            var data = await CatalogAsync(cancellationToken);
            var source = await ReadPersistedStructureAsync(structureId, data, cancellationToken);
            if (source is null)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.StructureUnavailable, 0, cancellationToken);
            if (source.SystemId != solarSystemId)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.SolarSystemMismatch, 0, cancellationToken);
            if (!IsStandardUpwell(source.TypeId, data))
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.AssetSafetyUnavailable, 0, cancellationToken);

            var ejected = IsWormholeOrPochven(source.SystemId, data);
            if (ejected && source.Position is null)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.StructureUnavailable, 0, cancellationToken);

            var contents = await LockItemTreeAsync(connection, transaction, structureId, ownerId, cancellationToken);
            var roots = contents.Where(i => i.LocationId == structureId && i.OwnerId == ownerId).ToArray();
            if (roots.Length == 0)
                return await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.NoItemsToMove, 0, cancellationToken);

            var wrapName = $"{data.Systems[source.SystemId].Name} - {source.Name} Asset Safety";
            var markerType = GetWrapMarkerType(data);
            var markerLocation = ejected ? solarSystemId : 0;
            var markerOwner = ejected ? 0 : ownerId;
            var markerId = await _items.CreateAsync(
                connection, transaction,
                new Eve.Inventory.NewItem(markerType.TypeId, markerOwner, markerLocation, 0, Singleton: true, CustomName: wrapName),
                cancellationToken);

            if (ejected)
            {
                var spaceItem = new Eve.Ships.SpaceItem(
                    markerId, checked((int)solarSystemId), source.Position!, [0, 0, 1],
                    markerType.Radius > 0 ? markerType.Radius : 1,
                    markerType.GroupId, 0, 0, 0, 0, 0, _now() + (long)System.TimeSpan.FromHours(2).TotalMilliseconds);
                await _spaceItems.SaveAsync(connection, transaction, spaceItem, cancellationToken);
                var rootIds = roots.Select(i => i.Id).ToHashSet();
                foreach (var item in contents)
                {
                    var location = rootIds.Contains(item.Id) ? markerId : item.LocationId;
                    await _items.MoveToOwnerAsync(connection, transaction, item.Id, 0, location, item.Flag, cancellationToken);
                }
            }
            else
            {
                foreach (var item in roots)
                    await _items.MoveAsync(connection, transaction, item.Id, markerId, item.Flag, cancellationToken);
            }

            await using (var insert = new Npgsql.NpgsqlCommand(
                $"""
                INSERT INTO {_schema}.asset_safety_wraps
                    (wrap_id, scope, owner_id, corporation_id, source_structure_id, source_system_id, source_type_id,
                     source_name, system_name, wrap_name, created_at_ms, state)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
                """, connection, transaction))
            {
                foreach (var value in new object[]
                {
                    markerId, scope, ownerId, corporationId, structureId, checked((int)solarSystemId), source.TypeId,
                    source.Name, data.Systems[source.SystemId].Name, wrapName, _now(), ejected ? Ejected : Stored,
                })
                    insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = value });
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await FinishReceiptAsync(
                connection, transaction, operationKey,
                ejected ? Eve.Services.Core.AssetSafetyActionState.ItemsEjected
                        : scope == Corporation ? Eve.Services.Core.AssetSafetyActionState.CorporationWrapCreated
                                               : Eve.Services.Core.AssetSafetyActionState.PersonalWrapCreated,
                markerId, cancellationToken);
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            _log?.Invoke($"asset safety trigger for structure {structureId} failed: {error.Message}");
            return Eve.Services.Core.AssetSafetyActionState.PersistenceFailure;
        }
    }

    private async System.Threading.Tasks.Task DeliverAutomaticallyAsync(
        long wrapId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var operationKey = $"asset-safety:auto:{wrapId}";
        var replay = await BeginReceiptAsync(connection, transaction, operationKey, "auto-delivery", cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        var wrap = await ReadWrapForUpdateAsync(connection, transaction, wrapId, cancellationToken);
        if (wrap is null || wrap.State != Stored || _now() - wrap.CreatedAtMs < 20 * DayMs)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }
        var station = NearestAutomaticStation(wrap.SourceSystemId, data);
        if (station is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return;
        }

        var contents = await LockItemTreeAsync(connection, transaction, wrapId, wrap.OwnerId, cancellationToken);
        var fee = await FeeAsync(contents, wrap.SourceSystemId == station.SolarSystemId, cancellationToken);
        if (fee > 0)
        {
            await _wallets.AdjustAsync(
                connection, transaction, wrap.OwnerId, -fee, station.Id, AssetSafetyRecoveryTaxRefTypeId,
                "Asset Safety automatic recovery", _now(), $"asset-safety-wrap:{wrapId}", cancellationToken, wrapId);
        }
        foreach (var item in contents.Where(i => i.LocationId == wrapId && i.OwnerId == wrap.OwnerId))
            await _items.MoveAsync(connection, transaction, item.Id, station.Id, item.Flag, cancellationToken);

        await _items.DeleteAsync(connection, transaction, wrapId, cancellationToken);
        await SetDeliveredAsync(connection, transaction, wrapId, station.Id, _now(), cancellationToken);
        await FinishReceiptAsync(connection, transaction, operationKey, Eve.Services.Core.AssetSafetyActionState.WrapDelivered, wrapId, cancellationToken);
    }

    private async System.Threading.Tasks.Task<DestinationCheck> ValidateDestinationAsync(
        long characterId,
        long corporationId,
        long destinationId,
        long sourceSystemId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        var station = data.Stations.Get(destinationId);
        if (station is not null)
        {
            return station.SolarSystemId == sourceSystemId
                ? new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.WrapDelivered, station.SolarSystemId)
                : new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.InvalidDestination, station.SolarSystemId);
        }

        var structure = await ReadPersistedStructureAsync(destinationId, data, cancellationToken);
        if (structure is null || !IsStandardUpwell(structure.TypeId, data))
            return new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.InvalidDestination, 0);
        if (structure.SystemId != sourceSystemId)
            return new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.InvalidDestination, structure.SystemId);

        if (corporationId <= 0)
            return new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.DestinationAccessDenied, structure.SystemId);
        var affiliation = await _affiliationOf(characterId, cancellationToken);
        var snapshots = await _structures.GetDockingAccessSnapshotsAsync(
            characterId, corporationId, affiliation.Alliance ?? 0, [destinationId], cancellationToken);
        var access = snapshots.FirstOrDefault(s => s.StructureId == destinationId);
        return access is not null && AllowsDocking(access, characterId, corporationId, affiliation.Alliance ?? 0)
            ? new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.WrapDelivered, structure.SystemId)
            : new DestinationCheck(Eve.Services.Core.AssetSafetyActionState.DestinationAccessDenied, structure.SystemId);
    }

    private async System.Threading.Tasks.Task<StructureRow?> ReadPersistedStructureAsync(
        long structureId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        if (structureId <= 0 || await _structures.GetStructureInfoAsync(structureId, cancellationToken) is not { PersistentlyDeployed: true })
            return null;

        var table = await _readTable("structures", cancellationToken);
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (table[key] is not System.Text.Json.Nodes.JsonObject row || !IsPersistedStructureRow(row, structureId))
            return null;
        var typeId = checked((int)Positive(Integer(row["typeID"])));
        var systemId = checked((int)Positive(Integer(row["solarSystemID"])));
        var ownerCorporationId = Positive(Integer(row["ownerCorpID"] ?? row["corporationID"]));
        if (!data.Systems.ContainsKey(systemId))
            return null;
        return new StructureRow(
            structureId, typeId, systemId, ownerCorporationId, Math.Max(0, Integer(row["allianceID"])),
            Text(row["name"] ?? row["structureName"] ?? row["itemName"]) is { Length: > 0 } name ? name : $"Structure {structureId}",
            ReadPosition(row));
    }

    private async System.Threading.Tasks.Task<System.Collections.Generic.List<StructureRow>> ReadPersistedStructuresAsync(
        long systemId,
        StaticData data,
        System.Threading.CancellationToken cancellationToken)
    {
        var table = await _readTable("structures", cancellationToken);
        var rows = new System.Collections.Generic.List<StructureRow>();
        foreach (var (key, value) in table)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)
                || value is not System.Text.Json.Nodes.JsonObject row
                || !IsPersistedStructureRow(row, id)
                || Integer(row["solarSystemID"]) != systemId)
                continue;
            var typeId = checked((int)Positive(Integer(row["typeID"])));

            if (!IsStandardUpwell(typeId, data))
                continue;
            rows.Add(new StructureRow(
                id, typeId, checked((int)systemId), Positive(Integer(row["ownerCorpID"] ?? row["corporationID"])),
                Math.Max(0, Integer(row["allianceID"])),
                Text(row["name"] ?? row["structureName"] ?? row["itemName"]) is { Length: > 0 } name ? name : $"Structure {id}",
                ReadPosition(row)));
        }
        return rows;
    }

    private static bool IsPersistedStructureRow(System.Text.Json.Nodes.JsonObject row, long structureId) =>
        structureId > 0
        && Positive(Integer(row["structureID"] ?? row["itemID"], structureId)) == structureId
        && Positive(Integer(row["typeID"])) > 0
        && Positive(Integer(row["solarSystemID"])) > 0
        && Positive(Integer(row["ownerCorpID"] ?? row["corporationID"])) > 0
        && !Truthy(row["destroyedAt"]);

    private static bool IsStandardUpwell(int typeId, StaticData data)
    {
        if (!data.Types.TryGetValue(typeId, out var type) || !type.Published
            || !data.Groups.TryGetValue(type.GroupId, out var group)
            || !data.Categories.TryGetValue(group.CategoryId, out var category)
            || !category.Equals("Structure", System.StringComparison.OrdinalIgnoreCase))
            return false;
        return AssetSafetyGroups.Contains(group.Name);
    }

    private static bool AllowsDocking(
        Eve.Services.Core.StructureDockingAccessSnapshot structure,
        long characterId,
        long corporationId,
        long allianceId)
    {
        if (!structure.PersistentlyDeployed || structure.OwnerCorporationId <= 0
            || structure.OwnerAllianceId < 0 || !structure.ProfileResolved || !structure.DockingSettingResolved
            || structure.AccessLists is not { Count: > 0 } lists
            || lists.Any(list => list is null || !list.IsLoaded || list.Members is null))
            return false;

        var seen = new System.Collections.Generic.HashSet<long>();
        foreach (var list in lists)
        {
            if (list is null || !seen.Add(list.AccessListId))
                return false;
        }
        foreach (var list in lists)
        {
            var specificity = new System.Collections.Generic.List<long>(4);
            if (characterId > 0) specificity.Add(characterId);
            if (corporationId > 0) specificity.Add(corporationId);
            if (allianceId > 0) specificity.Add(allianceId);
            specificity.Add(0);
            foreach (var memberId in specificity)
            {
                var matches = list!.Members!.Where(m => m.MemberId == memberId).ToArray();
                if (matches.Length > 1)
                    return false;
                if (matches.Length == 1)
                {
                    if (matches[0].IsAllowed)
                        return true;
                    break;
                }
            }
        }
        return false;
    }

    private async System.Threading.Tasks.Task<bool> IsCorporationExecutiveAsync(
        long characterId,
        long corporationId,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var allowed = await IsCorporationExecutiveAsync(connection, transaction, characterId, corporationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return allowed;
    }

    private async System.Threading.Tasks.Task<bool> IsCorporationExecutiveAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        long corporationId,
        System.Threading.CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            return false;

        long ceoId;
        bool ceoRolesSuspended;
        bool isNpc;
        bool deleted;
        await using (var corporation = new Npgsql.NpgsqlCommand(
            $"SELECT ceo_id, ceo_roles_suspended, is_npc, deleted FROM {_corporationSchema}.corporations WHERE id = $1 FOR SHARE",
            connection, transaction))
        {
            corporation.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
            await using var reader = await corporation.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return false;
            ceoId = reader.GetInt64(0);
            ceoRolesSuspended = reader.GetBoolean(1);
            isNpc = reader.GetBoolean(2);
            deleted = reader.GetBoolean(3);
        }
        if (isNpc || deleted)
            return false;

        long roles;
        await using (var member = new Npgsql.NpgsqlCommand(
            $"SELECT roles FROM {_corporationSchema}.members WHERE corporation_id = $1 AND character_id = $2 FOR SHARE",
            connection, transaction))
        {
            member.Parameters.Add(new Npgsql.NpgsqlParameter { Value = corporationId });
            member.Parameters.Add(new Npgsql.NpgsqlParameter { Value = characterId });
            var value = await member.ExecuteScalarAsync(cancellationToken);
            if (value is not long roleMask)
                return false;
            roles = roleMask;
        }
        return (!ceoRolesSuspended && ceoId == characterId) || (roles & DirectorRole) != 0;
    }

    private async System.Threading.Tasks.Task<System.Collections.Generic.List<Eve.Inventory.Item>> LockItemTreeAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long locationId,
        long ownerId,
        System.Threading.CancellationToken cancellationToken)
    {
        var ids = new System.Collections.Generic.List<long>();
        await using (var command = new Npgsql.NpgsqlCommand(
            $"""
            WITH RECURSIVE contents(id, path) AS (
                SELECT id, ARRAY[id]::bigint[]
                FROM {_inventorySchema}.items
                WHERE location_id = $1 AND owner_id = $2
                UNION ALL
                SELECT child.id, contents.path || child.id
                FROM {_inventorySchema}.items child
                JOIN contents ON child.location_id = contents.id
                WHERE NOT child.id = ANY(contents.path)
            )
            SELECT DISTINCT id FROM contents ORDER BY id
            """, connection, transaction))
        {
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = locationId });
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = ownerId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                ids.Add(reader.GetInt64(0));
        }

        var items = new System.Collections.Generic.List<Eve.Inventory.Item>(ids.Count);
        foreach (var id in ids)
        {
            var item = await _items.LockAndGetAsync(connection, transaction, id, cancellationToken);
            if (item is not null)
                items.Add(item);
        }
        return items;
    }

    private async System.Threading.Tasks.Task<decimal> FeeAsync(
        System.Collections.Generic.IReadOnlyList<Eve.Inventory.Item> items,
        bool sameSystem,
        System.Threading.CancellationToken cancellationToken)
    {
        var value = await EstimateValueAsync(items, cancellationToken);
        var rate = sameSystem ? 0.005m : 0.15m;
        return decimal.Round(value * rate, 2, System.MidpointRounding.AwayFromZero);
    }

    private async System.Threading.Tasks.Task<decimal> EstimateValueAsync(
        System.Collections.Generic.IReadOnlyList<Eve.Inventory.Item> items,
        System.Threading.CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return 0m;

        var typeIds = items.Select(i => (long)i.TypeId).Distinct().ToArray();
        var prices = new System.Collections.Generic.Dictionary<int, decimal>();
        try
        {
            foreach (var row in await _sde.ReadAsync("marketPrices", typeIds, cancellationToken))
            {
                var typeId = checked((int)row.Key);
                var average = DecimalProperty(row.Doc, "averagePrice");
                var adjusted = DecimalProperty(row.Doc, "adjustedPrice");
                var price = average > 0 ? average : adjusted;
                if (price > 0)
                    prices[typeId] = price;
            }
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == "42P01")
        {
            // The current SDE build may not publish marketPrices; the type basePrice below remains usable.
        }

        var missing = typeIds.Where(id => !prices.ContainsKey(checked((int)id))).ToArray();
        if (missing.Length > 0)
        {
            foreach (var row in await _sde.ReadAsync("types", missing, cancellationToken))
            {
                var price = DecimalProperty(row.Doc, "basePrice");
                if (price > 0)
                    prices[checked((int)row.Key)] = price;
            }
        }

        decimal total = 0;
        foreach (var item in items)
        {
            var quantity = item.Singleton || item.Quantity <= 0 ? 1m : item.Quantity;
            var price = prices.GetValueOrDefault(item.TypeId);
            if (price <= 0)
            {
                // ponytail: Unknown-price items are valued at 1 ISK per unit so rare items do not block delivery or create an overcharge.
                price = 1m;
            }
            total += price * quantity;
        }
        return decimal.Round(total, 2, System.MidpointRounding.AwayFromZero);
    }

    private async System.Threading.Tasks.Task<StaticData> CatalogAsync(System.Threading.CancellationToken cancellationToken)
    {
        if (_catalog is not null)
            return _catalog;
        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            if (_catalog is not null)
                return _catalog;

            var summaries = await _sde.ReadTypeSummariesAsync(cancellationToken);
            var types = summaries.ToDictionary(t => t.TypeId);
            var groups = (await _sde.ReadAsync("groups", cancellationToken: cancellationToken))
                .ToDictionary(r => checked((int)r.Key), r => new GroupInfo(
                    checked((int)Integer(r.Doc, "categoryID")),
                    Eve.Sde.SdeReader.English(r.Doc.GetProperty("name"))));
            var categories = (await _sde.ReadAsync("categories", cancellationToken: cancellationToken))
                .ToDictionary(r => checked((int)r.Key), r => Eve.Sde.SdeReader.English(r.Doc.GetProperty("name")));
            var regions = (await _sde.ReadAsync("mapRegions", cancellationToken: cancellationToken))
                .ToDictionary(r => checked((int)r.Key), r => new RegionInfo(
                    Eve.Sde.SdeReader.English(r.Doc.GetProperty("name")),
                    Positive(Integer(r.Doc, "wormholeClassID")) > 0));
            var systems = new System.Collections.Generic.Dictionary<int, SystemInfo>();
            foreach (var row in await _sde.ReadAsync("mapSolarSystems", cancellationToken: cancellationToken))
            {
                var id = checked((int)row.Key);
                var regionId = checked((int)Integer(row.Doc, "regionID"));
                var region = regions.GetValueOrDefault(regionId);
                systems[id] = new SystemInfo(
                    id,
                    Eve.Sde.SdeReader.English(row.Doc.GetProperty("name")),
                    regionId,
                    row.Doc.TryGetProperty("securityStatus", out var security) && security.ValueKind == System.Text.Json.JsonValueKind.Number ? security.GetDouble() : 0,
                    ReadVector(row.Doc, "center"),
                    region?.Name.Equals("Pochven", System.StringComparison.OrdinalIgnoreCase) == true,
                    region?.Wormhole == true);
            }
            var stations = await Eve.Universe.StationCatalog.LoadAsync(_sde, cancellationToken);
            var cargoType = summaries.FirstOrDefault(t => t.Name.Equals("Cargo Container", System.StringComparison.OrdinalIgnoreCase));
            if (cargoType is null)
                throw new System.InvalidOperationException("The current SDE has no published Cargo Container type.");
            var wrapType = summaries.FirstOrDefault(t => t.Name.Equals("Asset Safety Wrap", System.StringComparison.OrdinalIgnoreCase)) ?? cargoType;
            var containerDetails = await _sde.ReadAsync("types", [cargoType.TypeId, wrapType.TypeId], cancellationToken);
            var details = containerDetails.ToDictionary(
                row => checked((int)row.Key),
                row => new TypeDetails(
                    DecimalProperty(row.Doc, "basePrice"),
                    DoubleProperty(row.Doc, "radius")));

            _catalog = new StaticData(types, groups, categories, systems, stations, cargoType, wrapType, details);
            return _catalog;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private static Eve.Universe.Station? NearestAutomaticStation(int sourceSystemId, StaticData data)
    {
        if (!data.Systems.TryGetValue(sourceSystemId, out var source))
            return null;
        var high = source.Security >= HighSecurityMinimum;
        var bestDistance = double.PositiveInfinity;
        Eve.Universe.Station? best = null;
        foreach (var station in data.Stations.All)
        {
            if (!data.Systems.TryGetValue(station.SolarSystemId, out var system))
                continue;
            var eligibleSecurity = high
                ? system.Security >= HighSecurityMinimum
                : system.Security > 0 && system.Security < HighSecurityMinimum;
            if (!eligibleSecurity)
                continue;
            var distance = SquaredDistance(source.Position, system.Position);
            if (distance < bestDistance || (distance == bestDistance && station.Id < best?.Id))
            {
                bestDistance = distance;
                best = station;
            }
        }
        return best;
    }

    private static bool IsWormholeOrPochven(long systemId, StaticData data) =>
        data.Systems.TryGetValue(checked((int)systemId), out var system) && (system.Wormhole || system.Pochven);

    private static double SquaredDistance(double[] left, double[] right)
    {
        var x = left[0] - right[0];
        var y = left[1] - right[1];
        var z = left[2] - right[2];
        return x * x + y * y + z * z;
    }

    private static Eve.Marshal.PyValue WrapPayload(
        long wrapId,
        long solarSystemId,
        string name,
        long createdAtMs,
        Eve.Universe.Station? nearest)
    {
        return Eve.Services.Py.KeyVal(
            ("assetWrapID", new Eve.Marshal.PyLong(wrapId)),
            ("solarSystemID", new Eve.Marshal.PyLong(solarSystemId)),
            ("wrapName", new Eve.Marshal.PyStr(name)),
            ("ejectTime", new Eve.Marshal.PyLong(ToFileTime(createdAtMs))),
            ("daysUntilCanDeliverConst", new Eve.Marshal.PyInt(5)),
            ("daysUntilAutoMoveConst", new Eve.Marshal.PyInt(20)),
            ("nearestNPCStationInfo", NearestStationPayload(nearest)));
    }

    private static Eve.Marshal.PyValue NearestStationPayload(Eve.Universe.Station? station) =>
        station is null
            ? Eve.Marshal.PyNone.Instance
            : Eve.Services.Py.KeyVal(
                ("itemID", new Eve.Marshal.PyLong(station.Id)),
                ("typeID", new Eve.Marshal.PyInt(station.TypeId)));

    private static Eve.Marshal.PyValue DestinationPayload(long itemId, int typeId, string name) =>
        Eve.Services.Py.KeyVal(
            ("itemID", new Eve.Marshal.PyLong(itemId)),
            ("typeID", new Eve.Marshal.PyInt(typeId)),
            ("name", new Eve.Marshal.PyStr(name)));

    private static (int TypeId, int GroupId, double Radius) GetWrapMarkerType(StaticData data)
    {
        var type = data.WrapItem;
        var radius = data.TypeDetails.TryGetValue(type.TypeId, out var details) ? details.Radius : 0;
        return (type.TypeId, type.GroupId, radius);
    }

    private async System.Threading.Tasks.Task<WrapRecord?> ReadWrapForUpdateAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long wrapId,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            SELECT wrap_id, scope, owner_id, source_structure_id, source_system_id, source_type_id,
                   source_name, system_name, wrap_name, created_at_ms, state
            FROM {_schema}.asset_safety_wraps
            WHERE wrap_id = $1
            FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new WrapRecord(
                reader.GetInt64(0), reader.GetInt16(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6), reader.GetString(7),
                reader.GetString(8), reader.GetInt64(9), reader.GetInt16(10))
            : null;
    }

    private async System.Threading.Tasks.Task<ReceiptReplay?> BeginReceiptAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationKey,
        string action,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var insert = new Npgsql.NpgsqlCommand(
            $"""
            INSERT INTO {_schema}.asset_safety_operation_receipts (operation_key, action, result_state, created_at_ms)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (operation_key) DO NOTHING
            """, connection, transaction);
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = action });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = (int)Eve.Services.Core.AssetSafetyActionState.NotCompleted });
        insert.Parameters.Add(new Npgsql.NpgsqlParameter { Value = _now() });
        if (await insert.ExecuteNonQueryAsync(cancellationToken) > 0)
            return null;

        await using var read = new Npgsql.NpgsqlCommand(
            $"SELECT action, result_state FROM {_schema}.asset_safety_operation_receipts WHERE operation_key = $1",
            connection, transaction);
        read.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        if (!reader.GetString(0).Equals(action, System.StringComparison.Ordinal))
            throw new System.InvalidOperationException("ASSET_SAFETY_RECEIPT_CONFLICT");
        return new ReceiptReplay((Eve.Services.Core.AssetSafetyActionState)reader.GetInt32(1));
    }

    private async System.Threading.Tasks.Task<Eve.Services.Core.AssetSafetyActionState> FinishReceiptAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationKey,
        Eve.Services.Core.AssetSafetyActionState state,
        long wrapId,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            UPDATE {_schema}.asset_safety_operation_receipts
            SET result_state = $2, wrap_id = $3
            WHERE operation_key = $1
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationKey });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = (int)state });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    private async System.Threading.Tasks.Task SetDeliveredAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long wrapId,
        long destinationId,
        long deliveredAt,
        System.Threading.CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"""
            UPDATE {_schema}.asset_safety_wraps
            SET state = $2, destination_id = $3, delivered_at_ms = $4
            WHERE wrap_id = $1 AND state = $5
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = wrapId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Delivered });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = destinationId });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = deliveredAt });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = Stored });
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new System.InvalidOperationException("ASSET_SAFETY_WRAP_STATE_CHANGED");
    }

    private string RequestKey(Eve.Services.ServiceCall call, string action, params long[] values)
    {
        var callId = call.CallId switch
        {
            Eve.Marshal.PyInt integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Eve.Marshal.PyLong integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Eve.Marshal.PyReal real => real.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            _ => Eve.Macho.JsSemantics.StringValue(call.CallId) ?? call.CallId.ToString(),
        };
        return $"asset-safety:{action}:{call.Session.SessionId}:{call.Session.CharacterId ?? 0}:{callId}:{System.String.Join(':', values)}";
    }

    private static long ToFileTime(long unixMilliseconds) =>
        checked(unixMilliseconds * 10_000L + 116_444_736_000_000_000L);

    private static string SchemaName(string schema)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]{0,62}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new System.ArgumentException($"not a usable schema name: {schema}", nameof(schema));
        return schema;
    }

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long Integer(System.Text.Json.Nodes.JsonNode? node, long fallback = 0) =>
        long.TryParse(node?.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : fallback;

    private static double Number(System.Text.Json.Nodes.JsonNode? node, double fallback = 0) =>
        double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value : fallback;

    private static string Text(System.Text.Json.Nodes.JsonNode? node) => node?.ToString() ?? "";

    private static bool Truthy(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is null)
            return false;
        var text = node.ToString();
        return text.Length > 0 && text != "0" && !text.Equals("false", System.StringComparison.OrdinalIgnoreCase);
    }

    private static double[]? ReadPosition(System.Text.Json.Nodes.JsonObject row)
    {
        if (row["position"] is System.Text.Json.Nodes.JsonArray array && array.Count >= 3)
        {
            var values = new[] { Number(array[0], double.NaN), Number(array[1], double.NaN), Number(array[2], double.NaN) };
            return values.All(double.IsFinite) ? values : null;
        }
        if (row["position"] is System.Text.Json.Nodes.JsonObject position)
        {
            var values = new[] { Number(position["x"], double.NaN), Number(position["y"], double.NaN), Number(position["z"], double.NaN) };
            return values.All(double.IsFinite) ? values : null;
        }
        var topLevel = new[] { Number(row["x"], double.NaN), Number(row["y"], double.NaN), Number(row["z"], double.NaN) };
        return topLevel.All(double.IsFinite) ? topLevel : null;
    }

    private static double[] ReadVector(System.Text.Json.JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var vector))
            return [0, 0, 0];
        if (vector.ValueKind == System.Text.Json.JsonValueKind.Array && vector.GetArrayLength() >= 3)
            return [vector[0].GetDouble(), vector[1].GetDouble(), vector[2].GetDouble()];
        if (vector.ValueKind == System.Text.Json.JsonValueKind.Object
            && vector.TryGetProperty("x", out var x) && vector.TryGetProperty("y", out var y) && vector.TryGetProperty("z", out var z))
            return [x.GetDouble(), y.GetDouble(), z.GetDouble()];
        return [0, 0, 0];
    }

    private static long Integer(System.Text.Json.JsonElement parent, string name, long fallback = 0) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt64(out var parsed)
            ? parsed : fallback;

    private static decimal DecimalProperty(System.Text.Json.JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDecimal(out var parsed)
            ? parsed : 0m;

    private static double DoubleProperty(System.Text.Json.JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? value.GetDouble() : 0d;

    private sealed record GroupInfo(int CategoryId, string Name);
    private sealed record RegionInfo(string Name, bool Wormhole);
    private sealed record SystemInfo(int Id, string Name, int RegionId, double Security, double[] Position, bool Pochven, bool Wormhole);
    private sealed record StructureRow(long Id, int TypeId, int SystemId, long OwnerCorporationId, long OwnerAllianceId, string Name, double[]? Position);
    private sealed record TypeDetails(decimal BasePrice, double Radius);
    private sealed record WrapRecord(long Id, short Scope, long OwnerId, long SourceStructureId, int SourceSystemId, int SourceTypeId, string SourceName, string SystemName, string WrapName, long CreatedAtMs, short State);
    private sealed record ReceiptReplay(Eve.Services.Core.AssetSafetyActionState State);
    private sealed record DestinationCheck(Eve.Services.Core.AssetSafetyActionState State, long SystemId);
    private sealed record StaticData(
        System.Collections.Generic.Dictionary<int, Eve.Sde.SdeTypeSummary> Types,
        System.Collections.Generic.Dictionary<int, GroupInfo> Groups,
        System.Collections.Generic.Dictionary<int, string> Categories,
        System.Collections.Generic.Dictionary<int, SystemInfo> Systems,
        Eve.Universe.StationCatalog Stations,
        Eve.Sde.SdeTypeSummary CargoContainer,
        Eve.Sde.SdeTypeSummary WrapItem,
        System.Collections.Generic.Dictionary<int, TypeDetails> TypeDetails)
    {
        public bool IsStandardUpwell(int typeId) => PersistenceStructureAssetSafetyReadModel.IsStandardUpwell(typeId, this);
        public string GroupNameOf(int typeId) =>
            Types.TryGetValue(typeId, out var type) && Groups.TryGetValue(type.GroupId, out var group) ? group.Name : "";
    }
}

public sealed class PersistenceStructureDirectoryReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf,
    Eve.Sde.SdeReader sde,
    Eve.GameStore.GameStoreCache gameStoreCache) :
    Eve.Services.Core.IStructureDirectoryReadCallbacks,
    Eve.Services.Core.IStructureSettingsReadCallbacks
{
    public PersistenceStructureDirectoryReadModel(
        Func<string, CancellationToken, Task<JsonObject>> readTable,
        Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf)
        : this(readTable, affiliationOf, null!, null!)
    {
    }

    private const long AnsiblexJumpBridgeTypeId = 35841;
    private const int JumpBridgeServiceId = 15;
    private const int DockingServiceId = 1;
    private const int JumpBridgeAccessSettingId = 34;
    private const int DockAccessSettingId = 19;

    private static int? SettingIdForService(long serviceId) => serviceId switch
    {
        DockingServiceId => DockAccessSettingId,
        JumpBridgeServiceId => JumpBridgeAccessSettingId,
        5 => 4,
        16 => 35,
        _ => null,
    };

    private sealed record Bridge(long Id, long SolarSystemId, long DestinationSolarSystemId, JsonObject Value);

    public async ValueTask<long?> GetJbStructureDestinationAsync(long structureId, CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var structures = await readTable("structures", cancellationToken);
        var bridge = ReadBridges(structures).FirstOrDefault(candidate => candidate.Id == structureId);
        return bridge is { DestinationSolarSystemId: > 0 } ? bridge.DestinationSolarSystemId : null;
    }

    public async ValueTask<(long StructureId, long SolarSystemId)?> GetLinkedStructureAsync(
        long structureId,
        CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;
        var structures = await readTable("structures", cancellationToken);
        var bridges = ReadBridges(structures);
        var source = bridges.FirstOrDefault(candidate => candidate.Id == structureId);
        if (source is null || source.DestinationSolarSystemId <= 0)
            return null;
        var linked = bridges.FirstOrDefault(candidate =>
            candidate.Id != source.Id &&
            candidate.SolarSystemId == source.DestinationSolarSystemId &&
            candidate.DestinationSolarSystemId == source.SolarSystemId);
        return linked is { Id: > 0, SolarSystemId: > 0 }
            ? (linked.Id, linked.SolarSystemId)
            : null;
    }

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

public async ValueTask<Eve.Services.Core.PersistedStructureInfo?> GetStructureInfoAsync(
        long structureId,
        CancellationToken cancellationToken)
    {
        if (structureId <= 0)
            return null;

        var structures = await readTable("structures", cancellationToken);
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (structures[key] is not JsonObject structure || !IsPersistentlyDeployedStructure(structure, structureId))
            return null;

        return new Eve.Services.Core.PersistedStructureInfo(
            structureId,
            true,
            StructureInfoPayload(structureId, structure));
    }

    public async ValueTask<IReadOnlyList<Eve.Services.Core.StructureDockingAccessSnapshot>> GetDockingAccessSnapshotsAsync(
        long characterId,
        long corporationId,
        long allianceId,
        IReadOnlyList<long> structureIds,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0 || structureIds.Count == 0)
            return Array.Empty<Eve.Services.Core.StructureDockingAccessSnapshot>();

        var affiliation = await affiliationOf(characterId, cancellationToken);
        if (affiliation.Corporation != corporationId || (affiliation.Alliance ?? 0) != allianceId)
            return Array.Empty<Eve.Services.Core.StructureDockingAccessSnapshot>();

        var structures = await readTable("structures", cancellationToken);
        var profileState = await readTable("structureProfiles", cancellationToken);
        var accessGroupState = await readTable("accessGroups", cancellationToken);
        var result = new List<Eve.Services.Core.StructureDockingAccessSnapshot>();

        foreach (var structureId in structureIds.Distinct())
        {
            if (structureId <= 0 ||
                structures[structureId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject structure ||
                !IsPersistentlyDeployedStructure(structure, structureId))
                continue;

            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
            if (ownerCorporationId <= 0)
                continue;

            var access = DockingAccessLists(structure, ownerCorporationId, profileState, accessGroupState);
            result.Add(new Eve.Services.Core.StructureDockingAccessSnapshot(
                structureId,
                ownerCorporationId,
                Math.Max(0, Integer(structure["allianceID"])),
                PersistentlyDeployed: true,
                access.ProfileResolved,
                access.DockingSettingResolved,
                access.AccessLists));
        }

        return result;
    }

    public async ValueTask<PyDict> GetMyCharacterStructuresAsync(
        long characterId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return new PyDict([]);
        var affiliation = await affiliationOf(characterId, cancellationToken);
        var corporationId = affiliation.Corporation ?? 0;
        return corporationId > 0
            ? await ReadStructuresForCorporationAsync(corporationId, cancellationToken)
            : new PyDict([]);
    }

    public async ValueTask<PyDict> GetMyCorporationStructuresAsync(
        long characterId,
        long corporationId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || corporationId <= 0)
            return new PyDict([]);
        var affiliation = await affiliationOf(characterId, cancellationToken);
        return affiliation.Corporation == corporationId
            ? await ReadStructuresForCorporationAsync(corporationId, cancellationToken)
            : new PyDict([]);
    }

    private async ValueTask<PyDict> ReadStructuresForCorporationAsync(
        long corporationId,
        CancellationToken cancellationToken)
    {
        var state = await readTable("structures", cancellationToken);
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId) ||
                Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"])) != corporationId)
                continue;

            entries.Add(new KeyValuePair<PyValue, PyValue>(
                new PyLong(structureId),
                StructureInfoPayload(structureId, structure)));
        }

        entries.Sort(static (left, right) => ((PyLong)left.Key).Value.CompareTo(((PyLong)right.Key).Value));
        return new PyDict(entries);
    }

    private static bool IsPersistentlyDeployedStructure(JsonObject structure, long structureId)
    {
        var storedId = Positive(Integer(structure["structureID"] ?? structure["itemID"], structureId));
        return structureId > 0 && storedId == structureId &&
            Positive(Integer(structure["typeID"])) > 0 &&
            Positive(Integer(structure["solarSystemID"])) > 0 &&
            Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"])) > 0 &&
            !Truthy(structure["destroyedAt"]);
    }

    private static PyValue StructureInfoPayload(long structureId, JsonObject structure)
    {
        var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
        var ownerId = Positive(Integer(structure["ownerID"], ownerCorporationId));
        var allianceId = Math.Max(0, Integer(structure["allianceID"]));
        var services = structure["services"] as JsonObject ??
            structure["serviceStates"] as JsonObject ?? new JsonObject();
        var serviceEntries = new List<KeyValuePair<PyValue, PyValue>>();

        foreach (var (key, value) in services)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var serviceId) ||
                serviceId <= 0 || serviceId > 9_007_199_254_740_991L)
                continue;
            var state = Integer(value, long.MinValue);
            if (state != long.MinValue)
                serviceEntries.Add(new KeyValuePair<PyValue, PyValue>(new PyLong(serviceId), new PyInt(state)));
        }

        return Py.KeyVal(
            ("structureID", new PyLong(structureId)),
            ("itemID", new PyLong(structureId)),
            ("typeID", new PyInt(Positive(Integer(structure["typeID"])))),
            ("solarSystemID", new PyInt(Positive(Integer(structure["solarSystemID"])))),
            ("ownerID", ownerId > 0 ? new PyLong(ownerId) : PyNone.Instance),
            ("corporationID", new PyLong(ownerCorporationId)),
            ("ownerCorpID", new PyLong(ownerCorporationId)),
            ("allianceID", new PyLong(allianceId)),
            ("services", new PyDict(serviceEntries)));
    }

    private static (
        bool ProfileResolved,
        bool DockingSettingResolved,
        IReadOnlyList<Eve.Services.Core.StructureAccessListSnapshot?>? AccessLists) DockingAccessLists(
            JsonObject structure,
            long ownerCorporationId,
            JsonObject profileState,
            JsonObject accessGroupState)
    {
        var profileId = Integer(structure["profileID"]);
        if (profileId < 0)
            return (false, false, null);

        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        JsonObject? profile = profileId > 0
            ? profiles[profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));

        if (profileId > 0 &&
            (profile is null || Integer(profile["corporationID"]) != ownerCorporationId))
            return (false, false, null);

        if (profile is not null && Integer(profile["corporationID"]) != ownerCorporationId)
            return (false, false, null);

        if (profile is null)
            return OwnerCorporationDockingAccess(ownerCorporationId);

        var settings = profile["settingsBySettingID"] as JsonObject;
        if (settings is null || settings["19"] is not JsonArray groups || groups.Count == 0)
            return profileId <= 0 || Truthy(profile["isDefault"])
                ? OwnerCorporationDockingAccess(ownerCorporationId)
                : (true, false, null);

        var accessLists = new List<Eve.Services.Core.StructureAccessListSnapshot?>();
        foreach (var node in groups)
        {
            if (node is not JsonObject row)
                return (true, false, null);
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0)
                return (true, false, null);
            if (groupId == 0)
            {
                accessLists.Add(OwnerCorporationAccessList(ownerCorporationId));
                continue;
            }

            var groupKey = groupId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (accessGroupState[groupKey] is not JsonObject group ||
                group["members"] is not JsonArray members)
            {
                accessLists.Add(new Eve.Services.Core.StructureAccessListSnapshot(groupId, false, null));
                continue;
            }

            var normalizedMembers = new List<Eve.Services.Core.StructureAccessListMember>();
            var malformed = false;
            foreach (var member in members)
            {
                if (member is not JsonObject entry)
                {
                    malformed = true;
                    break;
                }
                var memberId = Integer(entry["memberID"], -1);
                var membershipType = Integer(entry["membershipType"], -1);
                if (memberId < 0 || membershipType is < 0 or > 3)
                {
                    malformed = true;
                    break;
                }
                normalizedMembers.Add(new Eve.Services.Core.StructureAccessListMember(
                    memberId, membershipType is 0 or 1 or 2));
            }

            if (malformed)
            {
                accessLists.Add(new Eve.Services.Core.StructureAccessListSnapshot(groupId, false, null));
                continue;
            }

            var creatorId = Positive(Integer(group["creatorID"]));
            if (creatorId > 0 && normalizedMembers.All(member => member.MemberId != creatorId))
                normalizedMembers.Add(new Eve.Services.Core.StructureAccessListMember(creatorId, true));
            accessLists.Add(new Eve.Services.Core.StructureAccessListSnapshot(groupId, true, normalizedMembers));
        }

        return (true, true, accessLists);
    }

    private static (
        bool ProfileResolved,
        bool DockingSettingResolved,
        IReadOnlyList<Eve.Services.Core.StructureAccessListSnapshot?>? AccessLists) OwnerCorporationDockingAccess(
            long ownerCorporationId) =>
        ownerCorporationId > 0
            ? (true, true, [OwnerCorporationAccessList(ownerCorporationId)])
            : (false, false, null);

    private static Eve.Services.Core.StructureAccessListSnapshot OwnerCorporationAccessList(long ownerCorporationId) =>
        new(0, true, [new Eve.Services.Core.StructureAccessListMember(ownerCorporationId, true)]);



    public async ValueTask<PyDict> CharacterGetServicesAsync(
        long characterId,
        long structureId,
        CancellationToken cancellationToken)
    {
        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        if (context is null || context.Value.Structure["serviceStates"] is not JsonObject services)
            return new PyDict([]);

        var access = context.Value;
        var result = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var (key, node) in services)
        {
            if (!long.TryParse(key, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var serviceId) ||
                serviceId <= 0 || serviceId > 9_007_199_254_740_991L)
                continue;
            var settingId = SettingIdForService(serviceId);
            if (settingId is null ||
                !ProfileSettingAllows(
                    access.Structure,
                    access.OwnerCorporationId,
                    (access.CharacterId, access.Affiliation),
                    access.ProfileState,
                    access.AccessGroupState,
                    settingId.Value))
                continue;
            var state = Integer(node, long.MinValue);
            if (state == long.MinValue)
                continue;
            result.Add(new KeyValuePair<PyValue, PyValue>(new PyInt(serviceId), new PyInt(state)));
        }
        return new PyDict(result);
    }

    public async ValueTask<PyValue> CharacterGetServiceAsync(
        long characterId,
        long structureId,
        long serviceId,
        CancellationToken cancellationToken)
    {
        if (!await CharacterHasServiceAsync(characterId, structureId, serviceId, cancellationToken))
            return PyNone.Instance;

        var settingId = SettingIdForService(serviceId);
        if (settingId is not > 0)
            return PyBool.True;

        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        if (context is null)
            return PyNone.Instance;
        var settings = context.Value.Profile["settingsBySettingID"] as JsonObject;
        var settingGroups = settings?[settingId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonArray;
        if (settingGroups is not { Count: > 0 })
            return PyBool.True;

        var access = (context.Value.CharacterId, context.Value.Affiliation);
        var bestRank = 0;
        JsonNode? resolvedValue = null;
        foreach (var node in settingGroups)
        {
            if (node is not JsonObject row)
                continue;
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0)
                continue;
            var rank = groupId == 0
                ? context.Value.OwnerCorporationId > 0 &&
                    context.Value.Affiliation.Corporation == context.Value.OwnerCorporationId ? 1 : 0
                : AccessGroupMatchRank(groupId, access, context.Value.AccessGroupState);
            if (rank > bestRank)
            {
                bestRank = rank;
                resolvedValue = row["value"];
            }
        }

        return StructureSettingValue(resolvedValue);
    }




    public async ValueTask<PyValue> GetStructureMapDataAsync(
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (sde is null || solarSystemId <= 0)
            return new PyList([]);

        var state = await readTable("structures", cancellationToken);
        var structures = new List<(long Id, JsonObject Row)>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject row ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(row, structureId) ||
                Integer(row["solarSystemID"]) != solarSystemId)
                continue;
            structures.Add((structureId, row));
        }
        structures.Sort(static (left, right) => left.Id.CompareTo(right.Id));

        var typeIds = structures.Select(entry => (long)Positive(Integer(entry.Row["typeID"]))).Distinct().ToArray();
        var types = (await sde.ReadAsync("invTypes", typeIds, cancellationToken))
            .ToDictionary(row => row.Key);

        var descriptor = Descriptor.FromHeader(new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(
                new PyToken("blue.DBRowDescriptor"),
                Py.Tuple(Py.Tuple(
                    Py.Tuple(new PyStr("groupID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("typeID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("itemID"), new PyInt(0x14)),
                    Py.Tuple(new PyStr("itemName"), new PyInt(0x82)),
                    Py.Tuple(new PyStr("locationID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("orbitID"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("connector"), new PyInt(0x0b)),
                    Py.Tuple(new PyStr("x"), new PyInt(0x05)),
                    Py.Tuple(new PyStr("y"), new PyInt(0x05)),
                    Py.Tuple(new PyStr("z"), new PyInt(0x05)),
                    Py.Tuple(new PyStr("celestialIndex"), new PyInt(0x03)),
                    Py.Tuple(new PyStr("orbitIndex"), new PyInt(0x03))))),
            [],
            []));

        static double Coordinate(JsonNode? node) =>
            double.TryParse(node?.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
                    ? value
                    : 0;

        var rows = new List<PyValue>(structures.Count);
        foreach (var (structureId, row) in structures)
        {
            var typeId = Positive(Integer(row["typeID"]));
            var groupId = types.TryGetValue(typeId, out var typeRow)
                ? Positive(SdeInteger(typeRow, "groupID"))
                : 0;
            var ownerId = Positive(Integer(row["ownerID"], Positive(Integer(row["ownerCorpID"] ?? row["corporationID"]))));
            var position = row["position"] as JsonObject;
            var name = Text(row["itemName"] ?? row["name"]);
            rows.Add(new PyPackedRow(descriptor, [
                new PyInt(groupId),
                new PyInt(typeId),
                new PyLong(structureId),
                new PyWString(name),
                new PyInt(solarSystemId),
                new PyInt(ownerId),
                PyBool.False,
                new PyReal(Coordinate(position?["x"])),
                new PyReal(Coordinate(position?["y"])),
                new PyReal(Coordinate(position?["z"])),
                PyNone.Instance,
                PyNone.Instance,
            ]));
        }

        return new PyObjectEx(
            ObjectExKind.Ex2,
            Py.Tuple(
                Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")),
                Py.Dict(("header", descriptor.Header))),
            rows,
            []);
    }

    public async ValueTask<PyValue> GetMyAccessibleOnlineCynoBeaconStructuresAsync(ServiceCall call)
    {
        var access = await ReadAccessContextAsync(call, call.Cancellation);
        if (access.CharacterId <= 0)
            return new PyList([]);

        var state = await readTable("structures", call.Cancellation);
        var profiles = await readTable("structureProfiles", call.Cancellation);
        var groups = await readTable("accessGroups", call.Cancellation);
        var rows = new List<(long Id, PyValue Row)>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId) ||
                Integer(structure["typeID"]) != 35840 ||
                Integer((structure["serviceStates"] as JsonObject)?["16"]) != 1)
                continue;

            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
            if (ownerCorporationId <= 0 ||
                !ProfileSettingAllows(
                    structure,
                    ownerCorporationId,
                    access,
                    profiles,
                    groups,
                    35))
                continue;

            var ownerId = Positive(Integer(structure["ownerID"], ownerCorporationId));
            var stateId = Integer(structure["state"]);
            var name = Text(structure["itemName"] ?? structure["name"]);
            if (name.Length == 0)
                name = $"Structure {structureId}";
            rows.Add((structureId, new PyList([
                new PyInt(structureId),
                new PyInt(Positive(Integer(structure["typeID"]))),
                new PyInt(ownerId),
                new PyInt(Positive(Integer(structure["solarSystemID"]))),
                new PyInt(stateId),
                new PyStr(name),
            ])));
        }

        return new PyList(rows
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Row)
            .ToArray());
    }

    public async ValueTask<PyValue> GetSolarSystemsWithBeaconsAsync(CancellationToken cancellationToken)
    {
        var structures = await readTable("structures", cancellationToken);
        var systems = new SortedSet<long>();
        foreach (var (key, node) in structures)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId) ||
                Integer(structure["typeID"]) != 35840 ||
                Integer((structure["serviceStates"] as JsonObject)?["16"]) != 1)
                continue;
            var solarSystemId = Positive(Integer(structure["solarSystemID"]));
            if (solarSystemId > 0)
                systems.Add(solarSystemId);
        }

        return new PyList(systems.Select(id => (PyValue)new PyInt(id)).ToArray());
    }

    public async ValueTask<PyValue> GetValidWarHQsAsync(
        ServiceCall call,
        long ownerId,
        CancellationToken cancellationToken)
    {
        var characterId = call.Session?.CharacterId ?? 0;
        var corporationId = call.Session?.GetInt("corpid") ?? 0;
        var allianceId = call.Session?.GetInt("allianceid") ?? 0;
        if (characterId <= 0 || ownerId <= 0 ||
            (ownerId != corporationId && ownerId != allianceId))
            return new PyList([]);

        var state = await readTable("structures", cancellationToken);
        var structures = new List<(long Id, JsonObject Row)>();
        foreach (var (key, node) in state)
        {
            if (node is not JsonObject structure ||
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) ||
                !IsPersistentlyDeployedStructure(structure, structureId))
                continue;
            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["ownerID"]));
            var ownerAllianceId = Positive(Integer(structure["allianceID"]));
            if (ownerCorporationId != ownerId && ownerAllianceId != ownerId)
                continue;
            structures.Add((structureId, structure));
        }

        var typeRows = await sde.ReadAsync(
            "invTypes", structures.Select(entry => (long)Positive(Integer(entry.Row["typeID"]))).Distinct(), cancellationToken);
        var types = typeRows.ToDictionary(row => row.Key);
        var systemRows = await sde.ReadAsync(
            "mapSolarSystems", structures.Select(entry => (long)Positive(Integer(entry.Row["solarSystemID"]))).Distinct(), cancellationToken);
        var systems = systemRows.ToDictionary(row => row.Key);

        var valid = new List<(long Id, PyValue Payload)>();
        foreach (var (structureId, structure) in structures)
        {
            var typeId = Positive(Integer(structure["typeID"]));
            var solarSystemId = Positive(Integer(structure["solarSystemID"]));
            if (!types.TryGetValue(typeId, out var type) ||
                SdeInteger(type, "categoryID") != 65 ||
                !SdeBoolean(type, "dockable") ||
                !systems.TryGetValue(solarSystemId, out var system) ||
                SdeDouble(system, "security") < 0.45)
                continue;

            var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["ownerID"]));
            var ownerIdForPayload = Positive(Integer(structure["ownerID"], ownerCorporationId));
            var itemName = Text(structure["itemName"] ?? structure["name"]);
            if (itemName.Length == 0)
                itemName = $"Structure {structureId}";
            var upkeepState = Positive(Integer(structure["upkeepState"], 1));
            var inSpace = structure["inSpace"] is JsonValue inSpaceValue &&
                inSpaceValue.TryGetValue<bool>(out var isInSpace) && !isInSpace
                    ? new PyInt(0)
                    : new PyInt(1);
            var payload = Py.KeyVal(
                ("typeID", new PyInt(typeId)),
                ("structureID", new PyInt(structureId)),
                ("upkeepState", new PyInt(upkeepState)),
                ("wars", new PyList([])),
                ("ownerID", new PyInt(ownerIdForPayload)),
                ("solarSystemID", new PyInt(solarSystemId)),
                ("itemName", new PyStr(itemName)),
                ("inSpace", inSpace));
            valid.Add((structureId, payload));
        }

        return new PyList(valid
            .OrderBy(entry => entry.Id)
            .Select(entry => entry.Payload)
            .ToArray());
    }

    public async ValueTask<bool> SetStructureDescriptionAsync(ServiceCall call)
    {
        if (gameStoreCache is null)
            return false;
        var characterId = call.Session?.CharacterId ?? 0;
        var corporationId = call.Session?.GetInt("corpid") ?? 0;
        var corporationRoles = call.Session?.GetInt("corprole") ?? 0;
        var structureId = call.Arg(0) switch
        {
            PyInt id when id.Value > 0 => id.Value,
            PyLong id when id.Value > 0 => id.Value,
            _ => 0,
        };
        if (characterId <= 0 || corporationId <= 0 || structureId <= 0 ||
            (corporationRoles & 2048) != 2048)
            return false;

        var affiliation = await affiliationOf(characterId, call.Cancellation);
        if (affiliation.Corporation != corporationId)
            return false;

        var state = await readTable("structures", call.Cancellation);
        var key = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (state[key] is not JsonObject structure ||
            !IsPersistentlyDeployedStructure(structure, structureId) ||
            Positive(Integer(structure["ownerCorpID"] ?? structure["ownerID"])) != corporationId)
            return false;

        var text = call.Arg(1) switch
        {
            PyStr value => value.Value,
            PyWString value => value.Value,
            PyBytes value => System.Text.Encoding.UTF8.GetString(value.Value.Span),
            PyInt value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyLong value => value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PyReal value => value.Value.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
            PyBool value => value.Value ? "True" : "False",
            _ => string.Empty,
        };
        var description = text[..Math.Min(1000, text.Length)];
        var write = gameStoreCache.Write(
            "structures",
            $"/{structureId}/description",
            JsonValue.Create(description));
        if (!write.Success)
            throw new InvalidOperationException($"Could not update structure {structureId} description: {write.ErrorMsg}");
        await gameStoreCache.FlushTableAsync("structures", call.Cancellation);
        return true;
    }

    public async ValueTask<IReadOnlyList<long>> GetStructureIdsInSolarSystemAsync(
        long solarSystemId,
        CancellationToken cancellationToken)
    {
        if (solarSystemId <= 0)
            return Array.Empty<long>();

        var state = await readTable("structures", cancellationToken);
        var ids = new List<long>();
        foreach (var (key, node) in state)
        {
            if (node is JsonObject structure &&
                long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var structureId) &&
                IsPersistentlyDeployedStructure(structure, structureId) &&
                Integer(structure["solarSystemID"]) == solarSystemId &&
                Integer((structure["serviceStates"] as JsonObject)?[DockingServiceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) == 1)
                ids.Add(structureId);
        }

        ids.Sort();
        return ids;
    }

    public async ValueTask<bool> CharacterHasServiceAsync(
        long characterId,
        long structureId,
        long serviceId,
        CancellationToken cancellationToken)
    {
        if (serviceId <= 0 || serviceId > 9_007_199_254_740_991L)
            return false;
        var settingId = SettingIdForService(serviceId);
        if (settingId is null)
            return false;

        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        return context is { } access &&
            ProfileSettingAllows(
                access.Structure,
                access.OwnerCorporationId,
                (access.CharacterId, access.Affiliation),
                access.ProfileState,
                access.AccessGroupState,
                settingId.Value) &&
            access.Structure["serviceStates"] is JsonObject services &&
            Integer(services[serviceId.ToString(System.Globalization.CultureInfo.InvariantCulture)]) == 1;
    }

    public async ValueTask<bool> CharacterHasSettingAsync(
        long characterId,
        long structureId,
        long settingId,
        CancellationToken cancellationToken)
    {
        if (settingId <= 0 || settingId > int.MaxValue)
            return false;
        var context = await ReadStructureSettingsContextAsync(characterId, structureId, cancellationToken);
        return context is { } access &&
            ProfileSettingAllows(
                access.Structure,
                access.OwnerCorporationId,
                (access.CharacterId, access.Affiliation),
                access.ProfileState,
                access.AccessGroupState,
                (int)settingId);
    }

    private async ValueTask<(
        JsonObject Structure,
        JsonObject Profile,
        JsonObject ProfileState,
        JsonObject AccessGroupState,
        long OwnerCorporationId,
        long OwnerAllianceId,
        long CharacterId,
        Eve.Relations.Affiliation Affiliation)?> ReadStructureSettingsContextAsync(
        long characterId,
        long structureId,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0 || structureId <= 0)
            return null;

        var structureKey = structureId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var structures = await readTable("structures", cancellationToken);
        if (structures[structureKey] is not JsonObject structure || !IsPersistentlyDeployedStructure(structure, structureId))
            return null;

        var ownerCorporationId = Positive(Integer(structure["ownerCorpID"] ?? structure["corporationID"]));
        if (ownerCorporationId <= 0)
            return null;
        var ownerAllianceId = Math.Max(0, Integer(structure["allianceID"]));

        var profileId = Integer(structure["profileID"]);
        if (profileId < 0)
            return null;
        var profileState = await readTable("structureProfiles", cancellationToken);
        var profiles = profileState["profilesByID"] as JsonObject ?? profileState;
        JsonObject? profile = profileId > 0
            ? profiles[profileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));

        if (profileId > 0 &&
            (profile is null || Integer(profile["corporationID"]) != ownerCorporationId))
            return null;
        if (profile is not null && Integer(profile["corporationID"]) != ownerCorporationId)
            return null;
        if (profile is null)
        {
            if (profileId > 0)
                return null;
            profile = new JsonObject
            {
                ["corporationID"] = ownerCorporationId,
                ["isDefault"] = true,
                ["settingsBySettingID"] = new JsonObject(),
            };
        }
        else if (profile["settingsBySettingID"] is not JsonObject)
        {
            if (profileId > 0 && !Truthy(profile["isDefault"]))
                return null;
            profile["settingsBySettingID"] = new JsonObject();
        }

        var accessGroupState = await readTable("accessGroups", cancellationToken);
        var affiliation = await affiliationOf(characterId, cancellationToken);
        return (
            structure,
            profile,
            profileState,
            accessGroupState,
            ownerCorporationId,
            ownerAllianceId,
            characterId,
            affiliation);
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
        if (requestedProfileId < 0)
            return false;

        JsonObject? profile = requestedProfileId > 0
            ? profiles[requestedProfileId.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject
            : profiles.Select(pair => pair.Value)
                .OfType<JsonObject>()
                .FirstOrDefault(candidate =>
                    Integer(candidate["corporationID"]) == ownerCorporationId && Truthy(candidate["isDefault"]));

        if (requestedProfileId > 0 &&
            (profile is null || Integer(profile["corporationID"]) != ownerCorporationId))
            return false;
        if (profile is not null && Integer(profile["corporationID"]) != ownerCorporationId)
            return false;
        if (profile is null)
            return requestedProfileId <= 0 && ownerCorporationId > 0 &&
                access.Affiliation.Corporation == ownerCorporationId;

        var settings = profile["settingsBySettingID"] as JsonObject;
        if (settings?[settingId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonArray settingGroups ||
            settingGroups.Count == 0)
        {
            return ownerCorporationId > 0 &&
                access.Affiliation.Corporation == ownerCorporationId &&
                (requestedProfileId <= 0 || Truthy(profile["isDefault"]));
        }

        var hasDefaultGroup = false;
        var bestRank = 0;
        foreach (var node in settingGroups)
        {
            if (node is not JsonObject row)
                continue;
            var groupId = Integer(row["groupID"], -1);
            if (groupId < 0)
                return false;
            if (groupId == 0)
            {
                // The built-in default access list contains only the owning corporation.
                hasDefaultGroup |= ownerCorporationId > 0 &&
                    access.Affiliation.Corporation == ownerCorporationId;
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

    private static long SdeInteger(Eve.Sde.SdeRow row, string field)
    {
        if (!row.Doc.TryGetProperty(field, out var value))
            return 0;
        if (value.TryGetInt64(out var integer))
            return integer;
        return value.TryGetDouble(out var number) && double.IsFinite(number)
            ? (long)Math.Truncate(number)
            : 0;
    }

    private static double SdeDouble(Eve.Sde.SdeRow row, string field) =>
        row.Doc.TryGetProperty(field, out var value) &&
        value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : 0;

    private static bool SdeBoolean(Eve.Sde.SdeRow row, string field)
    {
        if (!row.Doc.TryGetProperty(field, out var value))
            return false;
        if (value.ValueKind is System.Text.Json.JsonValueKind.True)
            return true;
        if (value.ValueKind is System.Text.Json.JsonValueKind.Number)
            return value.TryGetInt64(out var integer) && integer != 0;
        return false;
    }

    private static PyValue StructureSettingValue(JsonNode? value)
    {
        if (value is null)
            return PyBool.True;
        if (value is JsonValue scalar)
        {
            if (scalar.TryGetValue<bool>(out var boolean))
                return PyValue.Of(boolean);
            if (scalar.TryGetValue<int>(out var int32))
                return new PyInt(int32);
            if (scalar.TryGetValue<long>(out var int64))
                return new PyLong(int64);
            if (scalar.TryGetValue<double>(out var real) && double.IsFinite(real))
                return new PyReal(real);
            if (scalar.TryGetValue<string>(out var text))
                return new PyStr(text);
        }
        return new PyStr(value.ToString());
    }
}

public sealed record SovSkyhookAcquisitionCharge(long CorporationId, decimal AmountIsk, int ReferenceTypeId, string OperationKey, bool IsRefund = false);

public sealed class PersistenceSovFwWriteOperations(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<string, CancellationToken, Task<JsonObject>> readPersistedTableAsync,
    Func<Eve.Services.ServiceCall, long, Eve.Services.Core.SovHubRecord, ValueTask<bool>> authorizeHubAsync,
    SemaphoreSlim writeGate,
    Func<IReadOnlyList<SovSkyhookAcquisitionCharge>, CancellationToken, ValueTask<bool>> chargeSkyhooksAsync,
    Func<SovSkyhookAcquisitionCharge, CancellationToken, ValueTask<bool>> hasWalletOperationAsync)
{
    private const decimal SkyhookBpoPriceProxyIsk = 5_500_000_000m;
    // ponytail: CCP has not published the sovereignty-capture conversion fee; the 5.5B BPO price is only a deterministic proxy when row pricing is absent.
    private const int SkyhookClaimFeeWalletReferenceTypeId = 184; // ESI wallet journal ref type skyhook_claim_fee.

    private static readonly HashSet<string> SolarSystemKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "solarSystemID", "systemID", "sourceSolarSystemID", "destinationSolarSystemID",
        "fromSolarSystemID", "toSolarSystemID", "importSolarSystemID", "exportSolarSystemID",
        "targetSolarSystemID",
    };

    public async ValueTask<bool> CanManageHubAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord expected)
    {
        if (solarSystemId <= 0 || expected.HubId is not > 0)
            return false;

        var state = await readPersistedTableAsync("sovereignty", call.Cancellation);
        if (!TrySystem(state, solarSystemId, out var system) || !TryHub(system, out var current) ||
            current.HubId != expected.HubId ||
            current.CorporationId != expected.CorporationId ||
            current.AllianceId != expected.AllianceId ||
            current.ClaimTimeFileTime != expected.ClaimTimeFileTime)
            return false;

        return await authorizeHubAsync(call, solarSystemId, current);
    }

    public async ValueTask<Eve.Marshal.PyValue> SetFuelAccessGroupAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        long? groupId)
    {
        if (groupId is <= 0)
            return Eve.Marshal.PyBool.False;
        if (groupId is { } accessGroupId)
        {
            var groups = await readPersistedTableAsync("accessGroups", call.Cancellation);
            if (groups[accessGroupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject)
                return Eve.Marshal.PyBool.False;
        }

        var written = await MutateSystemAsync(
            call,
            solarSystemId,
            row =>
            {
                row["fuelAccessGroupID"] = groupId is { } value ? JsonValue.Create(value) : null;
                return true;
            },
            row => NullablePositive(row["fuelAccessGroupID"]) == groupId);
        return written ? Eve.Marshal.PyNone.Instance : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<Eve.Marshal.PyValue> SaveUpgradeConfigurationAsync(
        Eve.Services.ServiceCall call,
        long hubId,
        IReadOnlyList<long> itemIds,
        IReadOnlyList<Eve.Services.Core.SovHubUpgradeState> states)
    {
        if (hubId <= 0 || itemIds.Count != states.Count ||
            itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count ||
            states.Any(state => state.TypeId <= 0) ||
            states.Select(state => state.TypeId).Distinct().Count() != states.Count)
            return Eve.Marshal.PyBool.False;

        var snapshot = await readPersistedTableAsync("sovereignty", call.Cancellation);
        var systemId = FindHubSystemId(snapshot, hubId);
        if (systemId <= 0 || !TrySystem(snapshot, systemId, out var current) || !TryHub(current, out var hub))
            return Eve.Marshal.PyBool.False;

        var upgrades = new JsonArray();
        for (var index = 0; index < states.Count; index++)
            upgrades.Add(new JsonObject
            {
                ["itemID"] = itemIds[index],
                ["typeID"] = states[index].TypeId,
                ["online"] = states[index].Online,
                ["priority"] = index,
            });

        var written = await MutateSystemAsync(
            call,
            systemId,
            row =>
            {
                row["hubUpgradeConfiguration"] = upgrades.DeepClone();
                return true;
            },
            row => JsonNode.DeepEquals(row["hubUpgradeConfiguration"], upgrades),
            hub.HubId);
        return written ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<Eve.Marshal.PyValue> SelfDestructUpgradeAsync(
        Eve.Services.ServiceCall call,
        long hubId,
        long typeId)
    {
        if (hubId <= 0 || typeId <= 0)
            return Eve.Marshal.PyBool.False;

        var snapshot = await readPersistedTableAsync("sovereignty", call.Cancellation);
        var systemId = FindHubSystemId(snapshot, hubId);
        if (systemId <= 0 || !TrySystem(snapshot, systemId, out var current) || !TryHub(current, out var hub))
            return Eve.Marshal.PyBool.False;

        var removed = await MutateSystemAsync(
            call,
            systemId,
            row =>
            {
                if (row["hubUpgradeConfiguration"] is not JsonArray configured)
                    return false;
                var remaining = new JsonArray();
                var found = false;
                foreach (var node in configured)
                {
                    if (node is JsonObject upgrade && Positive(Integer(upgrade["typeID"])) == typeId)
                    {
                        found = true;
                        continue;
                    }
                    remaining.Add(node?.DeepClone());
                }
                if (!found)
                    return false;
                row["hubUpgradeConfiguration"] = remaining;
                return true;
            },
            row => row["hubUpgradeConfiguration"] is JsonArray values &&
                !values.OfType<JsonObject>().Any(upgrade => Positive(Integer(upgrade["typeID"])) == typeId),
            hub.HubId);
        return removed ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<Eve.Marshal.PyValue> SaveWorkforceConfigurationAsync(
        Eve.Services.ServiceCall call,
        long hubId,
        Eve.Marshal.PyValue configuration)
    {
        if (hubId <= 0 || configuration is Eve.Marshal.PyNone)
            return Eve.Marshal.PyBool.False;

        var snapshot = await readPersistedTableAsync("sovereignty", call.Cancellation);
        var systemId = FindHubSystemId(snapshot, hubId);
        if (systemId <= 0 || !TrySystem(snapshot, systemId, out var current) || !TryHub(current, out var hub) ||
            hub.AllianceId is not > 0)
            return Eve.Marshal.PyBool.False;

        var payload = ToJson(configuration);
        if (payload is null || !TargetsSameAlliance(payload, snapshot["systems"] as JsonObject, hub.AllianceId.Value))
            return Eve.Marshal.PyBool.False;

        var written = await MutateSystemAsync(
            call,
            systemId,
            row =>
            {
                row["workforceConfiguration"] = payload.DeepClone();
                return true;
            },
            row => JsonNode.DeepEquals(row["workforceConfiguration"], payload),
            hub.HubId);
        return written ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
    }

    public async ValueTask<IReadOnlyList<long>> DestroySkyhooksAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub,
        IReadOnlyList<long> skyhookIds)
    {
        if (solarSystemId <= 0 || hub.HubId is not > 0 || hub.CorporationId is not > 0)
            return Array.Empty<long>();

        await writeGate.WaitAsync(call.Cancellation);
        try
        {
            if (!await CanManageHubAsync(call, solarSystemId, hub))
                return Array.Empty<long>();

            var root = readCacheTable("sovereignty");
            if (root["skyhooks"] is not JsonObject rows)
                return Array.Empty<long>();

            var changed = new HashSet<long>();
            foreach (var skyhookId in skyhookIds.Distinct())
            {
                if (skyhookId <= 0)
                    continue;
                var key = skyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (rows[key] is not JsonObject row ||
                    Positive(Integer(row["typeID"])) != 81080 ||
                    Positive(Integer(row["solarSystemID"])) != solarSystemId)
                    continue;
                if (Flag(row["destroyed"]))
                {
                    changed.Add(skyhookId);
                    continue;
                }

                var updated = (JsonObject)row.DeepClone();
                updated["destroyed"] = true;
                updated["productionActive"] = false;
                updated["resourceVersion"] = Guid.NewGuid().ToString("N");
                updated["destroyedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var result = write("sovereignty", $"/skyhooks/{key}", updated);
                if (!result.Success)
                    throw new InvalidOperationException($"Could not persist Skyhook destruction for {skyhookId}: {result.ErrorMsg}");
                changed.Add(skyhookId);
            }

            if (changed.Count == 0)
                return Array.Empty<long>();

            await flushTableAsync("sovereignty", CancellationToken.None);
            var persisted = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            var durableRows = persisted["skyhooks"] as JsonObject;
            return changed.Where(id =>
            {
                var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return durableRows?[key] is JsonObject row && Flag(row["destroyed"]) &&
                    !Flag(row["productionActive"]);
            }).Order().ToArray();
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<long>> AcquireSkyhooksAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub,
        IReadOnlyList<long> skyhookIds,
        long? accessListId)
    {
        if (solarSystemId <= 0 || hub.HubId is not > 0 || hub.CorporationId is not > 0 ||
            hub.AllianceId is not > 0 || accessListId is <= 0)
            return Array.Empty<long>();

        if (accessListId is { } groupId)
        {
            var groups = await readPersistedTableAsync("accessGroups", call.Cancellation);
            if (groups[groupId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject)
                return Array.Empty<long>();
        }

        await writeGate.WaitAsync(call.Cancellation);
        try
        {
            if (!await CanManageHubAsync(call, solarSystemId, hub))
                return Array.Empty<long>();
            var claimTime = hub.ClaimTimeFileTime ?? 0;
            var nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
            // CCP's Legion major-update notes extended the Skyhook management window to 24 hours.
            if (claimTime <= 0 || nowFileTime < claimTime ||
                nowFileTime - claimTime > TimeSpan.TicksPerDay)
                return Array.Empty<long>();

            var requested = skyhookIds.Where(id => id > 0).Distinct().Order().ToArray();
            if (requested.Length == 0)
                return Array.Empty<long>();

            var fingerprintSource = $"{hub.CorporationId}:{hub.AllianceId}:{hub.HubId}:{solarSystemId}:{accessListId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}:{string.Join(",", requested)}";
            var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
            var receiptKey = "sov-skyhook-acquire-" + fingerprint;
            var receiptPath = $"/skyhookAcquisitionSettlements/{receiptKey}";
            var durableRoot = await readPersistedTableAsync("sovereignty", call.Cancellation);
            var receipt = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;

            if (receipt is null)
            {
                var cachedRoot = readCacheTable("sovereignty");
                var cachedReceipt = (cachedRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                if (cachedReceipt is not null)
                {
                    var cachedWrite = write("sovereignty", receiptPath, cachedReceipt);
                    if (!cachedWrite.Success)
                        throw new InvalidOperationException($"Could not restage Skyhook acquisition receipt {receiptKey}: {cachedWrite.ErrorMsg}");
                    await flushTableAsync("sovereignty", CancellationToken.None);
                    durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                    receipt = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                }
            }

            if (receipt is not null &&
                (!string.Equals(receipt["requestFingerprint"]?.ToString(), fingerprint, StringComparison.Ordinal) ||
                 Positive(Integer(receipt["corporationID"])) != hub.CorporationId ||
                 Positive(Integer(receipt["allianceID"])) != hub.AllianceId ||
                 Positive(Integer(receipt["solarSystemID"])) != solarSystemId ||
                 Positive(Integer(receipt["hubID"])) != hub.HubId ||
                 NullablePositive(receipt["accessListID"]) != accessListId))
                return Array.Empty<long>();

            if (receipt is null)
            {
                if (!TrySystem(durableRoot, solarSystemId, out var system) ||
                    Positive(Integer(system["infrastructureHubID"])) != hub.HubId ||
                    Positive(Integer(system["corporationID"])) != hub.CorporationId ||
                    Positive(Integer(system["allianceID"])) != hub.AllianceId ||
                    durableRoot["skyhooks"] is not JsonObject rows)
                    return Array.Empty<long>();

                var processedIds = new HashSet<long>();
                var expectedRows = new JsonObject();
                var desiredRows = new JsonObject();
                var charges = new List<SovSkyhookAcquisitionCharge>();
                foreach (var skyhookId in requested)
                {
                    var key = skyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (rows[key] is not JsonObject row ||
                        Positive(Integer(row["typeID"])) != 81080 ||
                        Positive(Integer(row["solarSystemID"])) != solarSystemId ||
                        Positive(Integer(row["planetID"])) <= 0 ||
                        Flag(row["destroyed"]))
                        continue;

                    var owner = Positive(Integer(row["corporationID"] ?? row["ownerCorpID"]));
                    var planetId = Positive(Integer(row["planetID"]));
                    var anotherSkyhook = rows.Any(pair => pair.Key != key && pair.Value is JsonObject other &&
                        Positive(Integer(other["solarSystemID"])) == solarSystemId &&
                        Positive(Integer(other["planetID"])) == planetId &&
                        !Flag(other["destroyed"]));
                    if (anotherSkyhook)
                        continue;

                    var accessMatches = NullablePositive(row["accessListID"]) == accessListId;
                    if (owner == hub.CorporationId && Positive(Integer(row["allianceID"])) == hub.AllianceId && accessMatches)
                    {
                        processedIds.Add(skyhookId);
                        continue;
                    }

                    expectedRows[key] = row.DeepClone();
                    var updated = (JsonObject)row.DeepClone();
                    updated["corporationID"] = hub.CorporationId.Value;
                    updated["ownerCorpID"] = hub.CorporationId.Value;
                    updated["allianceID"] = hub.AllianceId.Value;
                    updated["accessListID"] = accessListId is { } access ? JsonValue.Create(access) : null;
                    if (owner != hub.CorporationId)
                    {
                        decimal price;
                        if (row["acquisitionPriceISK"] is null)
                            price = SkyhookBpoPriceProxyIsk;
                        else if (!decimal.TryParse(row["acquisitionPriceISK"]?.ToString(),
                                     System.Globalization.NumberStyles.Number,
                                     System.Globalization.CultureInfo.InvariantCulture, out price) ||
                                 price <= 0 || decimal.Round(price, 2) != price)
                            continue;

                        int refTypeId;
                        if (row["acquisitionWalletRefTypeID"] is null)
                            refTypeId = SkyhookClaimFeeWalletReferenceTypeId;
                        else if (!int.TryParse(row["acquisitionWalletRefTypeID"]?.ToString(),
                                     System.Globalization.NumberStyles.Integer,
                                     System.Globalization.CultureInfo.InvariantCulture, out refTypeId) ||
                                 refTypeId <= 0)
                            continue;

                        var resourceVersion = row["resourceVersion"]?.ToString() ?? "initial";
                        var operationIdentity = $"{hub.CorporationId}:{skyhookId}:{resourceVersion}";
                        var operationKey = "sov-skyhook-acquire:" +
                            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                                System.Text.Encoding.UTF8.GetBytes(operationIdentity))).ToLowerInvariant();
                        charges.Add(new(hub.CorporationId.Value, price, refTypeId, operationKey));
                        updated["productionActive"] = false;
                        updated["acquisitionPaidISK"] = price;
                        updated["acquisitionWalletOperationKey"] = operationKey;
                    }

                    updated["resourceVersion"] = Guid.NewGuid().ToString("N");
                    updated["acquiredAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    desiredRows[key] = updated;
                    processedIds.Add(skyhookId);
                }

                if (processedIds.Count == 0)
                    return Array.Empty<long>();

                var chargeRows = new JsonArray();
                foreach (var charge in charges)
                    chargeRows.Add(new JsonObject
                    {
                        ["corporationID"] = charge.CorporationId,
                        ["amountISK"] = charge.AmountIsk,
                        ["referenceTypeID"] = charge.ReferenceTypeId,
                        ["operationKey"] = charge.OperationKey,
                    });
                var processedRows = new JsonArray();
                foreach (var id in processedIds.Order())
                    processedRows.Add(id);

                receipt = new JsonObject
                {
                    ["requestFingerprint"] = fingerprint,
                    ["state"] = "pending",
                    ["corporationID"] = hub.CorporationId.Value,
                    ["allianceID"] = hub.AllianceId.Value,
                    ["solarSystemID"] = solarSystemId,
                    ["hubID"] = hub.HubId.Value,
                    ["accessListID"] = accessListId is { } storedAccess ? JsonValue.Create(storedAccess) : null,
                    ["processedSkyhookIDs"] = processedRows,
                    ["expectedSkyhooks"] = expectedRows,
                    ["desiredSkyhooks"] = desiredRows,
                    ["charges"] = chargeRows,
                    ["createdAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                var intentWrite = write("sovereignty", receiptPath, receipt);
                if (!intentWrite.Success)
                    throw new InvalidOperationException($"Could not persist Skyhook acquisition receipt {receiptKey}: {intentWrite.ErrorMsg}");
                await flushTableAsync("sovereignty", CancellationToken.None);
                durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                receipt = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                if (receipt is null || receipt["state"]?.ToString() != "pending" ||
                    !string.Equals(receipt["requestFingerprint"]?.ToString(), fingerprint, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} was not durably recorded.");
            }

            var receiptState = receipt["state"]?.ToString();
            var receiptIds = (receipt["processedSkyhookIDs"] as JsonArray ?? [])
                .Select(node => Positive(Integer(node)))
                .Where(id => id > 0)
                .Distinct()
                .ToArray();
            if (receiptState == "completed")
                return await ReadCommittedAcquisitionsAsync(
                    receiptIds, solarSystemId, hub, accessListId, CancellationToken.None);
            if (receiptState != "pending")
                return Array.Empty<long>();

            var receiptCharges = new List<SovSkyhookAcquisitionCharge>();
            foreach (var node in receipt["charges"] as JsonArray ?? [])
            {
                if (node is not JsonObject charge ||
                    Positive(Integer(charge["corporationID"])) != hub.CorporationId ||
                    !decimal.TryParse(charge["amountISK"]?.ToString(),
                        System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                    amount <= 0 ||
                    !int.TryParse(charge["referenceTypeID"]?.ToString(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var referenceTypeId) ||
                    referenceTypeId <= 0 ||
                    string.IsNullOrWhiteSpace(charge["operationKey"]?.ToString()))
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} contains invalid settlement data.");

                receiptCharges.Add(new(
                    hub.CorporationId.Value,
                    amount,
                    referenceTypeId,
                    charge["operationKey"]!.ToString()));
            }

            if (receiptCharges.Count > 0 && !await chargeSkyhooksAsync(receiptCharges, call.Cancellation))
                return Array.Empty<long>();

            var desired = receipt["desiredSkyhooks"] as JsonObject ?? new JsonObject();
            var cacheRows = (readCacheTable("sovereignty")["skyhooks"] as JsonObject) ??
                throw new InvalidOperationException("Skyhook table disappeared during acquisition replay.");
            foreach (var (key, node) in desired)
            {
                if (node is not JsonObject desiredRow || cacheRows[key] is not JsonObject currentRow)
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} has an invalid row payload.");
                var rowWrite = write("sovereignty", $"/skyhooks/{key}", MergeSkyhookAcquisitionRow(currentRow, desiredRow));
                if (!rowWrite.Success)
                    throw new InvalidOperationException($"Could not replay Skyhook acquisition row {key}: {rowWrite.ErrorMsg}");
            }

            await flushTableAsync("sovereignty", CancellationToken.None);
            durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            var durableRows = durableRoot["skyhooks"] as JsonObject;
            foreach (var (key, node) in desired)
                if (node is not JsonObject desiredRow ||
                    durableRows?[key] is not JsonObject savedRow ||
                    !MatchesSkyhookAcquisition(savedRow, desiredRow))
                    throw new InvalidOperationException($"Skyhook acquisition row {key} was not durably committed; receipt {receiptKey} remains pending.");

            var completedReceipt = (JsonObject)receipt.DeepClone();
            completedReceipt["state"] = "completed";
            completedReceipt["completedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var receiptWrite = write("sovereignty", receiptPath, completedReceipt);
            if (!receiptWrite.Success)
                throw new InvalidOperationException($"Could not complete Skyhook acquisition receipt {receiptKey}: {receiptWrite.ErrorMsg}");
            await flushTableAsync("sovereignty", CancellationToken.None);

            durableRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            var completed = (durableRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
            if (completed is null || completed["state"]?.ToString() != "completed" ||
                !string.Equals(completed["requestFingerprint"]?.ToString(), fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} was not durably completed.");

            return await ReadCommittedAcquisitionsAsync(
                receiptIds, solarSystemId, hub, accessListId, CancellationToken.None);
        }
        finally
        {
            writeGate.Release();
        }
    }

public async Task ReplayPendingSkyhookAcquisitionsAsync(CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            var initial = await readPersistedTableAsync("sovereignty", cancellationToken);
            var pendingKeys = (initial["skyhookAcquisitionSettlements"] as JsonObject)?
                .Where(pair => pair.Value is JsonObject receipt && receipt["state"]?.ToString() == "pending")
                .Select(pair => pair.Key)
                .ToArray() ?? Array.Empty<string>();

            foreach (var receiptKey in pendingKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = await readPersistedTableAsync("sovereignty", cancellationToken);
                var receipt = (root["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey] as JsonObject;
                if (receipt is null || receipt["state"]?.ToString() != "pending")
                    continue;

                var corporationId = Positive(Integer(receipt["corporationID"]));
                var allianceId = Positive(Integer(receipt["allianceID"]));
                var systemId = Positive(Integer(receipt["solarSystemID"]));
                var hubId = Positive(Integer(receipt["hubID"]));
                var accessListId = NullablePositive(receipt["accessListID"]);
                var charges = new List<SovSkyhookAcquisitionCharge>();
                var invalidReceipt = corporationId <= 0 || allianceId <= 0 || systemId <= 0 || hubId <= 0;
                foreach (var node in receipt["charges"] as JsonArray ?? [])
                {
                    if (node is not JsonObject charge ||
                        Positive(Integer(charge["corporationID"])) != corporationId ||
                        !decimal.TryParse(charge["amountISK"]?.ToString(),
                            System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.InvariantCulture, out var amount) ||
                        amount <= 0 ||
                        !int.TryParse(charge["referenceTypeID"]?.ToString(),
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var referenceTypeId) ||
                        referenceTypeId <= 0 ||
                        string.IsNullOrWhiteSpace(charge["operationKey"]?.ToString()))
                    {
                        invalidReceipt = true;
                        break;
                    }
                    charges.Add(new(corporationId, amount, referenceTypeId, charge["operationKey"]!.ToString()));
                }

                var desiredRows = receipt["desiredSkyhooks"] as JsonObject;
                var expectedRows = receipt["expectedSkyhooks"] as JsonObject;
                if (desiredRows is null || expectedRows is null)
                    invalidReceipt = true;

                var systemIsCurrent = TrySystem(root, systemId, out var system) &&
                    Positive(Integer(system["infrastructureHubID"])) == hubId &&
                    Positive(Integer(system["corporationID"])) == corporationId &&
                    Positive(Integer(system["allianceID"])) == allianceId;
                var skyhooks = root["skyhooks"] as JsonObject;
                var rowsAreCurrent = systemIsCurrent && !invalidReceipt && skyhooks is not null;
                if (rowsAreCurrent)
                {
                    foreach (var (key, node) in desiredRows!)
                    {
                        if (node is not JsonObject desired || expectedRows![key] is not JsonObject expected ||
                            skyhooks![key] is not JsonObject current ||
                            Flag(current["destroyed"]) ||
                            Positive(Integer(current["typeID"])) != 81080 ||
                            Positive(Integer(current["solarSystemID"])) != systemId ||
                            Positive(Integer(current["planetID"])) != Positive(Integer(expected["planetID"])))
                        {
                            rowsAreCurrent = false;
                            break;
                        }

                        var currentOwner = Positive(Integer(current["corporationID"] ?? current["ownerCorpID"]));
                        var expectedOwner = Positive(Integer(expected["corporationID"] ?? expected["ownerCorpID"]));
                        if (currentOwner != expectedOwner && currentOwner != corporationId)
                        {
                            rowsAreCurrent = false;
                            break;
                        }

                        var planetId = Positive(Integer(current["planetID"]));
                        if (skyhooks.Any(pair => pair.Key != key && pair.Value is JsonObject other &&
                                Positive(Integer(other["solarSystemID"])) == systemId &&
                                Positive(Integer(other["planetID"])) == planetId &&
                                !Flag(other["destroyed"])))
                        {
                            rowsAreCurrent = false;
                            break;
                        }
                    }
                }

                if (!rowsAreCurrent)
                {
                    var refunds = new List<SovSkyhookAcquisitionCharge>();
                    foreach (var charge in charges)
                    {
                        if (!await hasWalletOperationAsync(charge, cancellationToken))
                            continue;
                        var refund = charge with
                        {
                            OperationKey = charge.OperationKey + ":refund",
                            IsRefund = true,
                        };
                        if (!await hasWalletOperationAsync(refund, cancellationToken))
                            refunds.Add(refund);
                    }
                    if (refunds.Count > 0 && !await chargeSkyhooksAsync(refunds, cancellationToken))
                        continue;

                    var refundedReceipt = (JsonObject)receipt.DeepClone();
                    refundedReceipt["state"] = "refunded";
                    refundedReceipt["resolvedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    refundedReceipt["resolution"] = "sovereignty-or-skyhook-state-changed";
                    var refundReceiptWrite = write("sovereignty", $"/skyhookAcquisitionSettlements/{receiptKey}", refundedReceipt);
                    if (!refundReceiptWrite.Success)
                        throw new InvalidOperationException($"Could not resolve Skyhook acquisition receipt {receiptKey}: {refundReceiptWrite.ErrorMsg}");
                    await flushTableAsync("sovereignty", CancellationToken.None);
                    var refundedRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                    if ((refundedRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey]?["state"]?.ToString() != "refunded")
                        throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} refund was not durable.");
                    continue;
                }

                if (charges.Count > 0 && !await chargeSkyhooksAsync(charges, cancellationToken))
                    continue;

                var cacheRows = (readCacheTable("sovereignty")["skyhooks"] as JsonObject) ??
                    throw new InvalidOperationException("Skyhook table disappeared during acquisition recovery.");
                foreach (var (key, node) in desiredRows!)
                {
                    if (node is not JsonObject desired || cacheRows[key] is not JsonObject current)
                        throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} has an invalid row payload.");
                    var rowWrite = write("sovereignty", $"/skyhooks/{key}", MergeSkyhookAcquisitionRow(current, desired));
                    if (!rowWrite.Success)
                        throw new InvalidOperationException($"Could not replay Skyhook acquisition row {key}: {rowWrite.ErrorMsg}");
                }

                await flushTableAsync("sovereignty", CancellationToken.None);
                var committed = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                var committedRows = committed["skyhooks"] as JsonObject;
                foreach (var (key, node) in desiredRows!)
                    if (node is not JsonObject desired ||
                        committedRows?[key] is not JsonObject saved ||
                        !MatchesSkyhookAcquisition(saved, desired))
                        throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} remains pending after row readback.");

                var completedReceipt = (JsonObject)receipt.DeepClone();
                completedReceipt["state"] = "completed";
                completedReceipt["completedAtUnixMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var completedWrite = write("sovereignty", $"/skyhookAcquisitionSettlements/{receiptKey}", completedReceipt);
                if (!completedWrite.Success)
                    throw new InvalidOperationException($"Could not complete Skyhook acquisition receipt {receiptKey}: {completedWrite.ErrorMsg}");
                await flushTableAsync("sovereignty", CancellationToken.None);
                var completedRoot = await readPersistedTableAsync("sovereignty", CancellationToken.None);
                if ((completedRoot["skyhookAcquisitionSettlements"] as JsonObject)?[receiptKey]?["state"]?.ToString() != "completed")
                    throw new InvalidOperationException($"Skyhook acquisition receipt {receiptKey} completion was not durable.");
            }
        }
        finally
        {
            writeGate.Release();
        }
    }

private static JsonObject MergeSkyhookAcquisitionRow(JsonObject current, JsonObject desired)
    {
        var merged = (JsonObject)current.DeepClone();
        foreach (var field in new[]
        {
            "corporationID", "ownerCorpID", "allianceID", "accessListID", "productionActive",
            "resourceVersion", "acquiredAtUnixMs", "acquisitionPaidISK", "acquisitionWalletOperationKey",
        })
            if (desired.ContainsKey(field))
                merged[field] = desired[field]?.DeepClone();
        return merged;
    }

    private static bool MatchesSkyhookAcquisition(JsonObject saved, JsonObject desired)
    {
        foreach (var field in new[]
        {
            "corporationID", "ownerCorpID", "allianceID", "accessListID", "productionActive",
            "resourceVersion", "acquiredAtUnixMs", "acquisitionPaidISK", "acquisitionWalletOperationKey",
        })
            if (desired.ContainsKey(field) && !JsonNode.DeepEquals(saved[field], desired[field]))
                return false;
        return true;
    }

private async ValueTask<IReadOnlyList<long>> ReadCommittedAcquisitionsAsync(
        IReadOnlyList<long> ids,
        long solarSystemId,
        Eve.Services.Core.SovHubRecord hub,
        long? accessListId,
        CancellationToken cancellationToken)
    {
        var root = await readPersistedTableAsync("sovereignty", cancellationToken);
        var rows = root["skyhooks"] as JsonObject;
        if (rows is null)
            return Array.Empty<long>();

        return ids.Distinct().Where(id =>
        {
            var key = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return rows[key] is JsonObject row &&
                !Flag(row["destroyed"]) &&
                Positive(Integer(row["corporationID"] ?? row["ownerCorpID"])) == hub.CorporationId &&
                Positive(Integer(row["allianceID"])) == hub.AllianceId &&
                Positive(Integer(row["solarSystemID"])) == solarSystemId &&
                Positive(Integer(row["planetID"])) > 0 &&
                NullablePositive(row["accessListID"]) == accessListId;
        }).Order().ToArray();
    }

    private async Task<bool> MutateSystemAsync(
        Eve.Services.ServiceCall call,
        long solarSystemId,
        Func<JsonObject, bool> mutate,
        Func<JsonObject, bool> verify,
        long? expectedHubId = null)
    {
        if (solarSystemId <= 0)
            return false;

        await writeGate.WaitAsync(call.Cancellation);
        try
        {
            var cache = readCacheTable("sovereignty");
            if (!TrySystem(cache, solarSystemId, out var current) || !TryHub(current, out var hub) ||
                (expectedHubId is { } expected && hub.HubId != expected) ||
                !await CanManageHubAsync(call, solarSystemId, hub))
                return false;

            var updated = (JsonObject)current.DeepClone();
            if (!mutate(updated))
                return false;

            var key = solarSystemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var result = write("sovereignty", $"/systems/{key}", updated);
            if (!result.Success)
                throw new InvalidOperationException($"Could not persist sovereignty system {solarSystemId}: {result.ErrorMsg}");

            await flushTableAsync("sovereignty", CancellationToken.None);
            var persisted = await readPersistedTableAsync("sovereignty", CancellationToken.None);
            return TrySystem(persisted, solarSystemId, out var saved) && verify(saved);
        }
        finally
        {
            writeGate.Release();
        }
    }

    private static long FindHubSystemId(JsonObject root, long hubId)
    {
        if (root["systems"] is not JsonObject systems)
            return 0;
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system || Positive(Integer(system["infrastructureHubID"])) != hubId)
                continue;
            return long.TryParse(key, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
        }
        return 0;
    }

    private static bool TrySystem(JsonObject root, long systemId, out JsonObject system)
    {
        system = null!;
        return root["systems"] is JsonObject systems &&
            systems[systemId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is JsonObject found &&
            (system = found) is not null;
    }

    private static bool TryHub(JsonObject system, out Eve.Services.Core.SovHubRecord hub)
    {
        var id = Positive(Integer(system["infrastructureHubID"]));
        if (id <= 0)
        {
            hub = null!;
            return false;
        }
        hub = new(
            id,
            NullablePositive(system["corporationID"]),
            NullablePositive(system["allianceID"]),
            NullablePositive(system["claimTime"]));
        return true;
    }

    private static bool TargetsSameAlliance(JsonNode payload, JsonObject? systems, long allianceId)
    {
        if (systems is null)
            return false;
        return Visit(payload);

        bool Visit(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var (key, value) in obj)
                {
                    if (SolarSystemKeys.Contains(key))
                    {
                        if (!long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
                                System.Globalization.CultureInfo.InvariantCulture, out var targetId) || targetId <= 0 ||
                            systems[targetId.ToString(System.Globalization.CultureInfo.InvariantCulture)] is not JsonObject target ||
                            Positive(Integer(target["allianceID"])) != allianceId)
                            return false;
                    }
                    else if (value is not null && !Visit(value))
                        return false;
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var value in array)
                    if (value is not null && !Visit(value))
                        return false;
            }
            return true;
        }
    }

    private static JsonNode? ToJson(Eve.Marshal.PyValue value) => value switch
    {
        Eve.Marshal.PyNone => null,
        Eve.Marshal.PyBool boolean => JsonValue.Create(boolean.Value),
        Eve.Marshal.PyInt integer => JsonValue.Create(integer.Value),
        Eve.Marshal.PyLong integer => JsonValue.Create(integer.Value),
        Eve.Marshal.PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        Eve.Marshal.PyStr text => JsonValue.Create(text.Value),
        Eve.Marshal.PyWString text => JsonValue.Create(text.Value),
        Eve.Marshal.PyList list => new JsonArray(list.Items.Select(ToJson).ToArray()),
        Eve.Marshal.PyTuple tuple => new JsonArray(tuple.Items.Select(ToJson).ToArray()),
        Eve.Marshal.PyDict dict => ToJsonObject(dict),
        _ => throw new InvalidOperationException($"Unsupported workforce configuration value {value.GetType().Name}."),
    };

    private static JsonObject ToJsonObject(Eve.Marshal.PyDict value)
    {
        var result = new JsonObject();
        foreach (var entry in value.Entries)
        {
            var key = entry.Key switch
            {
                Eve.Marshal.PyStr text => text.Value,
                Eve.Marshal.PyWString text => text.Value,
                Eve.Marshal.PyInt integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Eve.Marshal.PyLong integer => integer.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                _ => throw new InvalidOperationException("Workforce configuration keys must be scalar."),
            };
            result[key] = ToJson(entry.Value);
        }
        return result;
    }

    private static long Integer(JsonNode? node) =>
        long.TryParse(node?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static long Positive(long value) => value > 0 ? value : 0;

    private static long? NullablePositive(JsonNode? node)
    {
        var value = Integer(node);
        return value > 0 ? value : null;
    }

    private static bool Flag(JsonNode? node) =>
        bool.TryParse(node?.ToString(), out var value) ? value : Integer(node) != 0;
}

public sealed class PersistenceSovFwReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTable,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf)
{
    public async ValueTask<IReadOnlyList<Eve.Host.DevelopmentIndexRecord>> ReadAllDevelopmentIndicesAsync(
        CancellationToken cancellationToken)
    {
        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        if (systems is null)
            return [];

        var records = new List<Eve.Host.DevelopmentIndexRecord>(systems.Count);
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system)
                continue;
            var systemId = Positive(Integer(system["solarSystemID"]));
            if (systemId <= 0 &&
                !long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out systemId))
                continue;
            if (systemId <= 0)
                continue;

            var indices = system["devIndices"] as JsonObject;
            records.Add(new(
                systemId,
                Positive(Integer(indices?["militaryPoints"])),
                Positive(Integer(indices?["industrialPoints"])),
                Positive(Integer(indices?["claimedForDays"] ?? indices?["claimedFor"]))));
        }
        return records.OrderBy(record => record.SolarSystemId).ToArray();
    }

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


    public async ValueTask<IReadOnlyList<long>> ReadAllianceSystemsAsync(
        long allianceId,
        CancellationToken cancellationToken)
    {
        if (allianceId <= 0)
            return Array.Empty<long>();
        var snapshot = await ReadSnapshotAsync(cancellationToken);
        return snapshot.ClaimsBySolarSystemId
            .Where(pair => pair.Value.AllianceId == allianceId)
            .Select(pair => pair.Key)
            .OrderBy(systemId => systemId)
            .ToArray();
    }


    public async ValueTask<PyValue> ReadCurrentMapDataAsync(
        long locationId,
        IReadOnlyList<Eve.Sde.SdeRow> staticSystems,
        CancellationToken cancellationToken)
    {
        var columns = new[]
        {
            "locationID", "solarSystemID", "constellationID", "regionID", "ownerID",
            "allianceID", "corporationID", "claimStructureID", "infrastructureHubID",
            "stationID", "claimTime",
        };
        if (locationId <= 0)
            return Py.Rowset(columns, []);

        static long StaticId(System.Text.Json.JsonElement doc, string field) =>
            doc.TryGetProperty(field, out var value) && value.TryGetInt64(out var number) ? number : 0;

        static PyValue Id(JsonObject? row, string field, long fallback = 0)
        {
            var value = Positive(Integer(row?[field]));
            if (value <= 0)
                value = fallback;
            return value > 0 ? new PyInt(value) : PyNone.Instance;
        }

        static PyValue CurrentRow(JsonObject? row, long systemId, long constellationId, long regionId)
        {
            var allianceId = Positive(Integer(row?["allianceID"]));
            return Py.Tuple(
                Id(row, "locationID", systemId),
                Id(row, "solarSystemID", systemId),
                Id(row, "constellationID", constellationId),
                Id(row, "regionID", regionId),
                Id(row, "ownerID", allianceId),
                Id(row, "allianceID", allianceId),
                Id(row, "corporationID"),
                Id(row, "claimStructureID"),
                Id(row, "infrastructureHubID"),
                Id(row, "stationID"),
                new PyLong(Math.Max(0, Integer(row?["claimTime"]))));
        }

        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        var result = new List<PyValue>();
        foreach (var system in staticSystems.OrderBy(row => row.Key))
        {
            var constellationId = StaticId(system.Doc, "constellationID");
            var regionId = StaticId(system.Doc, "regionID");
            if (system.Key != locationId && constellationId != locationId && regionId != locationId)
                continue;

            var stored = systems?[system.Key.ToString(System.Globalization.CultureInfo.InvariantCulture)] as JsonObject;
            if (stored?["currentData"] is JsonArray { Count: > 0 } currentRows)
            {
                foreach (var current in currentRows.OfType<JsonObject>())
                    result.Add(CurrentRow(current, system.Key, constellationId, regionId));
            }
            else
            {
                result.Add(CurrentRow(stored, system.Key, constellationId, regionId));
            }
        }

        return Py.Rowset(columns, result);
    }

    public async ValueTask<PyValue> ReadRecentSovActivityAsync(CancellationToken cancellationToken)
    {
        var columns = new[] { "solarSystemID", "ownerID", "oldOwnerID", "stationID", "changeTime" };
        var state = await readTable("sovereignty", cancellationToken);
        var systems = state["systems"] as JsonObject;
        if (systems is null)
            return Py.Rowset(columns, []);

        var changes = new List<(long SystemId, long OwnerId, long OldOwnerId, long StationId, long ChangeTime)>();
        foreach (var (key, node) in systems)
        {
            if (node is not JsonObject system || system["recentActivity"] is not JsonArray activityRows)
                continue;
            var storedSystemId = Positive(Integer(system["solarSystemID"]));
            if (storedSystemId <= 0 && long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSystemId))
                storedSystemId = Positive(parsedSystemId);

            foreach (var activity in activityRows.OfType<JsonObject>())
            {
                var systemId = Positive(Integer(activity["solarSystemID"]));
                if (systemId <= 0)
                    systemId = storedSystemId;
                changes.Add((
                    systemId,
                    Positive(Integer(activity["ownerID"])),
                    Positive(Integer(activity["oldOwnerID"])),
                    Positive(Integer(activity["stationID"])),
                    Math.Max(0, Integer(activity["changeTime"]))));
            }
        }

        return Py.Rowset(columns, changes
            .OrderByDescending(change => change.ChangeTime)
            .ThenBy(change => change.SystemId)
            .Select(change => Py.Tuple(
                change.SystemId > 0 ? new PyInt(change.SystemId) : PyNone.Instance,
                change.OwnerId > 0 ? new PyInt(change.OwnerId) : PyNone.Instance,
                change.OldOwnerId > 0 ? new PyInt(change.OldOwnerId) : PyNone.Instance,
                change.StationId > 0 ? new PyInt(change.StationId) : PyNone.Instance,
                new PyLong(change.ChangeTime))));
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

public sealed class PersistenceAgentLocationReadModel(
    Func<string, CancellationToken, Task<IReadOnlyList<SdeRow>>> readTableAsync)
{
    public async Task<IReadOnlyDictionary<long, long>> ReadAsync(CancellationToken cancellationToken)
    {
        var locations = new Dictionary<long, long>();
        foreach (var row in await readTableAsync("npcCharacters", cancellationToken))
        {
            if (row.Key > 0 &&
                row.Doc.TryGetProperty("locationID", out var location) &&
                location.TryGetInt64(out var locationId) &&
                locationId > 0)
            {
                locations[row.Key] = locationId;
            }
        }

        return locations;
    }
}

public sealed class PersistenceSkyhookActivationWriter(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<long, CancellationToken, ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?>> readSkyhookAsync,
    Func<Eve.Services.ServiceCall, Eve.Services.Core.SkyhookResourceSnapshot, Eve.Services.Core.SkyhookResourcePermission, CancellationToken, ValueTask<bool>> authorizeAsync,
    SemaphoreSlim? sharedWriteGate = null)
{
    private readonly SemaphoreSlim _gate = sharedWriteGate ?? new(1, 1);

    public async ValueTask<Eve.Services.Core.SkyhookResourceSnapshot?> PersistAsync(
        Eve.Services.ServiceCall call,
        Eve.Services.Core.SkyhookResourceSnapshot expected,
        bool active,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var permission = active
                ? Eve.Services.Core.SkyhookResourcePermission.Activate
                : Eve.Services.Core.SkyhookResourcePermission.Deactivate;
            if (!await authorizeAsync(call, expected, permission, cancellationToken))
                return null;

            var current = await readSkyhookAsync(expected.SkyhookId, cancellationToken);
            if (current is null ||
                current.CorporationId != expected.CorporationId ||
                current.ProductionActive != expected.ProductionActive ||
                !string.Equals(current.ResourceVersion, expected.ResourceVersion, StringComparison.Ordinal))
                return null;

            var key = expected.SkyhookId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var path = $"/skyhooks/{key}";
            if (readCacheTable("sovereignty")["skyhooks"] is not JsonObject rows ||
                rows[key] is not JsonObject row ||
                !string.Equals(row["resourceVersion"]?.ToString() ?? string.Empty,
                    expected.ResourceVersion, StringComparison.Ordinal))
                return null;

            var updatedRow = (JsonObject)row.DeepClone();
            updatedRow["productionActive"] = active;
            var version = Guid.NewGuid().ToString("N");
            updatedRow["resourceVersion"] = version;
            var result = write("sovereignty", path, updatedRow);
            if (!result.Success)
                throw new InvalidOperationException($"Could not persist Skyhook activation for {expected.SkyhookId}: {result.ErrorMsg}");

            // Finish the one-row commit after the cache mutation has been staged.
            await flushTableAsync("sovereignty", CancellationToken.None);
            var persisted = await readSkyhookAsync(expected.SkyhookId, CancellationToken.None);
            return persisted is { } readback &&
                readback.CorporationId == expected.CorporationId &&
                readback.ProductionActive == active &&
                string.Equals(readback.ResourceVersion, version, StringComparison.Ordinal)
                    ? readback
                    : null;
        }
        finally
        {
            _gate.Release();
        }
    }
}

public sealed class PersistenceCharMgrReadModel(
    Func<string, CancellationToken, Task<JsonObject>> readTableAsync,
    Func<long, int, int, CancellationToken, ValueTask<IReadOnlyList<PyValue>>> readRecentShipKillmailPageAsync,
    Func<long, CancellationToken, ValueTask<string?>> readCharacterDescriptionAsync,
    Func<long, string, CancellationToken, ValueTask> writeCharacterDescriptionAsync,
    Func<long, CancellationToken, ValueTask<long?>> readMedal1GraphicIdAsync,
    Func<string, string, JsonNode?, WriteResult>? writePath = null,
    Func<string, string, WriteResult>? removePath = null)
{
    // ponytail: serialize personal-data read-modify-write, including the shared nextNoteID; split by owner plus a note-ID allocator if throughput matters.
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private const string DefaultFoldersLabel = "S:Folders";
    private const string DefaultFoldersNote = "1::F::0::Main|";

    public ValueTask<IReadOnlyList<PyValue>> ReadRecentShipKillmailPageAsync(
        long characterId, int num, int startIndex, CancellationToken cancellationToken) =>
        readRecentShipKillmailPageAsync(characterId, num, startIndex, cancellationToken);


    public static IReadOnlyList<PyValue> PackKillmailRows(
        IReadOnlyList<JsonObject> rows,
        Func<JsonNode?, PyValue> toPy)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var columns = rows
            .SelectMany(row => row.Select(property => property.Key))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var header = new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(
                new PyToken("blue.DBRowDescriptor"),
                Py.Tuple(Py.Tuple([
                    .. columns.Select(name => (PyValue)Py.Tuple(
                        new PyStr(name),
                        new PyInt(Eve.Marshal.DbType.Empty)))
                ]))),
            [],
            []);
        var descriptor = Eve.Marshal.Descriptor.FromHeader(header);

        return rows.Select(row => (PyValue)new PyPackedRow(
            descriptor,
            [.. columns.Select(name => row.TryGetPropertyValue(name, out var value)
                ? toPy(value)
                : PyNone.Instance)])).ToArray();
    }

    public ValueTask<string?> ReadCharacterDescriptionAsync(
        long characterId, CancellationToken cancellationToken) =>
        readCharacterDescriptionAsync(characterId, cancellationToken);

    public ValueTask WriteCharacterDescriptionAsync(
        long characterId, string description, CancellationToken cancellationToken) =>
        writeCharacterDescriptionAsync(characterId, description, cancellationToken);

    public ValueTask<long?> ReadMedal1GraphicIdAsync(
        long characterId, CancellationToken cancellationToken) =>
        readMedal1GraphicIdAsync(characterId, cancellationToken);

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

    public async ValueTask<CharMgrPersonalData?> UpdateAsync(
        long characterId,
        Func<CharMgrPersonalData, CharMgrPersonalData> update,
        CancellationToken cancellationToken)
    {
        if (characterId <= 0)
        {
            return null;
        }

        await _updateGate.WaitAsync(cancellationToken);
        try
        {
            var before = await ReadAsync(characterId, cancellationToken) ?? CharMgrPersonalData.Empty;
            var after = update(before);
            WriteChanges(characterId, before, after);
            return after;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    public ValueTask WriteActivityStatusAsync(
        long characterId, long status, string? extraInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WritePath("characters", $"/{key}/activityStatus", JsonValue.Create(status));
        WritePath("characters", $"/{key}/activityStatusExtraInfo",
            extraInfo is null ? null : JsonValue.Create(extraInfo));
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteLogSettingsAsync(long characterId, PyValue settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = JsonFromPy(settings);
        if (value is not (JsonObject or JsonArray))
        {
            value = new JsonObject();
        }
        var key = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        WritePath("characters", $"/{key}/lastSettingsTelemetry", value);
        return ValueTask.CompletedTask;
    }

    private void WriteChanges(long characterId, CharMgrPersonalData before, CharMgrPersonalData after)
{
    var owner = characterId.ToString(System.Globalization.CultureInfo.InvariantCulture);
    var fileTime = Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    var oldContacts = before.Contacts.ToDictionary(contact => contact.ContactId);
    var newContacts = after.Contacts.ToDictionary(contact => contact.ContactId);
    foreach (var contactId in oldContacts.Keys.Union(newContacts.Keys))
    {
        if (!newContacts.TryGetValue(contactId, out var contact))
        {
            RemovePath("characters", $"/{owner}/personalContacts/{contactId}");
        }
        else if (!oldContacts.TryGetValue(contactId, out var old) || old != contact)
        {
            WritePath("characters", $"/{owner}/personalContacts/{contactId}", new JsonObject
            {
                ["contactID"] = contact.ContactId,
                ["inWatchlist"] = contact.InWatchlist,
                ["relationshipID"] = contact.RelationshipId,
                ["labelMask"] = contact.LabelMask,
            });
        }
    }

    var oldBlocked = before.BlockedSenders.ToHashSet();
    var newBlocked = after.BlockedSenders.ToHashSet();
    foreach (var ownerId in oldBlocked.Union(newBlocked))
    {
        if (!newBlocked.Contains(ownerId))
        {
            RemovePath("characters", $"/{owner}/blockedOwners/{ownerId}");
        }
        else if (!oldBlocked.Contains(ownerId))
        {
            WritePath("characters", $"/{owner}/blockedOwners/{ownerId}", new JsonObject
            {
                ["senderID"] = ownerId,
            });
        }
    }

    var oldOwnerNotes = before.OwnerNotes.ToDictionary(note => note.NoteId);
    var newOwnerNotes = after.OwnerNotes.ToDictionary(note => note.NoteId);
    foreach (var noteId in oldOwnerNotes.Keys.Union(newOwnerNotes.Keys))
    {
        var path = $"/characters/{owner}/notes/{noteId}";
        if (!newOwnerNotes.TryGetValue(noteId, out var note))
        {
            RemovePath("characterNotes", path);
        }
        else if (!oldOwnerNotes.TryGetValue(noteId, out var old))
        {
            WritePath("characterNotes", path, new JsonObject
            {
                ["noteID"] = note.NoteId,
                ["label"] = note.Label,
                ["note"] = note.Note,
                ["created"] = fileTime,
                ["updated"] = fileTime,
            });
        }
        else if (old != note)
        {
            if (old.Label != note.Label)
            {
                WritePath("characterNotes", $"{path}/label", JsonValue.Create(note.Label));
            }
            if (old.Note != note.Note)
            {
                WritePath("characterNotes", $"{path}/note", JsonValue.Create(note.Note));
            }
            WritePath("characterNotes", $"{path}/updated", JsonValue.Create(fileTime));
        }
    }

    var oldItemNotes = before.ItemNotes;
    var newItemNotes = after.ItemNotes;
    foreach (var itemId in oldItemNotes.Keys.Union(newItemNotes.Keys))
    {
        var path = $"/characters/{owner}/itemNotes/{itemId}";
        if (!newItemNotes.TryGetValue(itemId, out var note))
        {
            RemovePath("characterNotes", path);
        }
        else if (!oldItemNotes.TryGetValue(itemId, out var old))
        {
            WritePath("characterNotes", path, new JsonObject
            {
                ["itemID"] = itemId,
                ["note"] = note,
                ["updated"] = fileTime,
            });
        }
        else if (old != note)
        {
            WritePath("characterNotes", $"{path}/note", JsonValue.Create(note));
            WritePath("characterNotes", $"{path}/updated", JsonValue.Create(fileTime));
        }
    }

    if (before.NextOwnerNoteId != after.NextOwnerNoteId)
    {
        if (after.NextOwnerNoteId is > 0)
        {
            WritePath("characterNotes", "/_meta/nextNoteID", JsonValue.Create(after.NextOwnerNoteId.Value));
        }
        else
        {
            RemovePath("characterNotes", "/_meta/nextNoteID");
        }
    }
}

    private void WritePath(string table, string path, JsonNode? data)
    {
        var result = writePath?.Invoke(table, path, data)
            ?? throw new InvalidOperationException("CharMgr GameStore writer is not configured.");
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not write CharMgr data at {table}{path}: {result.ErrorMsg}");
        }
    }

    private void RemovePath(string table, string path)
    {
        var result = removePath?.Invoke(table, path)
            ?? throw new InvalidOperationException("CharMgr GameStore remover is not configured.");
        if (!result.Success && result.ErrorMsg != "ENTRY_NOT_FOUND")
        {
            throw new InvalidOperationException($"Could not remove CharMgr data at {table}{path}: {result.ErrorMsg}");
        }
    }

    private static JsonNode? JsonFromPy(PyValue value) => value switch
    {
        PyNone => null,
        PyBool boolean => JsonValue.Create(boolean.Value),
        PyInt number => JsonValue.Create(number.Value),
        PyLong number => JsonValue.Create(number.Value),
        PyReal number when double.IsFinite(number.Value) => JsonValue.Create(number.Value),
        PyReal => null,
        PyStr text => JsonValue.Create(text.Value),
        PyWString text => JsonValue.Create(text.Value),
        PyToken text => JsonValue.Create(text.Value),
        PyBytes bytes => JsonValue.Create(System.Text.Encoding.UTF8.GetString(bytes.Value.Span)),
        PyBuffer buffer => JsonValue.Create(System.Text.Encoding.UTF8.GetString(buffer.Value.Span)),
        PyList list => new JsonArray(list.Items.Select(JsonFromPy).ToArray()),
        PyTuple tuple => new JsonArray(tuple.Items.Select(JsonFromPy).ToArray()),
        PyDict dict => JsonFromPy(dict.Entries),
        PyObject obj => JsonFromPy(obj.Args),
        PyObjectEx objEx when objEx.Dict.Count > 0 => JsonFromPy(objEx.Dict),
        PyObjectEx objEx when objEx.List.Count > 0 => new JsonArray(objEx.List.Select(JsonFromPy).ToArray()),
        PyObjectEx => new JsonObject(),
        _ => null,
    };

    private static JsonObject JsonFromPy(IReadOnlyList<KeyValuePair<PyValue, PyValue>> entries)
    {
        var result = new JsonObject();
        foreach (var (key, value) in entries)
        {
            var name = key switch
            {
                PyStr text => text.Value,
                PyWString text => text.Value,
                PyToken text => text.Value,
                PyInt number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PyLong number => number.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PyBool boolean => boolean.Value ? "true" : "false",
                PyNone => "null",
                _ => Eve.Macho.JsSemantics.StringValue(key),
            };
            if (name is not null)
            {
                result[name] = JsonFromPy(value);
            }
        }
        return result;
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

public sealed class PersistenceCrimewatchCallbacks(
    Eve.Relations.RelationStore relations,
    Eve.GameStore.PersistenceStore store,
    Eve.GameStore.GameStoreCache cache,
    Eve.Services.CharacterSessions sessions,
    Func<long, CancellationToken, Task<Eve.Relations.Affiliation>> affiliationOf,
    Func<long> nowUnixMs)
    : Eve.Services.Crimewatch.ICrimewatchPersistenceCallbacks
{
    private const string RuntimeTable = "crimewatchRuntime";
    private const long DuelChallengeLifetimeMs = 120_000;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<Eve.Services.Crimewatch.SecurityStatusTransaction>> SecurityStatusTransactionsOfAsync(
        long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return [];

        var rows = await relations.HistoryAsync(
            Eve.Relations.RelationKinds.SecurityStatus.Name,
            characterId,
            Eve.Relations.RelationStore.World,
            int.MaxValue,
            cancellationToken);
        return rows.Select(row =>
        {
            var attrs = row.Attrs;
            var eventType = Int(attrs, "eventTypeID") ??
                Eve.Crimewatch.SecurityStatusEventTypeIds.FromHistoryReason(row.Reason);
            return new Eve.Services.Crimewatch.SecurityStatusTransaction(
                eventType,
                row.AtMs,
                Long(attrs, "otherOwnerID"),
                Int(attrs, "otherTypeID"),
                Long(attrs, "locationID"),
                Double(attrs, "modification"),
                row.NewValue);
        }).ToArray();
    }

    public async Task<long?> StartDuelChallengeAsync(
        long challengerCharacterId,
        long targetCharacterId,
        long challengerCorporationId,
        long challengerAllianceId,
        CancellationToken cancellationToken)
    {
        if (challengerCharacterId <= 0 || targetCharacterId <= 0 ||
            challengerCharacterId == targetCharacterId ||
            challengerCorporationId <= 0 || challengerAllianceId < 0)
            return null;

        var challenger = sessions.Of(challengerCharacterId);
        var target = sessions.Of(targetCharacterId);
        if (challenger?.CharacterId != challengerCharacterId || target?.CharacterId != targetCharacterId)
            return null;

        var affiliation = await affiliationOf(challengerCharacterId, cancellationToken);
        if ((affiliation.Corporation ?? 0) != challengerCorporationId ||
            (affiliation.Alliance ?? 0) != challengerAllianceId ||
            (challenger.GetInt("corpid") ?? 0) != challengerCorporationId ||
            (challenger.GetInt("allianceid") ?? 0) != challengerAllianceId)
            return null;

        var issuedAt = nowUnixMs();
        var expiresAtUnixMs = checked(issuedAt + DuelChallengeLifetimeMs);
        var expiresAtFileTime = Py.FileTimeFromUnixMs(expiresAtUnixMs);
        var key = ChallengeKey(challengerCharacterId, targetCharacterId);
        var created = await MutateRootAsync(root =>
        {
            if (HasActiveEngagement(root, challengerCharacterId, targetCharacterId, issuedAt))
                return (false, false);

            root[key] = new JsonObject
            {
                ["kind"] = "duelChallenge",
                ["challengerCharacterID"] = challengerCharacterId,
                ["targetCharacterID"] = targetCharacterId,
                ["challengerCorporationID"] = challengerCorporationId,
                ["challengerAllianceID"] = challengerAllianceId,
                ["expiresAtFileTime"] = expiresAtFileTime,
                ["expiresAtMs"] = expiresAtUnixMs,
            };
            return (true, true);
        }, cancellationToken);

        if (!created)
            return null;

        target.SendNotification(
            "OnDuelChallenge",
            "charid",
            new PyLong(challengerCharacterId),
            new PyLong(challengerCorporationId),
            new PyLong(challengerAllianceId),
            new PyLong(expiresAtFileTime));
        return expiresAtFileTime;
    }

    public async Task<IReadOnlyList<Eve.Services.Crimewatch.LimitedEngagement>> LimitedEngagementsAsync(
        long nowUnixMs, long? characterId, CancellationToken cancellationToken)
    {
        var root = await store.LoadTableAsync(RuntimeTable, createIfMissing: false, cancellationToken);
        var result = new List<Eve.Services.Crimewatch.LimitedEngagement>();
        foreach (var (_, value) in root)
        {
            if (value is not JsonObject row || Text(row["kind"]) != "limitedEngagement")
                continue;

            var first = Number(row["firstCharacterID"]);
            var second = Number(row["secondCharacterID"]);
            var expiresAt = Number((row["engagement"] as JsonObject)?["expiresAtMs"]);
            if (expiresAt <= nowUnixMs ||
                characterId is { } character && first != character && second != character)
                continue;

            result.Add(new Eve.Services.Crimewatch.LimitedEngagement(first, second, expiresAt));
        }
        return result.OrderBy(row => row.FirstCharacterID).ThenBy(row => row.SecondCharacterID).ToArray();
    }

    public Task UpsertLimitedEngagementAsync(
        long firstCharacterId, long secondCharacterId, long expiresAtUnixMs,
        CancellationToken cancellationToken)
    {
        if (firstCharacterId <= 0 || secondCharacterId <= 0 ||
            firstCharacterId == secondCharacterId || expiresAtUnixMs <= nowUnixMs())
            throw new ArgumentOutOfRangeException(nameof(expiresAtUnixMs));

        var first = Math.Min(firstCharacterId, secondCharacterId);
        var second = Math.Max(firstCharacterId, secondCharacterId);
        return MutateRootAsync(root =>
        {
            root[EngagementKey(first, second)] = EngagementRow(first, second, expiresAtUnixMs);
            return (true, true);
        }, cancellationToken);
    }

    public Task<bool> ExpireLimitedEngagementAsync(
        long firstCharacterId,
        long secondCharacterId,
        long expectedExpiresAtUnixMs,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        var first = Math.Min(firstCharacterId, secondCharacterId);
        var second = Math.Max(firstCharacterId, secondCharacterId);
        return MutateRootAsync(root =>
        {
            var key = EngagementKey(first, second);
            if (root[key] is not JsonObject row ||
                Text(row["kind"]) != "limitedEngagement" ||
                Number((row["engagement"] as JsonObject)?["expiresAtMs"]) != expectedExpiresAtUnixMs ||
                expectedExpiresAtUnixMs > nowUnixMs)
                return (false, false);

            root.Remove(key);
            return (true, true);
        }, cancellationToken);
    }

    public Task<bool> RespondToDuelChallengeAsync(
        long responderCharacterId,
        long challengerCharacterId,
        long challengeExpiresAtFileTime,
        bool accept,
        long? engagementExpiresAtUnixMs,
        CancellationToken cancellationToken)
    {
        if (responderCharacterId <= 0 || challengerCharacterId <= 0 ||
            responderCharacterId == challengerCharacterId || challengeExpiresAtFileTime <= 0)
            return Task.FromResult(false);

        var now = nowUnixMs();
        var nowFileTime = Py.FileTimeFromUnixMs(now);
        if (accept && (engagementExpiresAtUnixMs is not { } due || due <= now))
            return Task.FromResult(false);

        return MutateRootAsync(root =>
        {
            var key = ChallengeKey(challengerCharacterId, responderCharacterId);
            if (root[key] is not JsonObject challenge ||
                Text(challenge["kind"]) != "duelChallenge" ||
                Number(challenge["challengerCharacterID"]) != challengerCharacterId ||
                Number(challenge["targetCharacterID"]) != responderCharacterId ||
                Number(challenge["expiresAtFileTime"]) != challengeExpiresAtFileTime)
                return (false, false);

            root.Remove(key);
            if (challengeExpiresAtFileTime <= nowFileTime)
                return (true, false);
            if (!accept)
                return (true, false);

            var expiresAt = engagementExpiresAtUnixMs!.Value;
            var first = Math.Min(responderCharacterId, challengerCharacterId);
            var second = Math.Max(responderCharacterId, challengerCharacterId);
            root[EngagementKey(first, second)] = EngagementRow(first, second, expiresAt);
            return (true, true);
        }, cancellationToken);
    }

    private async Task<T> MutateRootAsync<T>(
        Func<JsonObject, (bool Changed, T Result)> mutation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var persisted = await store.LoadTableAsync(RuntimeTable, createIfMissing: true, cancellationToken);
            var next = (JsonObject)persisted.DeepClone();
            var (changed, result) = mutation(next);
            if (!changed)
                return result;

            var write = cache.Write(RuntimeTable, "/", next);
            if (!write.Success)
                throw new InvalidOperationException(
                    $"Could not update {RuntimeTable}: {write.ErrorMsg ?? "GameStore rejected the write."}");

            await FlushUntilVisibleAsync(next, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushUntilVisibleAsync(JsonObject expected, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 500; attempt++)
        {
            await cache.FlushTableAsync(RuntimeTable, cancellationToken);
            var actual = await store.LoadTableAsync(RuntimeTable, createIfMissing: false, cancellationToken);
            if (JsonNode.DeepEquals(actual, expected))
                return;
            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException($"The {RuntimeTable} GameStore write did not become durable.");
    }

    private bool HasActiveEngagement(JsonObject root, long firstCharacterId, long secondCharacterId, long now)
    {
        var key = EngagementKey(
            Math.Min(firstCharacterId, secondCharacterId),
            Math.Max(firstCharacterId, secondCharacterId));
        return root[key] is JsonObject row &&
            Text(row["kind"]) == "limitedEngagement" &&
            Number((row["engagement"] as JsonObject)?["expiresAtMs"]) > now;
    }

    private static JsonObject EngagementRow(long first, long second, long expiresAtUnixMs) => new()
    {
        ["kind"] = "limitedEngagement",
        ["firstCharacterID"] = first,
        ["secondCharacterID"] = second,
        ["expiresAtMs"] = expiresAtUnixMs,
        ["engagement"] = new JsonObject { ["expiresAtMs"] = expiresAtUnixMs },
    };

    private static string EngagementKey(long first, long second) => $"le:{first}:{second}";
    private static string ChallengeKey(long challenger, long target) => $"duel:{challenger}:{target}";

    private static long Number(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    private static string Text(JsonNode? value) =>
        value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : string.Empty;

    private static System.Text.Json.JsonElement? Attribute(System.Text.Json.JsonElement? attrs, string name)
    {
        if (attrs is { } document &&
            document.ValueKind == System.Text.Json.JsonValueKind.Object &&
            document.TryGetProperty(name, out var value))
            return value;
        return null;
    }

    private static int? Int(System.Text.Json.JsonElement? attrs, string name) =>
        Attribute(attrs, name) is { } value && value.TryGetInt32(out var number) ? number : null;

    private static long? Long(System.Text.Json.JsonElement? attrs, string name) =>
        Attribute(attrs, name) is { } value && value.TryGetInt64(out var number) ? number : null;

    private static double? Double(System.Text.Json.JsonElement? attrs, string name) =>
        Attribute(attrs, name) is { } value && value.TryGetDouble(out var number) ? number : null;
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
        Eve.Services.Ships.ISpaceDroneBays? droneBays,
        Eve.Corporations.CorporationStore corporations,
        IReadOnlyList<ICharacterEntryHook>? entryHooks = null) =>
        new(
            items,
            types,
            stations,
            ships,
            statics,
            nowUnixMs,
            space,
            droneBays,
            corporationHangarAccess: async (characterId, corporationId, stationId, divisionFlag, operation, token) =>
            {
                if (await corporations.GetAsync(corporationId, token) is not { IsNpc: false } corporation
                    || await corporations.MemberAsync(characterId, token) is not { } member
                    || member.CorporationId != corporationId)
                    return false;

                if (operation == Eve.Services.Inventory.CorporationHangarOperation.Deposit)
                    return true;

                var roles = Eve.Corporations.CorporationRoles.Session(
                    corporation,
                    member,
                    stationId,
                    await corporations.TitlesAsync(corporationId, token));
                return Eve.Corporations.CorporationRoles.HasHangarAccess(
                    roles,
                    stationId,
                    divisionFlag,
                    take: operation == Eve.Services.Inventory.CorporationHangarOperation.Take);
            },
            entryHooks: entryHooks);
}


public sealed class PersistenceCalendarAudienceReader(
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> characterIdsForCorporation,
    Func<long, CancellationToken, Task<IReadOnlyList<long>>> corporationIdsForAlliance)
    : Eve.Calendar.ICalendarAudienceReader
{
    public async Task<IReadOnlyList<long>> GetMemberCharacterIdsAsync(
        short eventTag,
        long ownerId,
        CancellationToken cancellationToken = default)
    {
        if (ownerId <= 0)
            return Array.Empty<long>();
        if (eventTag == Eve.Calendar.CalendarTags.Corporation)
        {
            var members = await characterIdsForCorporation(ownerId, cancellationToken);
            return members.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
        }
        if (eventTag != Eve.Calendar.CalendarTags.Alliance)
            return Array.Empty<long>();

        var corporationIds = await corporationIdsForAlliance(ownerId, cancellationToken);
        var characters = new HashSet<long>();
        foreach (var corporationId in corporationIds.Where(id => id > 0).Distinct())
            foreach (var characterId in await characterIdsForCorporation(corporationId, cancellationToken))
                if (characterId > 0)
                    characters.Add(characterId);
        return characters.OrderBy(id => id).ToArray();
    }
}




public sealed class PersistenceIndustryJobOperations : Eve.Services.Core.IIndustryJobOperations
{
    public sealed record IndustryFacility(
        long FacilityId, long SolarSystemId, long OwnerId, decimal TaxRate, double TimeMultiplier, bool IsOnline);

    private sealed record Material(int TypeId, long Quantity);
    private sealed record Product(int TypeId, long Quantity, double Probability);
    private sealed record SkillRequirement(int TypeId, int Level);
    private sealed record ActivityRecipe(
        int TimeSeconds,
        IReadOnlyList<Material> Materials,
        IReadOnlyList<Product> Products,
        IReadOnlyList<SkillRequirement> Skills);
    private sealed record PreparedInstall(
        ActivityRecipe Recipe,
        IReadOnlyDictionary<int, long> Materials,
        IReadOnlyList<Product> Products,
        IndustryFacility Facility,
        long CostIsk,
        int TimeInSeconds,
        JsonObject Snapshot,
        string Fingerprint);
    private sealed record WalletNotice(long OwnerId, int AccountKey, decimal Balance);

    private const int IndustryJobTaxReferenceType = 42;
    private const int ManufacturingActivity = 1;
    private const int MaterialResearchActivity = 3;
    private const int TimeResearchActivity = 4;
    private const int CopyingActivity = 5;
    private const int InventionActivity = 8;
    private const long FactoryManagerRole = 1024;
    private const long DirectorRole = 1;

    private readonly Npgsql.NpgsqlDataSource _source;
    private readonly string _schema;
    private readonly Eve.Inventory.PgItemStore _items;
    private readonly Eve.Wallet.WalletTransactions _wallets;
    private readonly Eve.Skills.PgSkillStore _skills;
    private readonly Eve.Sde.SdeReader _sde;
    private readonly Eve.Corporations.CorporationStore _corporations;
    private readonly Eve.Events.EventBus _events;
    private readonly Eve.Universe.StationCatalog _stations;
    private readonly Func<long, long, long, long, CancellationToken, ValueTask<IndustryFacility?>> _resolveFacility;
    private readonly Func<long> _nowUnixMs;

    public PersistenceIndustryJobOperations(
        Npgsql.NpgsqlDataSource source,
        string schema,
        Eve.Inventory.PgItemStore items,
        Eve.Wallet.WalletTransactions wallets,
        Eve.Skills.PgSkillStore skills,
        Eve.Sde.SdeReader sde,
        Eve.Corporations.CorporationStore corporations,
        Eve.Events.EventBus events,
        Eve.Universe.StationCatalog stations,
        Func<long, long, long, long, CancellationToken, ValueTask<IndustryFacility?>> resolveFacility,
        Func<long> nowUnixMs)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(schema, "^[a-z_][a-z0-9_]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new ArgumentException("Not a valid industry schema.", nameof(schema));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _schema = schema;
        _items = items ?? throw new ArgumentNullException(nameof(items));
        _wallets = wallets ?? throw new ArgumentNullException(nameof(wallets));
        _skills = skills ?? throw new ArgumentNullException(nameof(skills));
        _sde = sde ?? throw new ArgumentNullException(nameof(sde));
        _corporations = corporations ?? throw new ArgumentNullException(nameof(corporations));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _stations = stations ?? throw new ArgumentNullException(nameof(stations));
        _resolveFacility = resolveFacility ?? throw new ArgumentNullException(nameof(resolveFacility));
        _nowUnixMs = nowUnixMs ?? throw new ArgumentNullException(nameof(nowUnixMs));
    }

    public async ValueTask<System.Text.Json.Nodes.JsonObject?> ReadJobsAsync(CancellationToken cancellationToken)
    {
        var jobs = new System.Text.Json.Nodes.JsonObject();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT job_id, activity_id, blueprint_item_id, blueprint_type_id, blueprint_copy,
                   blueprint_location_id, blueprint_location_flag_id, facility_id, owner_id, status,
                   installer_id, completed_character_id, solar_system_id, station_id,
                   start_date_filetime, end_date_filetime, pause_date_filetime, runs, licensed_runs,
                   successful_runs, cost_isk, time_in_seconds, probability, product_type_id,
                   optional_type_id, optional_type_id2, output_location_id, output_flag_id,
                   payment_owner_id, payment_account_key, activity_snapshot, output_item_ids
              FROM {_schema}.jobs
             ORDER BY job_id
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            jobs[Key(reader.GetInt64(0))] = ReadWireJob(reader);
        return new System.Text.Json.Nodes.JsonObject { ["jobs"] = jobs };
    }

    public async ValueTask<long> InstallJobAsync(
        Eve.Services.Core.IndustryJobInstallRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Context.OperationId))
            throw InvalidJob();
        var fingerprint = Fingerprint(request.Job);
        try
        {
            return await InstallOnceAsync(request, fingerprint, cancellationToken);
        }
        catch (Npgsql.PostgresException error) when (error.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation)
        {
            var replay = await FindInstallReplayAsync(request.Context.OperationId, cancellationToken);
            if (replay is null)
                throw;
            if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint))
                throw InvalidJob();
            return replay.Value.JobId;
        }
    }

    private async ValueTask<long> InstallOnceAsync(
        Eve.Services.Core.IndustryJobInstallRequest request,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var job = request.Job;
        await ValidateActorAsync(request.Context.Actor, job.OwnerId, job.Wallet, job.StationId, cancellationToken);
        var facility = await _resolveFacility(
            job.FacilityId, job.StationId, job.SolarSystemId, request.Context.Actor.CharacterId, cancellationToken);
        if (facility is null || !facility.IsOnline || facility.FacilityId != job.FacilityId ||
            facility.SolarSystemId != job.SolarSystemId || facility.OwnerId <= 0 ||
            !double.IsFinite(facility.TimeMultiplier) || facility.TimeMultiplier <= 0)
            throw InvalidJob();

        var recipe = await ReadRecipeAsync(job.BlueprintTypeId, job.ActivityId, cancellationToken);
        var products = SelectProducts(job, recipe);

        var priceTypes = products.Count > 0
            ? products.Select(product => product.TypeId).Distinct().ToArray()
            : [job.BlueprintTypeId];
        var prices = await ReadMarketPricesAsync(priceTypes, cancellationToken);
        var costIndex = await ReadCostIndexAsync(job.SolarSystemId, job.ActivityId, cancellationToken);

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var replay = await ReadInstallReplayAsync(connection, transaction, request.Context.OperationId, cancellationToken);
        if (replay is not null)
        {
            if (!StringComparer.Ordinal.Equals(replay.Value.Fingerprint, fingerprint))
                throw InvalidJob();
            await transaction.CommitAsync(cancellationToken);
            return replay.Value.JobId;
        }

        var blueprint = await _items.LockAndGetAsync(connection, transaction, job.BlueprintId, cancellationToken);
        if (blueprint is null || blueprint.OwnerId != job.OwnerId || blueprint.TypeId != job.BlueprintTypeId ||
            blueprint.LocationId != job.BlueprintLocationId || blueprint.Flag != job.BlueprintLocationFlagId ||
            !blueprint.Singleton || blueprint.BlueprintCopy != job.BlueprintCopy || blueprint.BlueprintLocked)
            throw InvalidJob();
        if (blueprint.BlueprintCopy &&
            (blueprint.BlueprintRuns is not { } remainingRuns || remainingRuns < job.Runs))
            throw InvalidJob();
        if (job.ActivityId == CopyingActivity && job.LicensedRuns <= 0)
            throw InvalidJob();
        var requiredMaterials = RequiredMaterials(job, blueprint, recipe);

        foreach (var requirement in recipe.Skills)
        {
            var level = await _skills.LevelAsync(
                connection, transaction, request.Context.Actor.CharacterId, requirement.TypeId, cancellationToken);
            if (level < requirement.Level)
                throw InvalidJob();
        }

        var duration = await TimeInSecondsAsync(
            connection, transaction, request.Context.Actor.CharacterId, job, recipe, facility, cancellationToken);
        var cost = CalculateCost(job, products, prices, costIndex, facility.TaxRate);
        await ConsumeMaterialsAsync(
            connection, transaction, job, blueprint.Id, requiredMaterials, cancellationToken);
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, job.OwnerId, blueprint.Id, blueprint.TypeId,
            blueprint.LocationId, blueprint.Flag, blueprint.BlueprintCopy, true, cancellationToken);

        var now = _nowUnixMs();
        var startFileTime = FileTime(now);
        var endFileTime = FileTime(checked(now + (long)duration * 1000));
        var snapshot = ActivitySnapshot(job, blueprint, recipe, requiredMaterials, products, cost, duration, facility);
        var snapshotJson = snapshot.ToJsonString();
        Eve.Wallet.WalletSettlement? settlement = null;
        if (cost > 0)
        {
            settlement = await _wallets.AdjustAsync(
                connection, transaction,
                new Eve.Wallet.WalletAccount(job.Wallet.OwnerId, job.Wallet.AccountKey),
                -cost, facility.OwnerId, IndustryJobTaxReferenceType,
                "Industry job installation", now, request.Context.OperationId,
                "industry-job-install", cancellationToken,
                blueprint.Id, blueprint.TypeId);
        }

        var insert = new Npgsql.NpgsqlCommand($"""
            INSERT INTO {_schema}.jobs (
                install_operation_id, install_request_fingerprint, activity_id, blueprint_item_id,
                blueprint_type_id, blueprint_copy, blueprint_location_id, blueprint_location_flag_id,
                facility_id, owner_id, installer_id, completed_character_id, solar_system_id, station_id,
                input_location_id, input_flag_id, output_location_id, output_flag_id, status,
                start_date_filetime, end_date_filetime, runs, licensed_runs, successful_runs, cost_isk,
                time_in_seconds, probability, product_type_id, optional_type_id, optional_type_id2,
                payment_owner_id, payment_account_key, activity_snapshot
            )
            VALUES (
                $1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,0,$12,$13,$14,$15,$16,$17,1,
                $18,$19,$20,$21,0,$22,$23,$24,$25,$26,$27,$28,$29::jsonb
            )
            RETURNING job_id
            """, connection, transaction);
        Add(insert,
            request.Context.OperationId, fingerprint, job.ActivityId, blueprint.Id, blueprint.TypeId,
            blueprint.BlueprintCopy, blueprint.LocationId, blueprint.Flag, job.FacilityId, job.OwnerId,
            request.Context.Actor.CharacterId, job.SolarSystemId, job.StationId,
            job.InputLocation.LocationId, job.InputLocation.FlagId,
            job.OutputLocation.LocationId, job.OutputLocation.FlagId,
            startFileTime, endFileTime, job.Runs, job.LicensedRuns, cost, duration,
            products.Count == 0 ? 1d : products.Max(product => product.Probability),
            (object?)job.ProductTypeId ?? DBNull.Value,
            (object?)job.OptionalTypeId ?? DBNull.Value,
            (object?)job.OptionalTypeId2 ?? DBNull.Value,
            job.Wallet.OwnerId, job.Wallet.AccountKey, snapshotJson);
        var jobId = (long)(await insert.ExecuteScalarAsync(cancellationToken))!;
        await PublishChangedAsync(connection, transaction, jobId, job.OwnerId, blueprint.Id,
            request.Context.Actor.CharacterId, 1, 0, request.Context.OperationId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Announce(settlement);
        return jobId;
    }

    public async ValueTask CompleteJobAsync(
        Eve.Services.Core.IndustryJobAction request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CompleteInTransactionAsync(connection, transaction, request, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CompleteManyJobsAsync(
        Eve.Services.Core.IndustryJobBatch request,
        CancellationToken cancellationToken)
    {
        if (request.Jobs.Count == 0)
            return;
        if (request.Jobs.Select(job => job.JobId).Distinct().Count() != request.Jobs.Count)
            throw InvalidJob();
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var job in request.Jobs)
            await CompleteInTransactionAsync(connection, transaction, job, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask CancelJobAsync(
        Eve.Services.Core.IndustryJobAction request,
        CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await ReadJobAsync(connection, transaction, request.JobId, cancellationToken);
        if (row is null)
            throw InvalidJob();
        var status = Int(row["status"]);
        if (status == 101)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        if (status >= 100 || Long(row["solarSystemID"]) != request.SolarSystemId)
            throw InvalidJob();
        var ownerId = Long(row["ownerID"]);
        var installerId = Long(row["installerID"]);
        if (!await CanManageExistingJobAsync(request.Context.Actor, ownerId, installerId,
                Int(row["stationID"]), cancellationToken))
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");

        var blueprintId = Long(row["blueprintID"]);
        var blueprint = await _items.LockAndGetAsync(connection, transaction, blueprintId, cancellationToken);
        if (blueprint is null || !blueprint.BlueprintLocked)
            throw InvalidJob();
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, blueprint.LocationId,
            blueprint.Flag, blueprint.BlueprintCopy, false, cancellationToken);
        await using var update = new Npgsql.NpgsqlCommand($"""
            UPDATE {_schema}.jobs
               SET status = 101, completed_character_id = $2, completed_at = clock_timestamp()
             WHERE job_id = $1 AND status < 100
            """, connection, transaction);
        Add(update, request.JobId, request.Context.Actor.CharacterId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw InvalidJob();
        await PublishChangedAsync(connection, transaction, request.JobId, ownerId, blueprintId,
            request.Context.Actor.CharacterId, 101, Long(row["successfulRuns"]),
            request.Context.OperationId + ":" + request.JobId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteInTransactionAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Eve.Services.Core.IndustryJobAction request,
        CancellationToken cancellationToken)
    {
        var row = await ReadJobAsync(connection, transaction, request.JobId, cancellationToken);
        if (row is null)
            throw InvalidJob();
        var status = Int(row["status"]);
        if (status == 100)
            return;
        if (status >= 100 || Long(row["solarSystemID"]) != request.SolarSystemId ||
            Long(row["endDate"]) > FileTime(_nowUnixMs()))
            throw InvalidJob();

        var ownerId = Long(row["ownerID"]);
        var installerId = Long(row["installerID"]);
        var activityId = Int(row["activityID"]);
        if (!await CanManageExistingJobAsync(request.Context.Actor, ownerId, installerId,
                Int(row["stationID"]), cancellationToken))
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        var facility = await _resolveFacility(
            Long(row["facilityID"]), Long(row["stationID"]), request.SolarSystemId,
            request.Context.Actor.CharacterId, cancellationToken);
        if (facility is null || !facility.IsOnline || facility.SolarSystemId != request.SolarSystemId)
            throw InvalidJob();

        var blueprintId = Long(row["blueprintID"]);
        var blueprint = await _items.LockAndGetAsync(connection, transaction, blueprintId, cancellationToken);
        if (blueprint is null || !blueprint.BlueprintLocked)
            throw InvalidJob();
        await _items.SetJobBlueprintLockedAsync(
            connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, blueprint.LocationId,
            blueprint.Flag, blueprint.BlueprintCopy, false, cancellationToken);

        var runs = Int(row["runs"]);
        var licensedRuns = Int(row["licensedRuns"]);
        var productRows = (row["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["products"] as System.Text.Json.Nodes.JsonArray ?? [];
        var outputIds = new List<long>();
        long successfulRuns = runs;
        var nextMaterialEfficiency = blueprint.MaterialEfficiency;
        var nextTimeEfficiency = blueprint.TimeEfficiency;

        if (activityId == MaterialResearchActivity)
            nextMaterialEfficiency = Math.Min(10, checked(nextMaterialEfficiency + runs));
        else if (activityId == TimeResearchActivity)
            nextTimeEfficiency = Math.Min(20, checked(nextTimeEfficiency + runs));
        else
        {
            if (blueprint.BlueprintCopy && activityId != CopyingActivity)
            {
                var remaining = (blueprint.BlueprintRuns ?? 0) - runs;
                if (remaining <= 0)
                    await _items.DeleteAsync(connection, transaction, blueprint.Id, cancellationToken);
                else
                    await _items.SetIndustryBlueprintStateAsync(
                        connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, true,
                        remaining, blueprint.MaterialEfficiency, blueprint.TimeEfficiency, cancellationToken);
            }

            successfulRuns = await CreateOutputsAsync(
                connection, transaction, row, productRows, outputIds, cancellationToken);
        }

        if (blueprint.BlueprintCopy && activityId is MaterialResearchActivity or TimeResearchActivity)
        {
            var remaining = (blueprint.BlueprintRuns ?? 0) - runs;
            if (remaining <= 0)
                await _items.DeleteAsync(connection, transaction, blueprint.Id, cancellationToken);
            else
                await _items.SetIndustryBlueprintStateAsync(
                    connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, true,
                    remaining, nextMaterialEfficiency, nextTimeEfficiency, cancellationToken);
        }
        else if (!blueprint.BlueprintCopy && activityId is MaterialResearchActivity or TimeResearchActivity)
        {
            await _items.SetIndustryBlueprintStateAsync(
                connection, transaction, ownerId, blueprint.Id, blueprint.TypeId, false,
                blueprint.BlueprintRuns, nextMaterialEfficiency, nextTimeEfficiency, cancellationToken);
        }

        var outputArray = new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Bigint,
            Value = outputIds.ToArray(),
        };
        await using var update = new Npgsql.NpgsqlCommand($"""
            UPDATE {_schema}.jobs
               SET status = 100, completed_character_id = $2, successful_runs = $3,
                   output_item_ids = $4, result_snapshot = $5::jsonb, completed_at = clock_timestamp()
             WHERE job_id = $1 AND status < 100
            """, connection, transaction);
        update.Parameters.Add(new Npgsql.NpgsqlParameter { Value = request.JobId });
        update.Parameters.Add(new Npgsql.NpgsqlParameter { Value = request.Context.Actor.CharacterId });
        update.Parameters.Add(new Npgsql.NpgsqlParameter { Value = successfulRuns });
        update.Parameters.Add(outputArray);
        update.Parameters.Add(new Npgsql.NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
            Value = new System.Text.Json.Nodes.JsonObject
            {
                ["blueprintMaterialEfficiency"] = nextMaterialEfficiency,
                ["blueprintTimeEfficiency"] = nextTimeEfficiency,
                ["outputItemIDs"] = new System.Text.Json.Nodes.JsonArray(outputIds.Select(id => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(id)).ToArray()),
            }.ToJsonString(),
        });
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw InvalidJob();

        await PublishChangedAsync(connection, transaction, request.JobId, ownerId, blueprintId,
            request.Context.Actor.CharacterId, 100, successfulRuns,
            request.Context.OperationId + ":" + request.JobId, cancellationToken);
    }

    private async Task<long> CreateOutputsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        System.Text.Json.Nodes.JsonObject job,
        System.Text.Json.Nodes.JsonArray products,
        List<long> outputIds,
        CancellationToken cancellationToken)
    {
        var activityId = Int(job["activityID"]);
        var ownerId = Long(job["ownerID"]);
        var runs = Int(job["runs"]);
        var successes = 0L;
        var selectedProducts = products.OfType<System.Text.Json.Nodes.JsonObject>().ToArray();
        if (selectedProducts.Length == 0 && activityId == CopyingActivity)
            selectedProducts = [new System.Text.Json.Nodes.JsonObject
            {
                ["typeID"] = Int(job["blueprintTypeID"]),
                ["quantity"] = 1,
                ["probability"] = 1d,
            }];
        var seedText = (job["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["rollSeed"]?.ToString();
        var seed = 0;
        if (!string.IsNullOrEmpty(seedText))
        {
            var bytes = Convert.FromBase64String(seedText);
            if (bytes.Length >= sizeof(int))
                seed = BitConverter.ToInt32(bytes, 0);
        }
        var random = new Random(seed);
        foreach (var product in selectedProducts)
        {
            var typeId = Int(product["typeID"]);
            var perRun = Long(product["quantity"], 1);
            var probability = Math.Clamp(Double(product["probability"], 1), 0, 1);
            if (typeId <= 0 || perRun <= 0)
                throw InvalidJob();
            long successful;
            if (activityId == InventionActivity)
            {
                successful = 0;
                for (var i = 0; i < runs; i++)
                    if (random.NextDouble() < probability)
                        successful++;
            }
            else
            {
                successful = runs;
            }
            if (activityId == InventionActivity)
                successes += successful;
            var blueprintProduct = activityId is CopyingActivity or InventionActivity;
            if (blueprintProduct)
            {
                for (long index = 0; index < successful; index++)
                {
                    var id = await _items.CreateAsync(connection, transaction,
                        new Eve.Inventory.NewItem(
                            typeId, ownerId, Long(job["outputLocationID"]), Int(job["outputFlagID"]),
                            1, true, BlueprintCopy: true, BlueprintRuns: Int(job["licensedRuns"]),
                            MaterialEfficiency: Int((job["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["blueprintMaterialEfficiency"]),
                            TimeEfficiency: Int((job["activitySnapshot"] as System.Text.Json.Nodes.JsonObject)?["blueprintTimeEfficiency"])),
                        cancellationToken);
                    outputIds.Add(id);
                }
            }
            else
            {
                var quantity = checked(perRun * successful);
                if (quantity <= 0)
                    continue;
                var id = await _items.CreateAsync(connection, transaction,
                    new Eve.Inventory.NewItem(typeId, ownerId, Long(job["outputLocationID"]), Int(job["outputFlagID"]), quantity),
                    cancellationToken);
                outputIds.Add(id);
                successes = Math.Max(successes, successful);
            }
        }
        return activityId == InventionActivity ? successes : runs;
    }

    private async Task<ActivityRecipe> ReadRecipeAsync(
        int blueprintTypeId,
        int activityId,
        CancellationToken cancellationToken)
    {
        var activities = await _sde.ReadByFieldAsync("industryActivity", "typeID", [blueprintTypeId], cancellationToken);
        var activity = activities.FirstOrDefault(row => Int(row.Doc, "activityID") == activityId);
        if (activity is null)
            throw InvalidJob();
        var materials = (await _sde.ReadByFieldAsync(
                "industryActivityMaterials", "typeID", [blueprintTypeId], cancellationToken))
            .Where(row => Int(row.Doc, "activityID") == activityId)
            .Select(row => new Material(Int(row.Doc, "materialTypeID"), Long(row.Doc, "quantity")))
            .Where(row => row.TypeId > 0 && row.Quantity > 0)
            .ToArray();
        var products = (await _sde.ReadByFieldAsync(
                "industryActivityProducts", "typeID", [blueprintTypeId], cancellationToken))
            .Where(row => Int(row.Doc, "activityID") == activityId)
            .Select(row => new Product(Int(row.Doc, "productTypeID"),
                Long(row.Doc, "quantity", 1), Double(row.Doc, "probability", 1)))
            .Where(row => row.TypeId > 0 && row.Quantity > 0)
            .ToArray();
        var skills = (await _sde.ReadByFieldAsync(
                "industryActivitySkills", "typeID", [blueprintTypeId], cancellationToken))
            .Where(row => Int(row.Doc, "activityID") == activityId)
            .Select(row => new SkillRequirement(Int(row.Doc, "skillID"), Int(row.Doc, "level")))
            .Where(row => row.TypeId > 0 && row.Level > 0)
            .ToArray();
        var time = checked((int)Math.Clamp(Long(activity.Doc, "time"), 1, int.MaxValue));
        if (materials.Length == 0 && products.Length == 0 &&
            activityId is not (MaterialResearchActivity or TimeResearchActivity))
            throw InvalidJob();
        return new ActivityRecipe(time, materials, products, skills);
    }

    private async Task<Dictionary<int, decimal>> ReadMarketPricesAsync(
        IReadOnlyCollection<int> typeIds,
        CancellationToken cancellationToken)
    {
        var rows = await _sde.ReadAsync("marketPrices", typeIds.Distinct().Select(id => (long)id), cancellationToken);
        var prices = new Dictionary<int, decimal>();
        foreach (var row in rows)
        {
            var typeId = checked((int)row.Key);
            var price = Decimal(row.Doc, "adjustedPrice");
            if (price <= 0)
                price = Decimal(row.Doc, "averagePrice");
            if (price > 0)
                prices[typeId] = price;
        }
        return prices;
    }

    private async Task<decimal> ReadCostIndexAsync(
        long solarSystemId,
        int activityId,
        CancellationToken cancellationToken)
    {
        var rows = await _sde.ReadAsync("industrySystems", [solarSystemId], cancellationToken);
        if (rows.Count == 0 ||
            !rows[0].Doc.TryGetProperty("costIndices", out var indices) ||
            indices.ValueKind != System.Text.Json.JsonValueKind.Array)
            throw InvalidJob();
        foreach (var index in indices.EnumerateArray())
        {
            if (Int(index, "activityID") != activityId)
                continue;
            var rate = Decimal(index, "costIndex");
            if (rate > 1)
                rate /= 100m;
            return Math.Clamp(rate, 0, 1);
        }
        throw InvalidJob();
    }


    public PyValue ReadMapIndustryCostModifier(
        int activityId,
        IReadOnlyList<Eve.Sde.SdeRow> systems)
    {
        var entries = new List<KeyValuePair<PyValue, PyValue>>();
        foreach (var system in systems)
        {
            if (!system.Doc.TryGetProperty("costIndices", out var costIndices) ||
                costIndices.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var index in costIndices.EnumerateArray())
            {
                if (Int(index, "activityID") != activityId)
                    continue;
                var rate = Decimal(index, "costIndex");
                if (rate > 1)
                    rate /= 100m;
                entries.Add(new(new PyInt(system.Key), new PyReal((double)Math.Clamp(rate, 0, 1))));
                break;
            }
        }
        return new PyDict(entries);
    }

    public async Task<PyValue> ReadMapIndustryJobsOverLast24HoursAsync(
        int activityId,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        const long fileTimeEpochOffset = 116444736000000000L;
        var nowFileTime = checked(fileTimeEpochOffset + nowUnixMs * 10_000L);
        var cutoff = nowFileTime - TimeSpan.TicksPerDay;
        var futureGrace = nowFileTime + TimeSpan.TicksPerMinute;

        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT solar_system_id, COUNT(*)::bigint
              FROM {_schema}.jobs
             WHERE solar_system_id > 0
               AND start_date_filetime >= $1
               AND start_date_filetime <= $2
               AND ($3 <= 0 OR activity_id = $3)
             GROUP BY solar_system_id
             ORDER BY solar_system_id
            """, connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = cutoff });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = futureGrace });
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = activityId });

        var rows = new List<PyValue>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Py.Tuple(new PyInt(reader.GetInt64(0)), new PyInt(reader.GetInt64(1))));
        return Py.Rowset(["solarSystemID", "noOfJobs"], rows);
    }

    private static IReadOnlyList<Product> SelectProducts(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        ActivityRecipe recipe)
    {
        if (job.ActivityId is MaterialResearchActivity or TimeResearchActivity)
            return [];
        if (job.ProductTypeId is { } selected)
        {
            var products = recipe.Products.Where(product => product.TypeId == selected).ToArray();
            if (products.Length == 0)
                throw InvalidJob();
            return products;
        }
        if (recipe.Products.Count == 1)
            return recipe.Products;
        if (job.ActivityId == CopyingActivity && recipe.Products.Count == 0)
            return [new Product(job.BlueprintTypeId, 1, 1)];
        throw InvalidJob();
    }

    private static IReadOnlyDictionary<int, long> RequiredMaterials(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        Eve.Inventory.Item blueprint,
        ActivityRecipe recipe)
    {
        var required = new SortedDictionary<int, long>();
        foreach (var material in recipe.Materials)
        {
            var amount = checked(material.Quantity * job.Runs);
            if (job.ActivityId == ManufacturingActivity && blueprint.MaterialEfficiency > 0)
                amount = checked((long)Math.Ceiling(amount * Math.Max(0, 100 - blueprint.MaterialEfficiency) / 100d));
            required[material.TypeId] = checked(required.GetValueOrDefault(material.TypeId) + amount);
        }
        if (job.OptionalTypeId is { } optionalTypeId)
            required[optionalTypeId] = checked(required.GetValueOrDefault(optionalTypeId) + 1);
        if (job.OptionalTypeId2 is { } optionalTypeId2)
            required[optionalTypeId2] = checked(required.GetValueOrDefault(optionalTypeId2) + 1);
        return required;
    }

    private static long CalculateCost(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        IReadOnlyList<Product> products,
        IReadOnlyDictionary<int, decimal> prices,
        decimal costIndex,
        decimal facilityTax)
    {
        decimal outputValue = 0;
        foreach (var product in products)
        {
            if (!prices.TryGetValue(product.TypeId, out var price) || price <= 0)
                throw InvalidJob();
            outputValue += price * product.Quantity * job.Runs;
        }
        if (products.Count == 0)
        {
            if (!prices.TryGetValue(job.BlueprintTypeId, out var price) || price <= 0)
                throw InvalidJob();
            outputValue = price * job.Runs;
        }
        return checked((long)decimal.Ceiling(outputValue * Math.Clamp(costIndex + facilityTax, 0, 1)));
    }

    private async Task<int> TimeInSecondsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long characterId,
        Eve.Services.Core.IndustryJobInstallSubmission job,
        ActivityRecipe recipe,
        IndustryFacility facility,
        CancellationToken cancellationToken)
    {
        var modifier = 1d;
        if (job.ActivityId == ManufacturingActivity)
        {
            var industry = await _skills.LevelAsync(connection, transaction, characterId, 3380, cancellationToken);
            var advancedIndustry = await _skills.LevelAsync(connection, transaction, characterId, 3388, cancellationToken);
            modifier = Math.Max(0.1, 1d - 0.04d * industry - 0.03d * advancedIndustry);
        }
        var duration = Math.Ceiling(recipe.TimeSeconds * (double)job.Runs * modifier * facility.TimeMultiplier);
        if (!double.IsFinite(duration) || duration < 1 || duration > int.MaxValue)
            throw InvalidJob();
        return checked((int)duration);
    }

    private async Task ConsumeMaterialsAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        Eve.Services.Core.IndustryJobInstallSubmission job,
        long blueprintId,
        IReadOnlyDictionary<int, long> requirements,
        CancellationToken cancellationToken)
    {
        var items = await _items.LockInLocationAsync(
            connection, transaction, job.InputLocation.LocationId, job.OwnerId,
            job.InputLocation.FlagId, requirements.Keys.ToArray(), cancellationToken);
        foreach (var requirement in requirements)
        {
            var remaining = requirement.Value;
            foreach (var item in items.Where(item => item.TypeId == requirement.Key && item.Id != blueprintId))
            {
                var available = item.Quantity > 0 ? item.Quantity : item.Singleton ? 1 : 0;
                if (available <= 0)
                    continue;
                var used = Math.Min(remaining, available);
                if (used == available)
                    await _items.DeleteAsync(connection, transaction, item.Id, cancellationToken);
                else
                    await _items.SetQuantityAsync(connection, transaction, item.Id, available - used, cancellationToken);
                remaining -= used;
                if (remaining == 0)
                    break;
            }
            if (remaining != 0)
                throw InvalidJob();
        }
    }

    private async Task ValidateActorAsync(
        Eve.Services.Core.IndustryJobActor actor,
        long ownerId,
        Eve.Services.Core.IndustryJobWallet wallet,
        long stationId,
        CancellationToken cancellationToken)
    {
        if (actor.CharacterId <= 0 || ownerId != actor.CharacterId && ownerId != actor.CorporationId ||
            wallet.OwnerId != ownerId && wallet.OwnerId != actor.CharacterId ||
            wallet.AccountKey is < 1000 or > 1006)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        if (wallet.OwnerId == actor.CharacterId && wallet.AccountKey != 1000)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        if (ownerId == actor.CharacterId)
            return;

        var corporation = await _corporations.GetAsync(ownerId, cancellationToken);
        var member = await _corporations.MemberAsync(actor.CharacterId, cancellationToken);
        if (corporation is null || corporation.IsNpc || member is null ||
            member.CorporationId != ownerId || corporation.Id != actor.CorporationId)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, stationId > 0 ? stationId : null,
            await _corporations.TitlesAsync(ownerId, cancellationToken)).CorpRole;
        if ((roles & (FactoryManagerRole | DirectorRole)) == 0)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
        if (wallet.OwnerId == ownerId &&
            (roles & Eve.Corporations.CorporationRoles.WalletTakeRole(wallet.AccountKey)) == 0)
            throw new Eve.Services.Core.IndustryJobUserErrorException("IndustryInvalidOwner");
    }

    private async Task<bool> CanManageExistingJobAsync(
        Eve.Services.Core.IndustryJobActor actor,
        long ownerId,
        long installerId,
        int stationId,
        CancellationToken cancellationToken)
    {
        if (actor.CharacterId <= 0)
            return false;
        if (actor.CharacterId == installerId)
            return true;
        if (ownerId != actor.CorporationId)
            return ownerId == actor.CharacterId && actor.CharacterId == installerId;
        var corporation = await _corporations.GetAsync(ownerId, cancellationToken);
        var member = await _corporations.MemberAsync(actor.CharacterId, cancellationToken);
        if (corporation is null || member is null || member.CorporationId != ownerId)
            return false;
        var roles = Eve.Corporations.CorporationRoles.Session(
            corporation, member, stationId > 0 ? stationId : null,
            await _corporations.TitlesAsync(ownerId, cancellationToken)).CorpRole;
        return (roles & (FactoryManagerRole | DirectorRole)) != 0;
    }

    private JsonObject ActivitySnapshot(
        Eve.Services.Core.IndustryJobInstallSubmission job,
        Eve.Inventory.Item blueprint,
        ActivityRecipe recipe,
        IReadOnlyDictionary<int, long> materials,
        IReadOnlyList<Product> products,
        long cost,
        int duration,
        IndustryFacility facility)
    {
        var materialRows = new System.Text.Json.Nodes.JsonArray();
        foreach (var material in materials)
            materialRows.Add(new System.Text.Json.Nodes.JsonObject { ["typeID"] = material.Key, ["quantity"] = material.Value });
        var productRows = new System.Text.Json.Nodes.JsonArray();
        foreach (var product in products)
            productRows.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["typeID"] = product.TypeId,
                ["quantity"] = product.Quantity,
                ["probability"] = product.Probability,
            });
        return new JsonObject
        {
            ["activityID"] = job.ActivityId,
            ["materials"] = materialRows,
            ["products"] = productRows,
            ["blueprintRuns"] = blueprint.BlueprintRuns,
            ["blueprintMaterialEfficiency"] = blueprint.MaterialEfficiency,
            ["blueprintTimeEfficiency"] = blueprint.TimeEfficiency,
            ["blueprintCopy"] = blueprint.BlueprintCopy,
            ["facilityTaxRate"] = facility.TaxRate,
            ["timeMultiplier"] = facility.TimeMultiplier,
            ["costIsk"] = cost,
            ["timeInSeconds"] = duration,
            ["rollSeed"] = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            ["requiredSkills"] = new System.Text.Json.Nodes.JsonArray(recipe.Skills.Select(skill =>
                (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
                {
                    ["typeID"] = skill.TypeId,
                    ["level"] = skill.Level,
                }).ToArray()),
        };
    }

    private static string Fingerprint(Eve.Services.Core.IndustryJobInstallSubmission job)
    {
        var canonical = string.Join("|",
            job.ActivityId, job.BlueprintId, job.BlueprintTypeId, job.BlueprintCopy,
            job.BlueprintLocationId, job.BlueprintLocationFlagId, job.FacilityId, job.OwnerId,
            job.SolarSystemId, job.StationId, job.InputLocation.LocationId, job.InputLocation.FlagId,
            job.OutputLocation.LocationId, job.OutputLocation.FlagId, job.Runs, job.LicensedRuns,
            job.ProductTypeId, job.OptionalTypeId, job.OptionalTypeId2,
            job.Wallet.OwnerId, job.Wallet.AccountKey);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical)));
    }

    private Task PublishChangedAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long jobId,
        long ownerId,
        long blueprintId,
        long installerId,
        int status,
        long successfulRuns,
        string operationId,
        CancellationToken cancellationToken) =>
        _events.PublishAsync(
            connection, transaction,
            Eve.Services.Core.IndustryJobEvents.Changed,
            new Eve.Services.Core.IndustryJobChangedEvent(
                jobId, ownerId, blueprintId, installerId, status, successfulRuns, operationId),
            operationId,
            cancellationToken);

private void Announce(Eve.Wallet.WalletSettlement? settlement)
    {
        if (settlement is null)
            return;
        foreach (var entry in settlement.Entries)
            _wallets.AnnounceCommitted(new Eve.Wallet.WalletBalanceChange(
                entry.OwnerId, entry.AccountKey, entry.Balance));
    }

    private static void Add(Npgsql.NpgsqlCommand command, params object?[] values)
    {
        foreach (var value in values)
            command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = value ?? DBNull.Value });
    }

    private async Task<(long JobId, string Fingerprint)?> FindInstallReplayAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _source.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand(
            $"SELECT job_id, install_request_fingerprint FROM {_schema}.jobs WHERE install_operation_id = $1",
            connection);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private async Task<(long JobId, string Fingerprint)?> ReadInstallReplayAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        string operationId,
        CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand(
            $"SELECT job_id, install_request_fingerprint FROM {_schema}.jobs WHERE install_operation_id = $1 FOR UPDATE",
            connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = operationId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private async Task<JsonObject?> ReadJobAsync(
        Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction,
        long jobId,
        CancellationToken cancellationToken)
    {
        await using var command = new Npgsql.NpgsqlCommand($"""
            SELECT job_id, activity_id, blueprint_item_id, blueprint_type_id, blueprint_copy,
                   blueprint_location_id, blueprint_location_flag_id, facility_id, owner_id, status,
                   installer_id, completed_character_id, solar_system_id, station_id,
                   start_date_filetime, end_date_filetime, pause_date_filetime, runs, licensed_runs,
                   successful_runs, cost_isk, time_in_seconds, probability, product_type_id,
                   optional_type_id, optional_type_id2, output_location_id, output_flag_id,
                   payment_owner_id, payment_account_key, activity_snapshot, output_item_ids
              FROM {_schema}.jobs WHERE job_id = $1 FOR UPDATE
            """, connection, transaction);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = jobId });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;
        return ReadWireJob(reader);
    }

    private static JsonObject ReadWireJob(Npgsql.NpgsqlDataReader reader)
    {
        using var snapshot = reader.GetFieldValue<System.Text.Json.JsonDocument>(30);
        var outputIds = reader.GetFieldValue<long[]>(31);
        var activitySnapshot = JsonNode.Parse(snapshot.RootElement.GetRawText()) as JsonObject ?? new JsonObject();
        return new JsonObject
        {
            ["jobID"] = reader.GetInt64(0),
            ["activityID"] = reader.GetInt32(1),
            ["blueprintID"] = reader.GetInt64(2),
            ["blueprintTypeID"] = reader.GetInt32(3),
            ["blueprintCopy"] = reader.GetBoolean(4),
            ["blueprintLocationID"] = reader.GetInt64(5),
            ["blueprintLocationFlagID"] = reader.GetInt32(6),
            ["facilityID"] = reader.GetInt64(7),
            ["ownerID"] = reader.GetInt64(8),
            ["status"] = reader.GetInt32(9),
            ["installerID"] = reader.GetInt64(10),
            ["completedCharacterID"] = reader.GetInt64(11),
            ["solarSystemID"] = reader.GetInt64(12),
            ["stationID"] = reader.GetInt64(13),
            ["startDate"] = reader.GetInt64(14),
            ["endDate"] = reader.GetInt64(15),
            ["pauseDate"] = reader.IsDBNull(16) ? null : JsonValue.Create(reader.GetInt64(16)),
            ["runs"] = reader.GetInt32(17),
            ["licensedRuns"] = reader.GetInt32(18),
            ["successfulRuns"] = reader.GetInt64(19),
            ["totalCost"] = reader.GetInt64(20),
            ["timeInSeconds"] = reader.GetInt32(21),
            ["probability"] = reader.GetDouble(22),
            ["productTypeID"] = reader.IsDBNull(23) ? null : JsonValue.Create(reader.GetInt32(23)),
            ["optionalTypeID"] = reader.IsDBNull(24) ? null : JsonValue.Create(reader.GetInt32(24)),
            ["optionalTypeID2"] = reader.IsDBNull(25) ? null : JsonValue.Create(reader.GetInt32(25)),
            ["outputLocationID"] = reader.GetInt64(26),
            ["outputFlagID"] = reader.GetInt32(27),
            ["paymentOwnerID"] = reader.GetInt64(28),
            ["paymentAccountKey"] = reader.GetInt32(29),
            ["activitySnapshot"] = activitySnapshot,
            ["outputItemIDs"] = new JsonArray(outputIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
        };
    }

    private static long FileTime(long unixMs) => checked(116444736000000000L + unixMs * 10_000L);
    private static string Key(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static Eve.Services.Core.IndustryJobUserErrorException InvalidJob() =>
        new("IndustryInvalidJob");

    private static long Long(JsonNode? value, long fallback = 0) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static int Int(JsonNode? value, int fallback = 0) =>
        int.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : fallback;
    private static double Double(JsonNode? value, double fallback = 0) =>
        double.TryParse(value?.ToString(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var result) && double.IsFinite(result) ? result : fallback;
    private static decimal Decimal(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value))
            return 0;
        return value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetDecimal(out var result)
            ? result
            : decimal.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out result) ? result : 0;
    }
    private static long Long(JsonElement row, string field, long fallback = 0) =>
        row.TryGetProperty(field, out var value) && value.TryGetInt64(out var result) ? result : fallback;
    private static int Int(JsonElement row, string field, int fallback = 0) =>
        row.TryGetProperty(field, out var value) && value.TryGetInt32(out var result) ? result : fallback;
    private static double Double(JsonElement row, string field, double fallback = 0) =>
        row.TryGetProperty(field, out var value) && value.TryGetDouble(out var result) && double.IsFinite(result)
            ? result
            : fallback;
}


public sealed record PersistenceMailSendRecipients(
    IReadOnlyList<long> CharacterIds,
    string SenderName,
    int SenderLabelMask,
    IReadOnlyDictionary<long, int> LabelMasksByCharacterId);

public sealed class PersistenceMailSendHostAdapter(
    Func<string, JsonObject> readCacheTable,
    Func<string, string?, JsonNode?, Eve.GameStore.WriteResult> write,
    Func<string, CancellationToken, Task<int>> flushTableAsync,
    Func<string, CancellationToken, Task<JsonObject>> readPersistedTableAsync,
    Func<Eve.Services.Core.MailSendRequest, Eve.Services.ClientSession, CancellationToken, Task<PersistenceMailSendRecipients?>> resolveRecipientsAsync,
    Func<long> nowUnixMs,
    Action<long, Eve.Marshal.PyValue[]> sendNotification)
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public async Task<long?> SendAsync(
        Eve.Services.Core.MailSendRequest request,
        Eve.Services.ClientSession session,
        CancellationToken cancellationToken)
    {
        if (request.SenderCharacterId <= 0 || session.CharacterId != request.SenderCharacterId)
            return null;

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var recipients = await resolveRecipientsAsync(request, session, cancellationToken);
            if (recipients is null)
                return null;

            var mailState = readCacheTable("mail");
            var messages = mailState["messages"] as JsonObject ?? new JsonObject();
            var mailboxes = mailState["mailboxes"] as JsonObject ?? new JsonObject();
            var messageId = NextMailId(messages, mailboxes);
            var sentFiletime = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).UtcDateTime.ToFileTimeUtc();
            var messageKey = Key(messageId);
            var directRecipients = request.CharacterRecipientIds.Distinct().ToArray();
            var corpOrAllianceIds = request.CorporationOrAllianceRecipientIds?.Distinct().ToArray();
            var message = new JsonObject
            {
                ["messageID"] = messageId,
                ["senderID"] = request.SenderCharacterId,
                ["senderName"] = recipients.SenderName,
                ["toCharacterIDs"] = LongArray(directRecipients),
                ["toListID"] = OptionalLong(request.MailingListId),
                ["toCorpOrAllianceID"] = corpOrAllianceIds is null ? null : LongArray(corpOrAllianceIds),
                ["title"] = request.Title,
                ["subject"] = request.Title,
                ["body"] = request.Body,
                ["sentDate"] = sentFiletime,
                ["replyToMessageID"] = OptionalLong(request.ReplyToMessageId),
                ["forwardedFromMessageID"] = OptionalLong(request.ForwardedFromMessageId),
                ["statusMask"] = 0,
                ["labelMask"] = 0,
                ["read"] = false,
                ["trashed"] = false,
                ["replied"] = false,
                ["forwarded"] = false,
            };

            var statuses = new Dictionary<(long CharacterId, long MessageId), JsonObject>();
            foreach (var recipientId in recipients.CharacterIds.Distinct().Where(id => id != request.SenderCharacterId))
            {
                statuses[(recipientId, messageId)] = new JsonObject
                {
                    ["statusMask"] = 0,
                    ["labelMask"] = recipients.LabelMasksByCharacterId.GetValueOrDefault(recipientId, 1),
                };
            }

            var senderDirectlyIncluded = directRecipients.Contains(request.SenderCharacterId);
            var senderRead = !senderDirectlyIncluded &&
                request.MailingListId is null &&
                request.CorporationOrAllianceRecipientIds is null;
            statuses[(request.SenderCharacterId, messageId)] = new JsonObject
            {
                ["statusMask"] = senderRead ? 1 : 0,
                ["labelMask"] = recipients.SenderLabelMask,
            };

            var sourceFlags = new Dictionary<long, long>();
            if (request.ReplyToMessageId is { } replyId && replyId > 0)
                sourceFlags[replyId] = sourceFlags.GetValueOrDefault(replyId) | 2;
            if (request.ForwardedFromMessageId is { } forwardedId && forwardedId > 0)
                sourceFlags[forwardedId] = sourceFlags.GetValueOrDefault(forwardedId) | 4;
            foreach (var (sourceId, flag) in sourceFlags)
            {
                if (messages[Key(sourceId)] is not JsonObject source ||
                    Number(source["senderID"]) != request.SenderCharacterId ||
                    (mailboxes[Key(request.SenderCharacterId)] as JsonObject)?["statuses"] is not JsonObject senderStatuses ||
                    senderStatuses[Key(sourceId)] is not JsonObject sourceStatus)
                    continue;
                var updated = (JsonObject)sourceStatus.DeepClone();
                updated["statusMask"] = Number(sourceStatus["statusMask"]) | flag;
                statuses[(request.SenderCharacterId, sourceId)] = updated;
            }

            RequireWrite("mail", $"/messages/{messageKey}", message);
            foreach (var ((characterId, statusMessageId), status) in statuses)
                RequireWrite("mail", $"/mailboxes/{Key(characterId)}/statuses/{Key(statusMessageId)}", status);

            await FlushUntilVisibleAsync("mail", persisted =>
            {
                var persistedMessages = persisted["messages"] as JsonObject;
                if (persistedMessages?[messageKey] is not JsonObject savedMessage ||
                    Number(savedMessage["senderID"]) != request.SenderCharacterId ||
                    !string.Equals(Text(savedMessage["title"]), request.Title, StringComparison.Ordinal) ||
                    !string.Equals(Text(savedMessage["body"]), request.Body, StringComparison.Ordinal))
                    return false;

                var persistedMailboxes = persisted["mailboxes"] as JsonObject;
                foreach (var ((characterId, statusMessageId), expected) in statuses)
                {
                    var actual = (persistedMailboxes?[Key(characterId)] as JsonObject)?["statuses"] is JsonObject rows
                        ? rows[Key(statusMessageId)] as JsonObject
                        : null;
                    if (actual is null ||
                        Number(actual["statusMask"]) != Number(expected["statusMask"]) ||
                        Number(actual["labelMask"]) != Number(expected["labelMask"]))
                        return false;
                }
                return true;
            }, cancellationToken);

            var notifications = readCacheTable("notifications");
            var notificationBoxes = notifications["boxes"] as JsonObject ?? new JsonObject();
            var nextNotificationId = NextNotificationId(notificationBoxes);
            var notificationIds = new Dictionary<long, long>();
            foreach (var recipientId in recipients.CharacterIds.Distinct().Where(id => id != request.SenderCharacterId))
            {
                var notificationId = nextNotificationId++;
                notificationIds[recipientId] = notificationId;
                var notificationMessage = (JsonObject)message.DeepClone();
                var notification = new JsonObject
                {
                    ["notificationID"] = notificationId,
                    ["typeID"] = 1004,
                    ["senderID"] = request.SenderCharacterId,
                    ["receiverID"] = recipientId,
                    ["processed"] = false,
                    ["created"] = sentFiletime,
                    ["data"] = new JsonObject
                    {
                        ["senderName"] = recipients.SenderName,
                        ["subject"] = request.Title,
                        ["msg"] = notificationMessage,
                    },
                };
                RequireWrite("notifications", $"/boxes/{Key(recipientId)}/byID/{Key(notificationId)}", notification);
            }

            if (notificationIds.Count > 0)
            {
                await FlushUntilVisibleAsync("notifications", persisted =>
                {
                    var boxes = persisted["boxes"] as JsonObject;
                    return notificationIds.All(pair =>
                        (boxes?[Key(pair.Key)] as JsonObject)?["byID"] is JsonObject rows &&
                        rows[Key(pair.Value)] is JsonObject saved &&
                        Number(saved["typeID"]) == 1004 &&
                        Number(saved["receiverID"]) == pair.Key);
                }, cancellationToken);

                foreach (var (recipientId, notificationId) in notificationIds)
                {
                    var data = NotificationData(
                        request.SenderCharacterId, recipients.SenderName,
                        request.Title, message, sentFiletime);
                    sendNotification(recipientId,
                    [
                        new Eve.Marshal.PyLong(notificationId),
                        new Eve.Marshal.PyInt(1004),
                        new Eve.Marshal.PyLong(request.SenderCharacterId),
                        Py.FileTimeLong(sentFiletime),
                        data,
                    ]);
                }
            }

            return messageId;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<long?> SendWelcomeMailAsync(
        long senderId,
        string senderName,
        long recipientCharacterId,
        string characterName,
        CancellationToken cancellationToken)
    {
        if (senderId <= 0 || recipientCharacterId <= 0 ||
            string.IsNullOrWhiteSpace(senderName) || string.IsNullOrWhiteSpace(characterName))
            return null;

        const string title = "Welcome to New Eden";
        var body = $"Welcome to New Eden, {System.Net.WebUtility.HtmlEncode(characterName)}.<br><br>" +
            "Your capsuleer career begins now. Fly safe.";

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var mailState = readCacheTable("mail");
            var messages = mailState["messages"] as JsonObject ?? new JsonObject();
            var mailboxes = mailState["mailboxes"] as JsonObject ?? new JsonObject();
            var mailboxStatuses = (mailboxes[Key(recipientCharacterId)] as JsonObject)?["statuses"] as JsonObject;
            if (mailboxStatuses is not null)
            {
                foreach (var (statusKey, _) in mailboxStatuses)
                    if (messages[statusKey] is JsonObject existing &&
                        Number(existing["welcomeMailForCharacterID"]) == recipientCharacterId &&
                        Number(existing["senderID"]) == senderId)
                        return Number(existing["messageID"]);
            }

            var messageId = NextMailId(messages, mailboxes);
            var sentFiletime = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).UtcDateTime.ToFileTimeUtc();
            var messageKey = Key(messageId);
            var message = new JsonObject
            {
                ["messageID"] = messageId,
                ["senderID"] = senderId,
                ["senderName"] = senderName,
                ["toCharacterIDs"] = LongArray([recipientCharacterId]),
                ["toListID"] = null,
                ["toCorpOrAllianceID"] = null,
                ["title"] = title,
                ["subject"] = title,
                ["body"] = body,
                ["sentDate"] = sentFiletime,
                ["replyToMessageID"] = null,
                ["forwardedFromMessageID"] = null,
                ["statusMask"] = 0,
                ["labelMask"] = 0,
                ["read"] = false,
                ["trashed"] = false,
                ["replied"] = false,
                ["forwarded"] = false,
                ["welcomeMailForCharacterID"] = recipientCharacterId,
            };
            var status = new JsonObject
            {
                ["statusMask"] = 0,
                ["labelMask"] = 1,
            };

            RequireWrite("mail", $"/messages/{messageKey}", message);
            RequireWrite("mail", $"/mailboxes/{Key(recipientCharacterId)}/statuses/{messageKey}", status);
            await FlushUntilVisibleAsync("mail", persisted =>
                persisted["messages"] is JsonObject persistedMessages &&
                persistedMessages[messageKey] is JsonObject savedMessage &&
                Number(savedMessage["senderID"]) == senderId &&
                Number(savedMessage["welcomeMailForCharacterID"]) == recipientCharacterId &&
                persisted["mailboxes"] is JsonObject persistedMailboxes &&
                persistedMailboxes[Key(recipientCharacterId)] is JsonObject mailbox &&
                mailbox["statuses"] is JsonObject savedStatuses &&
                savedStatuses[messageKey] is JsonObject savedStatus &&
                Number(savedStatus["statusMask"]) == 0 &&
                Number(savedStatus["labelMask"]) == 1, cancellationToken);

            var notifications = readCacheTable("notifications");
            var notificationBoxes = notifications["boxes"] as JsonObject ?? new JsonObject();
            var notificationId = NextNotificationId(notificationBoxes);
            var notification = new JsonObject
            {
                ["notificationID"] = notificationId,
                ["typeID"] = 1004,
                ["senderID"] = senderId,
                ["receiverID"] = recipientCharacterId,
                ["processed"] = false,
                ["created"] = sentFiletime,
                ["data"] = new JsonObject
                {
                    ["senderName"] = senderName,
                    ["subject"] = title,
                    ["msg"] = (JsonObject)message.DeepClone(),
                },
            };
            RequireWrite("notifications", $"/boxes/{Key(recipientCharacterId)}/byID/{Key(notificationId)}", notification);
            await FlushUntilVisibleAsync("notifications", persisted =>
                persisted["boxes"] is JsonObject boxes &&
                boxes[Key(recipientCharacterId)] is JsonObject recipientBox &&
                recipientBox["byID"] is JsonObject records &&
                records[Key(notificationId)] is JsonObject saved &&
                Number(saved["typeID"]) == 1004 &&
                Number(saved["receiverID"]) == recipientCharacterId, cancellationToken);

            sendNotification(recipientCharacterId,
            [
                new Eve.Marshal.PyLong(notificationId),
                new Eve.Marshal.PyInt(1004),
                new Eve.Marshal.PyLong(senderId),
                Py.FileTimeLong(sentFiletime),
                NotificationData(senderId, senderName, title, message, sentFiletime),
            ]);
            return messageId;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Persists and delivers a kill-right notification through the shared notification mailbox.</summary>
    public async Task WriteKillRightNotificationAsync(
        long recipientId,
        long senderId,
        int typeId,
        JsonObject data,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (recipientId <= 0 || senderId <= 0 || typeId <= 0)
            return;
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var boxes = readCacheTable("notifications")["boxes"] as JsonObject ?? new JsonObject();
            var recipientKey = Key(recipientId);
            if (idempotencyKey is not null &&
                (boxes[recipientKey] as JsonObject)?["byID"] is JsonObject existingRows &&
                existingRows.Any(entry => entry.Value is JsonObject existing &&
                    string.Equals(Text(existing["idempotencyKey"]), idempotencyKey, StringComparison.Ordinal)))
            {
                return;
            }

            var notificationId = NextNotificationId(boxes);
            var created = DateTimeOffset.FromUnixTimeMilliseconds(nowUnixMs()).UtcDateTime.ToFileTimeUtc();
            var notification = new JsonObject
            {
                ["notificationID"] = notificationId,
                ["typeID"] = typeId,
                ["senderID"] = senderId,
                ["receiverID"] = recipientId,
                ["groupID"] = Eve.Services.Crimewatch.KillRightNotificationTypes.MiscGroup,
                ["processed"] = false,
                ["created"] = created,
                ["data"] = data.DeepClone(),
            };
            if (idempotencyKey is not null)
                notification["idempotencyKey"] = idempotencyKey;
            RequireWrite("notifications", $"/boxes/{recipientKey}/byID/{Key(notificationId)}", notification);
            await FlushUntilVisibleAsync("notifications", persisted =>
            {
                var saved = (persisted["boxes"] as JsonObject)?[recipientKey] is JsonObject box &&
                    box["byID"] is JsonObject rows ? rows[Key(notificationId)] as JsonObject : null;
                return saved is not null && Number(saved["typeID"]) == typeId &&
                    Number(saved["senderID"]) == senderId && Number(saved["receiverID"]) == recipientId;
            }, cancellationToken);
            sendNotification(recipientId,
            [
                new Eve.Marshal.PyLong(notificationId),
                new Eve.Marshal.PyInt(typeId),
                new Eve.Marshal.PyLong(senderId),
                Py.FileTimeLong(created),
                NotificationWireData(data),
            ]);
        }
        finally
        {
            _sendGate.Release();
        }
    }
    private void RequireWrite(string table, string path, JsonNode data)
    {
        var result = write(table, path, data);
        if (!result.Success)
            throw new InvalidOperationException($"Could not persist {table}{path}: {result.ErrorMsg}");
    }

    private async Task FlushUntilVisibleAsync(
        string table,
        Func<JsonObject, bool> isVisible,
        CancellationToken cancellationToken)
    {
        var delayMs = 10;
        while (true)
        {
            await flushTableAsync(table, cancellationToken);
            if (isVisible(await readPersistedTableAsync(table, cancellationToken)))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), cancellationToken);
            delayMs = Math.Min(delayMs * 2, 250);
        }
    }

    private static long NextMailId(JsonObject messages, JsonObject mailboxes)
    {
        var maximum = MaximumNumericKey(messages);
        foreach (var (_, mailboxNode) in mailboxes)
            if (mailboxNode is JsonObject mailbox && mailbox["statuses"] is JsonObject statuses)
                maximum = Math.Max(maximum, MaximumNumericKey(statuses));
        return checked(maximum + 1);
    }

    private static long NextNotificationId(JsonObject boxes)
    {
        var maximum = 0L;
        foreach (var (_, boxNode) in boxes)
            if (boxNode is JsonObject box && box["byID"] is JsonObject records)
                maximum = Math.Max(maximum, MaximumNumericKey(records));
        return checked(maximum + 1);
    }

    private static long MaximumNumericKey(JsonObject rows)
    {
        var maximum = 0L;
        foreach (var (key, _) in rows)
            if (long.TryParse(key, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id) && id > maximum)
                maximum = id;
        return maximum;
    }

    private static long Number(JsonNode? value) =>
        long.TryParse(value?.ToString(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static string Text(JsonNode? value) => value?.ToString() ?? "";

    private static string Key(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static JsonArray LongArray(IEnumerable<long> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
            result.Add(value);
        return result;
    }

    private static JsonNode? OptionalLong(long? value) => value is { } id ? JsonValue.Create(id) : null;

    private static Eve.Marshal.PyValue NotificationWireData(JsonObject data) =>
        new Eve.Marshal.PyDict([.. data.Select(pair => new KeyValuePair<Eve.Marshal.PyValue, Eve.Marshal.PyValue>(
            new Eve.Marshal.PyStr(pair.Key), NotificationWireValue(pair.Value)))]);

    private static Eve.Marshal.PyValue NotificationWireValue(JsonNode? value)
    {
        if (value is not JsonValue scalar)
            return Eve.Marshal.PyNone.Instance;
        if (scalar.TryGetValue<long>(out var integer))
            return new Eve.Marshal.PyLong(integer);
        if (scalar.TryGetValue<decimal>(out var amount))
            return new Eve.Marshal.PyReal((double)amount);
        if (scalar.TryGetValue<double>(out var number) && double.IsFinite(number))
            return new Eve.Marshal.PyReal(number);
        if (scalar.TryGetValue<bool>(out var flag))
            return flag ? Eve.Marshal.PyBool.True : Eve.Marshal.PyBool.False;
        if (scalar.TryGetValue<string>(out var text))
            return new Eve.Marshal.PyStr(text);
        return Eve.Marshal.PyNone.Instance;
    }
    private static Eve.Marshal.PyValue NotificationData(
        long senderId,
        string senderName,
        string subject,
        JsonObject message,
        long sentFiletime)
    {
        var messageView = Py.KeyVal(
            ("messageID", new Eve.Marshal.PyLong(Number(message["messageID"]))),
            ("senderID", new Eve.Marshal.PyLong(senderId)),
            ("senderName", new Eve.Marshal.PyStr(senderName)),
            ("sentDate", Py.FileTimeLong(sentFiletime)),
            ("toCharacterIDs", new Eve.Marshal.PyList(
                ((JsonArray)message["toCharacterIDs"]!).Select(value => (Eve.Marshal.PyValue)new Eve.Marshal.PyLong(Number(value))).ToArray())),
            ("toListID", message["toListID"] is null ? Eve.Marshal.PyNone.Instance : new Eve.Marshal.PyLong(Number(message["toListID"]))),
            ("toCorpOrAllianceID", message["toCorpOrAllianceID"] is null
                ? Eve.Marshal.PyNone.Instance
                : new Eve.Marshal.PyList(((JsonArray)message["toCorpOrAllianceID"]!)
                    .Select(value => (Eve.Marshal.PyValue)new Eve.Marshal.PyLong(Number(value))).ToArray())),
            ("subject", new Eve.Marshal.PyStr(subject)),
            ("statusMask", new Eve.Marshal.PyInt(0)),
            ("labelMask", new Eve.Marshal.PyInt(0)),
            ("read", Eve.Marshal.PyBool.False),
            ("trashed", Eve.Marshal.PyBool.False),
            ("replied", Eve.Marshal.PyBool.False),
            ("forwarded", Eve.Marshal.PyBool.False));
        return Py.KeyVal(
            ("senderName", new Eve.Marshal.PyStr(senderName)),
            ("subject", new Eve.Marshal.PyStr(subject)),
            ("msg", messageView));
    }
}
