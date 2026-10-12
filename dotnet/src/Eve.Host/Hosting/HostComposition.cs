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
using static Eve.Host.HostConfiguration;

namespace Eve.Host;

/// <summary>Builds the host: accounts and foundations, the legacy GameStore, the character services, the RPC registry and the server.
/// The order is the original order of Program.cs; every step of it is observable (log lines, scheduler registration, RPC registration).</summary>
internal static class HostComposition
{
    internal static async Task<HostRuntime> BuildAsync(HostSettings settings)
    {
        var accountOptions = settings.AccountOptions;
        var options = settings.Options;
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
        LegacyGameStore? legacy = null;
        IGlobalConfigPersistence globalConfigPersistence = new InMemoryGlobalConfigPersistence();
        var online = new OnlineCharacters();
        var connected = new ConnectedSessions();
        CharacterSessions? characterSessions = null;
        if (Env("EVE_PG_URL", "") is { Length: > 0 } pgUrl)
        {
            var schema = Env("EVE_IDENTITY_SCHEMA", PgAccountDirectory.DefaultSchema);
            var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl));
            var applied = await PgAccountDirectory.MigrateAsync(source, schema);
            Console.Error.WriteLine($"eve-host: accounts in PostgreSQL, schema {schema}{(applied.Count > 0 ? "; applied " + string.Join(", ", applied) : "")}");
            var directory = new PgAccountDirectory(source, accountOptions, schema);
            accounts = directory;
            var configSchema = Env("EVE_GLOBAL_CONFIG_SCHEMA", PgGlobalConfigPersistence.DefaultSchema);
            await PgGlobalConfigPersistence.MigrateAsync(source, configSchema);
            globalConfigPersistence = new PgGlobalConfigPersistence(source, configSchema);
            Func<long> clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foundations = await Foundations.CreateAsync(source, clock, Env, message => Console.Error.WriteLine($"eve-host: {message}"));
            legacy = await LegacyGameStoreComposition.OpenAsync(source);
            (characterServices, space, simulation, crimewatch, calendarStore, calendarAudience, gameRpcCallbacks, keeperRpcCallbacks, characterSessions) = await CharacterServicesComposition.CharacterServicesAsync(source, directory, online, clock, foundations, legacy.Store, legacy.Owner, legacy.Cache, options.NewCharacterExperienceMode, new AccountOmegaStore(directory), connected);
            // every feature has registered its job handlers by now: the scheduler may start running jobs
            await foundations.StartAsync();
        }
        else
        {
            Console.Error.WriteLine("eve-host: no EVE_PG_URL: accounts are kept in memory and lost when the host stops; no characters");
            accounts = new InMemoryAccountDirectory(accountOptions);
        }

        GameStoreCache? mailCache = legacy?.Cache;
        PersistenceStore? mailStore = legacy?.Store;
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
        IOmegaAccountStore omegaAccounts = new AccountOmegaStore(accounts as IAccountSubscriptions
            ?? throw new InvalidOperationException("the account directory keeps no subscription end times"));
        foreach (var service in CoreServices.Create(
            omegaAccounts, now, calendarStore, calendarAudience,
            gameRpcCallbacks: gameRpcCallbacks,
            readJumpTimers: readJumpTimers,
            readNonDiminishingInjections: readNonDiminishingInjections,
            keeperRpcCallbacks: keeperRpcCallbacks,
            superWeaponRpcCallbacks: superWeaponCallbacks,
            spaceModules: space,
            isShipInSpace: space is null ? null : (Func<ClientSession, bool>)space.IsInSpace))
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
            registry.RegisterAlias("tradeMgr", "trademgr");
            registry.RegisterAlias("standing2", "standingMgr");
        }
        if (space is not null)
        {
            space.Registry = registry;
        }
        // machoNet's configuration: the settings, with the administrator's writes on top (saved in PostgreSQL when it is configured)
        var config = await GlobalConfigStore.OpenAsync(GlobalConfig.Build(options), globalConfigPersistence);
        HostServer? server = null;
        var info = new HostMachoNetInfo(registry, config.Current);
        registry.Register(new MachoNetService(info, now, GlobalConfigWrites.Create(config, connected, () => server?.PublishClusterConfig())));

        var dispatcher = new PacketDispatcher(registry, now) { StrictRpc = settings.StrictRpc };
        dispatcher.OnProblem = (message, error) =>
        {
            Console.Error.WriteLine($"eve-host: {message}{(error is null ? "" : ": " + error.Message)}");
            if (error is not null)
                SentrySdk.CaptureException(error);
        };
        server = new HostServer(
            new HostOptions
            {
                Log = message => Console.Error.WriteLine($"eve-host: {message}"),
                SessionOpened = session =>
                {
                    connected.Add(session);
                    session.Closed += () =>
                    {
                        connected.Remove(session);
                        online.Leave(session.SessionId);
                    };
                },
            },
            dispatcher,
            accounts,
            () => MarshalCodec.Encode(config.Current()));

        return new HostRuntime(server, registry, legacy, foundations, space, crimewatch, simulation, characterSessions);
    }
}
