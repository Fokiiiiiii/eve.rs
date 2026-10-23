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

internal static class HostComposition
{
    /// <summary>The ids of the SDE's character titles (characterTitles, keyed by uuid text).</summary>
    private static async Task<IReadOnlySet<Guid>> LoadCharacterTitleIdsAsync(NpgsqlDataSource source, string sdeSchema)
    {
        await using var command = source.CreateCommand($"SELECT key FROM {sdeSchema}.\"characterTitles\"");
        await using var reader = await command.ExecuteReaderAsync();
        var titles = new HashSet<Guid>();
        while (await reader.ReadAsync())
            titles.Add(Guid.Parse(reader.GetString(0)));
        return titles;
    }

    internal static async Task<HostRuntime> BuildAsync(HostSettings settings)
    {
        var accountOptions = settings.AccountOptions;
        var options = settings.Options;
        var xmppSettings = settings.Xmpp ?? XmppSettings.Disabled;
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
        Eve.Host.Xmpp.XmppListener? xmppListener = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? xmppCertificate = null;
        CharacterSessions? characterSessions = null;
        PublicGateway.PublicGatewayRoutes? publicGateway = null;
        Eve.Services.Core.IContractReadModel? contractReadModel = null;
        Eve.Services.Core.IContractWriteModel? contractWriteModel = null;
        Func<long, int, CancellationToken, ValueTask<int>>? contractSkillLevel = null;
        Func<long, long, CancellationToken, ValueTask<decimal>>? contractBrokerFeeAdjustment = null;
        Func<long, long, CancellationToken, ValueTask<bool>>? hasContractManagerRole = null;
        Func<long, long, short, CancellationToken, ValueTask<bool>>? canManageCalendarGroupEvent = null;
        Func<long, CancellationToken, ValueTask<(long CorporationId, long AllianceId)>>? readCurrentCalendarGroups = null;
        NpgsqlDataSource? dataSource = null; // owned by the runtime: shut down with it, so its pooled connections return to the server
        Eve.Host.Persistence.PgAccruedTimeStore? accruedTimeStore = null;
        Eve.Host.Xmpp.XmppMotdStore? xmppMotdStore = null;
        Eve.Host.Xmpp.XmppPlayerChannelStore? xmppPlayerChannelStore = null;
        if (Env("EVE_PG_URL", "") is { Length: > 0 } pgUrl)
        {
            var schema = Env("EVE_IDENTITY_SCHEMA", PgAccountDirectory.DefaultSchema);
            var source = dataSource = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl));
            accruedTimeStore = new Eve.Host.Persistence.PgAccruedTimeStore(source, Env("EVE_LOGIN_SCHEMA", schema));
            await accruedTimeStore.MigrateAsync();
            var contractSchema = Env("EVE_CONTRACTS_SCHEMA", Eve.Host.Persistence.PersistenceContractReadModel.DefaultSchema);
            await Eve.Host.Persistence.PersistenceContractReadModel.MigrateAsync(source, contractSchema);
            contractReadModel = new Eve.Host.Persistence.PersistenceContractReadModel(source, contractSchema);
            var contractItems = new PgItemStore(source, Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema));
            var contractWallets = new WalletTransactions(source, Env("EVE_WALLET_SCHEMA", PgWalletStore.DefaultSchema));
            var contractSkills = new PgSkillStore(source, Env("EVE_SKILLS_SCHEMA", PgSkillStore.DefaultSchema));
            async Task<int> ReadContractSkillAsync(long characterId, int typeId, CancellationToken ct)
            {
                await using var connection = await source.OpenConnectionAsync(ct);
                await using var transaction = await connection.BeginTransactionAsync(ct);
                return await contractSkills.LevelAsync(connection, transaction, characterId, typeId, ct);
            }
            contractSkillLevel = (characterId, typeId, ct) => new ValueTask<int>(ReadContractSkillAsync(characterId, typeId, ct));
            var applied = await PgAccountDirectory.MigrateAsync(source, schema);
            Console.Error.WriteLine($"eve-host: accounts in PostgreSQL, schema {schema}{(applied.Count > 0 ? "; applied " + string.Join(", ", applied) : "")}");
            var directory = new PgAccountDirectory(source, accountOptions, schema);
            accounts = directory;
            if (Env("EVE_GM_ACCOUNTS", "") is { Length: > 0 } gmGrants)
            {
                foreach (var (user, mask) in Eve.Services.GameMasterRoles.ParseGrants(gmGrants))
                {
                    var granted = await directory.GrantRolesAsync(user, mask);
                    Console.Error.WriteLine(HostConfiguration.GmAccountGrantLog(granted));
                }
            }
            var configSchema = Env("EVE_GLOBAL_CONFIG_SCHEMA", PgGlobalConfigPersistence.DefaultSchema);
            await PgGlobalConfigPersistence.MigrateAsync(source, configSchema);
            globalConfigPersistence = new PgGlobalConfigPersistence(source, configSchema);
            Func<long> clock = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foundations = await Foundations.CreateAsync(source, clock, Env, message => Console.Error.WriteLine($"eve-host: {message}"));
            var contractExpiryJobs = new Eve.Host.Persistence.ContractExpiryJobs(source, contractItems, contractWallets,
                foundations.Scheduler, contractSchema, foundations.Events);
            contractExpiryJobs.Register();
            contractWriteModel = new Eve.Host.Persistence.PersistenceContractWriteModel(source, contractItems, contractWallets,
                clock, contractSchema, contractExpiryJobs, foundations.Events);
            legacy = await LegacyGameStoreComposition.OpenAsync(source);
            (characterServices, space, simulation, crimewatch, calendarStore, calendarAudience, gameRpcCallbacks, keeperRpcCallbacks, characterSessions, contractBrokerFeeAdjustment, hasContractManagerRole, canManageCalendarGroupEvent, readCurrentCalendarGroups) = await CharacterServicesComposition.CharacterServicesAsync(source, directory, online, clock, foundations, legacy.Store, legacy.Owner, legacy.Cache, options.NewCharacterExperienceMode, new AccountOmegaStore(directory), connected);
            if (characterSessions is not null)
                new Eve.Host.Persistence.ContractNotifications(characterSessions).Subscribe(foundations.Events);
            // the public gateway's skill plans: the tables are the skills module's (migration 0009, applied with its store)
            var gateway = new Dictionary<string, PublicGateway.GatewayHandler>(StringComparer.Ordinal);
            var skillCatalog = await Eve.Skills.SkillCatalog.LoadAsync(new Eve.Sde.SdeReader(source));
            foreach (var (name, handler) in PublicGateway.SkillPlanGateway.Handlers(new Eve.Skills.PgSkillPlanStore(source, Env("EVE_SKILLS_SCHEMA", Eve.Skills.PgSkillStore.DefaultSchema)), skillCatalog))
                gateway[name] = handler;
            // the character titles: the SDE's title ids, and the owned and worn titles of the characters schema
            var titles = await LoadCharacterTitleIdsAsync(source, Eve.Sde.SdeReader.DefaultSchema);
            var titleStore = new Eve.Characters.CharacterTitleStore(source, Env("EVE_CHARACTERS_SCHEMA", Eve.Characters.PgCharacterStore.DefaultSchema));
            var notices = new PublicGateway.NoticeHub();
            foreach (var (name, handler) in PublicGateway.TitleGateway.Handlers(titleStore, titles, notices))
                gateway[name] = handler;
            var localChat = new PublicGateway.LocalChatGateway(connected, notices);
            foreach (var (name, handler) in localChat.Handlers())
                gateway[name] = handler;
            // a request's character counts only when the token's account owns it (the login JWT names the account)
            var gatewayCharacters = new Eve.Characters.PgCharacterStore(source, Env("EVE_CHARACTERS_SCHEMA", Eve.Characters.PgCharacterStore.DefaultSchema));
            var authority = new PublicGateway.GatewayAuthority(
                async (character, token) => (await gatewayCharacters.GetAsync(character, token))?.AccountId, () => DateTimeOffset.UtcNow);
            publicGateway = new PublicGateway.PublicGatewayRoutes(gateway, notices, authority)
            {
                CharacterTitleRewards = new PublicGateway.CharacterTitleRewardService(titleStore, titles, notices),
            };
            // every feature has registered its job handlers by now: the scheduler may start running jobs
            await foundations.StartAsync();
        }
        else
        {
            Console.Error.WriteLine("eve-host: no EVE_PG_URL: accounts are kept in memory and lost when the host stops; no characters");
            accounts = new InMemoryAccountDirectory(accountOptions);
        }

        var activeSurveyId = HostConfiguration.ParseActiveSurveyId(Env("EVE_ACTIVE_SURVEY_ID", ""));
        var configuredGameRpcCallbacks = gameRpcCallbacks ?? new Eve.Services.Core.GameRpcCallbacks();
        gameRpcCallbacks = configuredGameRpcCallbacks with
        {
            ReadActiveSurveyIdAsync = configuredGameRpcCallbacks.ReadActiveSurveyIdAsync
                ?? (_ => ValueTask.FromResult(activeSurveyId)),
        };

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
        var identityAccounts = accounts as PgAccountDirectory;
        Eve.Services.Core.AuthenticationRpcService? authentication = new(
            accruedTime: accruedTimeStore is { } accruedSessions
                ? async (call, ct) =>
                    call.Session.UserId is { } accountId
                        ? await accruedSessions.ReadWireValueAsync(accountId, DateTime.UtcNow.Ticks, ct)
                        : new Eve.Marshal.PyList([])
                : null,
            amUnderage: async (call, ct) =>
                identityAccounts is not null && call.Session.UserId is { } accountId
                    && await identityAccounts.IsUnderageAsync(checked((uint)accountId), ct));
        foreach (var service in CoreServices.Create(
            omegaAccounts, now, calendarStore, calendarAudience,
            authentication: authentication,
            gameRpcCallbacks: gameRpcCallbacks,
            readJumpTimers: readJumpTimers,
            readNonDiminishingInjections: readNonDiminishingInjections,
            keeperRpcCallbacks: keeperRpcCallbacks,
            superWeaponRpcCallbacks: superWeaponCallbacks,
            spaceModules: space,
            isShipInSpace: space is null ? null : (Func<ClientSession, bool>)space.IsInSpace,
            unanchorControlTower: space is null ? null : (session, itemId, cancellationToken) => space.ControlTowerUnanchor is { } unanchor
                ? new ValueTask(unanchor(session, itemId, cancellationToken))
                : throw new InvalidOperationException("the control towers are not wired on this server"),
            contractReadModel: contractReadModel,
            contractWriteModel: contractWriteModel,
            contractSkillLevel: contractSkillLevel,
            calendarSessions: characterSessions,
            canManageCalendarGroupEvent: canManageCalendarGroupEvent,
            readCurrentCalendarGroups: readCurrentCalendarGroups,
              contractBrokerFeeAdjustment: contractBrokerFeeAdjustment,
              hasContractManagerRole: hasContractManagerRole,
              localizationTextChanges: HostConfiguration.ParseLocalizationTextChanges(Env("EVE_LOCALIZATION_TEXT_CHANGES", ""))))
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
        var info = new HostMachoNetInfo(registry, config.Current, characterSessions);
        registry.Register(new MachoNetService(info, now, GlobalConfigWrites.Create(config, connected, () => server?.PublishClusterConfig())));
        registry.Register(new EventLogRpcService());
        var charMgr = characterServices.OfType<Eve.Services.CharacterInfo.CharMgrService>().FirstOrDefault();
        Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? readBlockedOwners =
            charMgr is null ? null : charMgr.ReadBlockedOwnersAsync;
        if (xmppSettings.Enabled && dataSource is { } xmppDataSource)
        {
            var xmppSchema = Env("EVE_XMPP_SCHEMA", "eve_xmpp");
            xmppMotdStore = await Eve.Host.Xmpp.XmppMotdStore.OpenAsync(xmppDataSource, xmppSchema);
            xmppPlayerChannelStore = await Eve.Host.Xmpp.XmppPlayerChannelStore.OpenAsync(xmppDataSource, xmppSchema);
        }
        var xmppHub = new Eve.Host.Xmpp.XmppChatHub(connected,
            new Eve.Host.Xmpp.XmppChatSettings(xmppSettings.Domain, xmppSettings.ConnectHost),
            xmppMotdStore,
            readBlockedOwners,
            xmppPlayerChannelStore);
        registry.Register(new Eve.Host.Xmpp.XmppChatMgrService(xmppHub));
        if (xmppSettings.Enabled)
        {
            if (xmppSettings.CertificatePath is { } certificatePath)
                xmppCertificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(
                    certificatePath, xmppSettings.CertificatePassword);
            var bindAddress = System.Net.IPAddress.Parse(xmppSettings.BindAddress);
            xmppListener = Eve.Host.Xmpp.XmppListener.Start(bindAddress, xmppSettings.Port, xmppHub, xmppCertificate);
            Console.Error.WriteLine($"eve-host: XMPP listening on {bindAddress}:{xmppSettings.Port}");
        }

        var dispatcher = new PacketDispatcher(registry, now)
        {
            StrictRpc = settings.StrictRpc,
            MaxExpensiveRpcUnitsPerMinute = int.Parse(HostConfiguration.Env("EVE_MAX_EXPENSIVE_RPC_UNITS_PER_MINUTE", "160"), System.Globalization.CultureInfo.InvariantCulture),
        };
        dispatcher.OnProblem = (message, error) =>
        {
            Console.Error.WriteLine($"eve-host: {message}{(error is null ? "" : ": " + error.Message)}");
            if (error is not null)
                SentrySdk.CaptureException(error);
        };
        server = new HostServer(
            new HostOptions
            {
                MaxFrontConnections = int.Parse(HostConfiguration.Env("EVE_MAX_FRONT_CONNECTIONS", "1024"), System.Globalization.CultureInfo.InvariantCulture),
                MaxPendingBytesPerSession = long.Parse(HostConfiguration.Env("EVE_MAX_PENDING_BYTES_PER_SESSION", "16777216"), System.Globalization.CultureInfo.InvariantCulture),
                MaxQueuedOutboundPacketsPerSession = int.Parse(HostConfiguration.Env("EVE_MAX_QUEUED_OUTBOUND_PACKETS_PER_SESSION", "512"), System.Globalization.CultureInfo.InvariantCulture),
                MaxQueuedOutboundBytesPerSession = long.Parse(HostConfiguration.Env("EVE_MAX_QUEUED_OUTBOUND_BYTES_PER_SESSION", "8388608"), System.Globalization.CultureInfo.InvariantCulture),
                MaxQueuedOutboundPacketsPerConnection = int.Parse(HostConfiguration.Env("EVE_MAX_QUEUED_OUTBOUND_PACKETS_PER_CONNECTION", "8192"), System.Globalization.CultureInfo.InvariantCulture),
                MaxQueuedOutboundBytesPerConnection = long.Parse(HostConfiguration.Env("EVE_MAX_QUEUED_OUTBOUND_BYTES_PER_CONNECTION", "67108864"), System.Globalization.CultureInfo.InvariantCulture),
                Log = message => Console.Error.WriteLine($"eve-host: {message}"),
                SessionOpened = session =>
                {
                    var previous = connected.AddExclusive(session);
                    previous?.RequestDisconnect("Another client logged in to this account.");
                    session.Closed += () =>
                    {
                        connected.Remove(session);
                        online.Leave(session.SessionId);
                    };
                },
                SessionOpenedAsync = async (session, ct) =>
                {
                    if (accruedTimeStore is not { } history || session.UserId is not { } accountId)
                        return;
                    var rowId = await history.OpenAsync(accountId, DateTime.UtcNow.Ticks, ct);
                    session.Set("accruedSessionId", new Eve.Marshal.PyLong(rowId));
                },
                SessionClosedAsync = async session =>
                {
                    if (accruedTimeStore is { } history && session.Get("accruedSessionId") is Eve.Marshal.PyLong rowId)
                        await history.CloseAsync(rowId.Value, DateTime.UtcNow.Ticks);
                },
            },
            dispatcher,
            accounts,
            () => MarshalCodec.Encode(config.Current()));

        return new HostRuntime(server, registry, legacy, foundations, space, crimewatch, simulation, characterSessions, publicGateway, dataSource, xmppListener, xmppCertificate);
    }
}
