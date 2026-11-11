using System.Net;
using System.Net.Sockets;
using Eve.Characters;
using Eve.Data;
using Eve.Host;
using Eve.Host.Persistence;
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

    internal static async Task<HostRuntime> BuildAsync(HostSettings settings, Func<long>? clockOverride = null)
    {
        var accountOptions = settings.AccountOptions;
        var options = settings.Options;
        var xmppSettings = settings.Xmpp ?? XmppSettings.Disabled;
        IAccountDirectory accounts;
        IReadOnlyList<IService> characterServices = [];
        Eve.Services.Core.GameRpcCallbacks? gameRpcCallbacks = null;
        ReadDynamicBountyCallback? readDynamicBounty = null;
        TourneyQueryCallback? tourneyQueryCallback = null;
        Eve.Host.Persistence.PersistenceTournamentMatches? tournamentMatches = null;
        Eve.Services.Core.KeeperRpcCallbacks? keeperRpcCallbacks = null;
        Eve.Calendar.ICalendarStore? calendarStore = null;
        Eve.Calendar.ICalendarAudienceReader? calendarAudience = null;
        Eve.Services.Core.CalendarCspaBilling? calendarCspaBilling = null;
        Foundations? foundations = null;
        SpaceSessions? space = null;
        SpaceContainers? tournamentContainers = null;
        CharacterShips? tournamentShips = null;
        Eve.Services.Crimewatch.CrimewatchRuntime? crimewatch = null;
        SimClient? simulation = null;
        LegacyGameStore? legacy = null;
        IGlobalConfigPersistence globalConfigPersistence = new InMemoryGlobalConfigPersistence();
        var registry = new ServiceRegistry();
        var online = new OnlineCharacters();
        var connected = new ConnectedSessions();
        var logoffNow = clockOverride ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Eve.Host.Xmpp.XmppListener? xmppListener = null;
        System.Security.Cryptography.X509Certificates.X509Certificate2? xmppCertificate = null;
        CharacterSessions? characterSessions = null;
        PgCharacterStore? characterStore = null;
        OnlineStatusNotifications? onlineStatusNotifications = null;
        List<ICharacterEntryHook>? characterEntryHooks = null;
        PublicGateway.PublicGatewayRoutes? publicGateway = null;
        Eve.Services.Core.IContractReadModel? contractReadModel = null;
        Eve.Services.Core.IContractWriteModel? contractWriteModel = null;
        Func<long, int, CancellationToken, ValueTask<int>>? contractSkillLevel = null;
        Func<long, long, CancellationToken, ValueTask<decimal>>? contractBrokerFeeAdjustment = null;
        Func<long, long, CancellationToken, ValueTask<bool>>? hasContractManagerRole = null;
        Func<long, long, short, CancellationToken, ValueTask<bool>>? canManageCalendarGroupEvent = null;
        Func<long, CancellationToken, ValueTask<(long CorporationId, long AllianceId)>>? readCurrentCalendarGroups = null;
        Func<long, long, CancellationToken, ValueTask<bool>>? isCharacterInCurrentCorporation = null;
        Func<long> clock = clockOverride ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        NpgsqlDataSource? dataSource = null; // owned by the runtime: shut down with it, so its pooled connections return to the server
        PgDeployedStore? deployedRecords = null;
        Eve.Host.Persistence.PersistenceInGameReportStore? inGameReportStore = null;
        Eve.Host.Persistence.PgAccruedTimeStore? accruedTimeStore = null;
        Eve.Host.Persistence.RaffleProxyStore? raffleProxyStore = null;
        Eve.Host.Xmpp.XmppMotdStore? xmppMotdStore = null;
        Eve.Host.Xmpp.XmppPlayerChannelStore? xmppPlayerChannelStore = null;
        Eve.Host.ProjectDiscovery.ScientificReplayComposition? scientificReplay = null;
        HttpClient? discoveryHttp = null;
        try
        {
        if (Env("EVE_PG_URL", "") is { Length: > 0 } pgUrl)
        {
            var schema = Env("EVE_IDENTITY_SCHEMA", PgAccountDirectory.DefaultSchema);
            var source = dataSource = NpgsqlDataSource.Create(PgConnection.ToConnectionString(pgUrl));
            var supportSchema = Env("EVE_SUPPORT_SCHEMA", Eve.Host.Persistence.PersistenceInGameReportStore.DefaultSchema);
            await Eve.Host.Persistence.PersistenceInGameReportStore.MigrateAsync(source, supportSchema);
            inGameReportStore = new Eve.Host.Persistence.PersistenceInGameReportStore(source, supportSchema);
            accruedTimeStore = new Eve.Host.Persistence.PgAccruedTimeStore(source, Env("EVE_LOGIN_SCHEMA", schema));
            await accruedTimeStore.MigrateAsync();
            var contractSchema = Env("EVE_CONTRACTS_SCHEMA", Eve.Host.Persistence.PersistenceContractReadModel.DefaultSchema);
            var inventorySchema = Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema);
            await Eve.Host.Persistence.PersistenceContractReadModel.MigrateAsync(source, contractSchema);
            contractReadModel = new Eve.Host.Persistence.PersistenceContractReadModel(source, contractSchema,
                Eve.Sde.SdeReader.DefaultSchema, inventorySchema);
            var contractItems = new PgItemStore(source, inventorySchema);
            var contractWallets = new WalletTransactions(source, Env("EVE_WALLET_SCHEMA", PgWalletStore.DefaultSchema));
            raffleProxyStore = new Eve.Host.Persistence.RaffleProxyStore(source, contractItems, contractWallets, options.HyperNetPlexPriceOverride, Env("EVE_RAFFLE_SCHEMA", "raffle"), Eve.Sde.SdeReader.DefaultSchema,
                Env("EVE_IDENTITY_SCHEMA", Eve.Identity.PgAccountDirectory.DefaultSchema));
            await raffleProxyStore.MigrateAsync();
            var contractSkills = new PgSkillStore(source, Env("EVE_SKILLS_SCHEMA", PgSkillStore.DefaultSchema));
            var contractCharacters = characterStore = new Eve.Characters.PgCharacterStore(source,
                Env("EVE_CHARACTERS_SCHEMA", Eve.Characters.PgCharacterStore.DefaultSchema));
            var contractWalletStore = new PgWalletStore(source, Env("EVE_WALLET_SCHEMA", PgWalletStore.DefaultSchema));
            var contractCorporations = new Eve.Corporations.CorporationStore(source, contractCharacters,
                contractWalletStore, contractWallets, contractSkills, () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Env("EVE_CORPORATIONS_SCHEMA", Eve.Corporations.CorporationStore.DefaultSchema));
            async ValueTask<bool> IsCharacterInCurrentCorporationAsync(long characterId, long corporationId, CancellationToken ct)
            {
                if (characterId <= 0 || corporationId <= 0 ||
                    (await contractCharacters.GetAsync(characterId, ct))?.CorporationId != corporationId)
                    return false;
                return await contractCorporations.GetAsync(corporationId, ct) is { IsNpc: false };
            }
            isCharacterInCurrentCorporation = IsCharacterInCurrentCorporationAsync;
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
            foundations = await Foundations.CreateAsync(source, clock, Env, message => Console.Error.WriteLine($"eve-host: {message}"));
              if (raffleProxyStore is { } raffleStore)
              {
                  raffleStore.RegisterScheduler(foundations.Scheduler);
                  await raffleStore.ScheduleExistingAsync();
              }
            var contractExpiryJobs = new Eve.Host.Persistence.ContractExpiryJobs(source, contractItems, contractWallets,
                foundations.Scheduler, contractSchema, foundations.Events);
            contractExpiryJobs.Register();
            contractWriteModel = new Eve.Host.Persistence.PersistenceContractWriteModel(source, contractItems, contractWallets,
                clock, contractSchema, contractExpiryJobs, foundations.Events);
            legacy = await LegacyGameStoreComposition.OpenAsync(source, foundations.Events);
            var notices = new PublicGateway.NoticeHub();
            (characterServices, space, simulation, crimewatch, calendarStore, calendarAudience, calendarCspaBilling, gameRpcCallbacks, keeperRpcCallbacks, characterSessions, onlineStatusNotifications, contractBrokerFeeAdjustment, hasContractManagerRole, canManageCalendarGroupEvent, readCurrentCalendarGroups, readDynamicBounty) = await CharacterServicesComposition.CharacterServicesAsync(source, directory, online, clock, foundations, legacy.Store, legacy.Owner, legacy.Cache,
                options.NewCharacterExperienceMode, new AccountOmegaStore(directory), connected, notices, hooks => characterEntryHooks = hooks,
                containers => tournamentContainers = containers, ships => tournamentShips = ships, clusterOptions: options);
            if (characterSessions is not null)
                new Eve.Host.Persistence.ContractNotifications(characterSessions).Subscribe(foundations.Events);
            // the public gateway's skill plans: the tables are the skills module's (migration 0009, applied with its store)
            var gateway = new Dictionary<string, PublicGateway.GatewayHandler>(StringComparer.Ordinal);
            var skinrSchema = Env("EVE_SKINR_SCHEMA", Eve.Host.Persistence.PgShipSkinrStore.DefaultSchema);
            await Eve.Host.Persistence.PgShipSkinrStore.MigrateAsync(source, skinrSchema);
            var skinrStore = new Eve.Host.Persistence.PgShipSkinrStore(source, skinrSchema);
            var sde = new Eve.Sde.SdeReader(source);
            await Eve.Host.Persistence.PersistenceTournamentStore.MigrateAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentArenas.MigrateAsync(source);
            deployedRecords = new PgDeployedStore(source, Env("EVE_SPACE_SCHEMA", PgSpaceItemStore.DefaultSchema));
            var tournamentArenas = new Eve.Host.Persistence.PersistenceTournamentArenas(source, sde, contractCharacters,
                contractItems, deployedRecords, foundations.Events);
            if (Env("EVE_TOURNAMENT_ARENA_IMPORT", "") is { Length: > 0 } arenaImportPath)
            {
                var arenaJson = await File.ReadAllTextAsync(arenaImportPath);
                var arenaImports = JsonSerializer.Deserialize<Eve.Host.Persistence.TournamentArenaImport[]>(arenaJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Tournament arena import must be a JSON array.");
                foreach (var arena in arenaImports) await tournamentArenas.ImportIfMissingAsync(arena);
            }
            var tournaments = new Eve.Host.Persistence.PersistenceTournamentStore(source, sde, tournamentArenas.ReadSystemsAsync, includeActiveMatches: true);
            if (Env("EVE_TOURNAMENT_IMPORT", "") is { Length: > 0 } tournamentImportPath)
            {
                var json = await File.ReadAllTextAsync(tournamentImportPath);
                var imports = JsonSerializer.Deserialize<Eve.Host.Persistence.TournamentImport[]>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Tournament import must be a JSON array.");
                foreach (var import in imports)
                    await tournaments.ImportIfMissingAsync(import.Configuration, import.Series);
            }
            tourneyQueryCallback = tournaments.QueryAsync;
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateFleetAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateSpaceAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateAutobansAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRecoveryAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRefereeTravelAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRulesAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigratePhysicalRegionsAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRefereeDirectionAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRosterAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRefereeMoveIdsAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateParticipantMoveIdsAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateParticipantIntentAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateCleanupGuardsAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateCleanupStagesAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateCleanupScanAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigratePostgameAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateParticipantDeathsAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateRefereeLeaseGenerationAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateCompletionPolicyAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateCutoffIntentAsync(source);
            await Eve.Host.Persistence.PersistenceTournamentMatches.MigrateMetricsIntentAsync(source);
            tournamentMatches = new Eve.Host.Persistence.PersistenceTournamentMatches(source, tournamentArenas, contractItems,
                deployedRecords, foundations.Events, simulation, sde,
                scheduler: foundations.Scheduler, sessions: characterSessions,
                fleetSchema: Env("EVE_FLEET_SCHEMA", Eve.Services.Space.FleetStore.DefaultSchema), space: space,
                tournamentShips: tournamentShips, tournamentCharacters: contractCharacters, tournamentContainers: tournamentContainers);
            if (Env("EVE_TOURNAMENT_REGION_IMPORT", "") is { Length: > 0 } regionImportPath)
            {
                var regionJson = await File.ReadAllTextAsync(regionImportPath);
                var regions = JsonSerializer.Deserialize<Eve.Host.Persistence.TournamentRegionImport[]>(regionJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Tournament region import must be a JSON array.");
                var configuredArenas = await tournamentArenas.ReadSystemsAsync();
                foreach (var region in regions)
                {
                    var arena = configuredArenas.SingleOrDefault(a => a.SolarSystemId == region.SolarSystemId)
                        ?? throw new InvalidOperationException($"Tournament region system {region.SolarSystemId} has no arena.");
                    if (region.CenterBeaconOrdinal < 0 || region.CenterBeaconOrdinal >= arena.BeaconIds.Count)
                        throw new InvalidOperationException($"Tournament region system {region.SolarSystemId} has an invalid zero-based center beacon ordinal.");
                    await tournamentMatches.ConfigurePhysicalRegionAsync(region.SolarSystemId, arena.BeaconIds[region.CenterBeaconOrdinal], region.RadiusMeters);
                }
            }
            if (Env("EVE_TOURNAMENT_RULES_IMPORT", "") is { Length: > 0 } rulesImportPath)
            {
                var rulesJson = await File.ReadAllTextAsync(rulesImportPath);
                var ruleImports = JsonSerializer.Deserialize<Eve.Host.Persistence.TournamentRulesImport[]>(rulesJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Tournament rules import must be a JSON array.");
                foreach (var rules in ruleImports) await tournamentMatches.ImportRulesAsync(rules);
            }
            if (Env("EVE_TOURNAMENT_COMPLETION_POLICY_IMPORT", "") is { Length: > 0 } completionImportPath)
            {
                var json = await File.ReadAllTextAsync(completionImportPath);
                var policies = JsonSerializer.Deserialize<Eve.Host.Persistence.TournamentCompletionPolicyImport[]>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Tournament completion policy import must be a JSON array.");
                foreach (var policy in policies) await tournamentMatches.ImportCompletionPolicyAsync(policy);
            }
            tournamentMatches.RegisterActionHandlers();
            await tournamentMatches.InitializeCutoffRecoveryAsync();
            await tournamentMatches.InitializeMetricsRecoveryAsync();
            tournamentMatches.RegisterFleetHandlers();
            if (space is not null)
            {
                tournamentMatches.RegisterSpaceHandlers();
                await tournamentMatches.InitializeTournamentRestrictionsAsync();
                space.TournamentRestricted = tournamentMatches.IsTournamentRestricted;
            }
            if (characterEntryHooks is not null && !characterEntryHooks.Contains(tournamentMatches))
                characterEntryHooks.Add(tournamentMatches);
            await tournamentMatches.ResetAfterRestartAsync();
            var missionSde = new Eve.Sde.SdeReader(source, Eve.Sde.SdeReader.MissionSchema);
            var skinrCatalog = await ShipSkinrCatalog.LoadAsync(missionSde);
            var skinrSkillTypeIds = (await sde.ReadTypeSummariesAsync())
                .Where(type => type.Name is "Studio Management" or "Advanced Studio Management" or
                    "Batch Sequencing" or "Mass Sequencing" or "Industrial Sequencing" or
                    "Parallel Sequencing" or "Multi-thread Sequencing" or "Basic Induction" or
                    "Induction Efficiency" or "Advanced Induction Efficiency" or "Peak Induction Acceleration")
                .ToDictionary(type => type.Name, type => type.TypeId, StringComparer.Ordinal);
            var skinrSkills = new Eve.Skills.PgSkillStore(source, Env("EVE_SKILLS_SCHEMA", Eve.Skills.PgSkillStore.DefaultSchema));
            var skillCatalog = await Eve.Skills.SkillCatalog.LoadAsync(sde);
            foreach (var (name, handler) in PublicGateway.SkillPlanGateway.Handlers(new Eve.Skills.PgSkillPlanStore(source, Env("EVE_SKILLS_SCHEMA", Eve.Skills.PgSkillStore.DefaultSchema)), skillCatalog))
                gateway[name] = handler;
            // the character titles: the SDE's title ids, and the owned and worn titles of the characters schema
            var titles = await LoadCharacterTitleIdsAsync(source, Eve.Sde.SdeReader.DefaultSchema);
            var titleStore = new Eve.Characters.CharacterTitleStore(source, Env("EVE_CHARACTERS_SCHEMA", Eve.Characters.PgCharacterStore.DefaultSchema));
            foreach (var (name, handler) in PublicGateway.ShipSkinrGateway.DraftHandlers(source, skinrStore, skinrCatalog, skinrSkills, skinrSkillTypeIds))
                gateway[name] = handler;
            Eve.Host.PublicGateway.ShipSkinrSequenceJobs.Register(foundations.Scheduler, skinrStore, foundations.Events);
            Eve.Host.PublicGateway.ShipSkinrNoticeHandlers.Register(foundations.Events, foundations.Scheduler, notices);
            var skinrWallets = new Eve.Wallet.PgWalletStore(source, Env("EVE_WALLET_SCHEMA", Eve.Wallet.PgWalletStore.DefaultSchema));
            var skinrSessions = characterSessions ?? new CharacterSessions();
            var skinrGateway = new PublicGateway.ShipSkinrSequencingGateway(source, skinrStore, skinrCatalog,
                skinrSkills, skinrSkillTypeIds, contractItems, Env("EVE_INVENTORY_SCHEMA", PgItemStore.DefaultSchema),
                skinrWallets, foundations.Scheduler, notices, skinrSessions, legacy?.Cache, clock);
            foreach (var (name, handler) in skinrGateway.Handlers())
                gateway[name] = handler;
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
            // Retry detached bound-object cleanup under the scheduler's server-owned lifetime.
            foundations.Scheduler.Register("services.bound-release.retry", async (_, ct) => await registry.RetryPendingBoundReleasesAsync(ct));
            await foundations.Scheduler.EnsureRecurringAsync("services.bound-release.retry", "pending", TimeSpan.FromSeconds(5));
            // every feature has registered its job handlers by now: the scheduler may start running jobs
            await foundations.StartAsync();
        }
        else
        {
            Console.Error.WriteLine("eve-host: no EVE_PG_URL: accounts are kept in memory and lost when the host stops; no characters");
            accounts = new InMemoryAccountDirectory(accountOptions);
        }

        Eve.Host.ProjectDiscovery.ProjectDiscoveryBonusPolicy? discoveryBonus = null;
        var bonusPolicy = Env("EVE_PROJECT_DISCOVERY_BONUS_POLICY", "none");
        if (bonusPolicy == "daily")
        {
            if (dataSource is null) throw new InvalidOperationException("Project Discovery bonus requires PostgreSQL.");
            int maximum = int.Parse(Env("EVE_PROJECT_DISCOVERY_BONUS_DAILY_MAX", ""), System.Globalization.CultureInfo.InvariantCulture);
            long bonusXp = long.Parse(Env("EVE_PROJECT_DISCOVERY_BONUS_XP", ""), System.Globalization.CultureInfo.InvariantCulture);
            int resetHour = int.Parse(Env("EVE_PROJECT_DISCOVERY_BONUS_RESET_UTC_HOUR", ""), System.Globalization.CultureInfo.InvariantCulture);
            if (maximum <= 0 || bonusXp <= 0 || resetHour is < 0 or > 23) throw new InvalidOperationException("Invalid Project Discovery bonus policy.");
            discoveryBonus = new(dataSource, Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema), maximum, bonusXp, resetHour);
        }
        else if (bonusPolicy != "none") throw new InvalidOperationException("Unsupported Project Discovery bonus policy.");
        if (dataSource is not null)
        {
            var mode = Env("EVE_PROJECT_DISCOVERY_MODE", "scientific-replay");
            var bind = IPAddress.Parse(Env("EVE_PROJECT_DISCOVERY_ASSET_BIND", "127.0.0.1"));
            var port = int.Parse(Env("EVE_PROJECT_DISCOVERY_ASSET_PORT", "0"), System.Globalization.CultureInfo.InvariantCulture);
            var origin = Env("EVE_PROJECT_DISCOVERY_ASSET_ORIGIN", "");
            var replayOptions = Eve.Host.ProjectDiscovery.ScientificReplayOptions.Bundled with
            {
                ListenAddress = bind, ListenPort = port,
                AllowNonLoopback = Env("EVE_PROJECT_DISCOVERY_ASSET_ALLOW_NONLOOPBACK", "false") == "true",
                AdvertisedOrigin = origin.Length == 0 ? null : new Uri(origin, UriKind.Absolute), Bonus = discoveryBonus
            };
            var pins = new ProjectDiscoveryTaskPinStore(dataSource, Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema));
            var discoverySchema = Env("EVE_PROJECT_DISCOVERY_SCHEMA", "project_discovery");
            if (mode == "remote")
            {
                var protocol = Env("EVE_PROJECT_DISCOVERY_REMOTE_PROTOCOL", "covid");
                var remoteProject = Env("EVE_PROJECT_DISCOVERY_MMOS_PROJECT", "");
                if (protocol is not ("covid" or "exoplanet" or "subcellular") || string.IsNullOrWhiteSpace(remoteProject))
                    throw new InvalidOperationException("Invalid Project Discovery remote project and protocol.");
                discoveryHttp = new HttpClient();
                try
                {
                    var api = new Eve.Host.ProjectDiscovery.MmosApiClient(discoveryHttp, new Eve.Host.ProjectDiscovery.MmosApiOptions
                    {
                        Enabled = true, BaseUri = new Uri(Env("EVE_PROJECT_DISCOVERY_MMOS_ORIGIN", ""), UriKind.Absolute),
                        Credential = Env("EVE_PROJECT_DISCOVERY_MMOS_CREDENTIAL", ""), Secret = Env("EVE_PROJECT_DISCOVERY_MMOS_SECRET", "")
                    });
                    var remote = new Eve.Host.ProjectDiscovery.RemoteDiscoveryProvider(dataSource, pins, api,
                        new Eve.Host.ProjectDiscovery.RemoteDiscoveryOptions { Enabled = true, Game = Env("EVE_PROJECT_DISCOVERY_MMOS_GAME", ""), Project = remoteProject, Protocol = protocol }, discoverySchema, discoveryBonus, Env("EVE_CHARACTERS_SCHEMA", PgCharacterStore.DefaultSchema));
                    await remote.MigrateAsync();
                    gameRpcCallbacks = new Eve.Host.ProjectDiscovery.RemoteDiscoveryRpc(remote, protocol).Attach(gameRpcCallbacks ?? new Eve.Services.Core.GameRpcCallbacks());
                }
                catch { discoveryHttp.Dispose(); throw; }
            }
            else
            {
                scientificReplay = await Eve.Host.ProjectDiscovery.ScientificReplayComposition.StartAsync(mode, dataSource, pins, replayOptions, discoverySchema);
                if (scientificReplay is not null)
                    gameRpcCallbacks = scientificReplay.Attach(gameRpcCallbacks ?? new Eve.Services.Core.GameRpcCallbacks());
            }
        }
        gameRpcCallbacks = (gameRpcCallbacks ?? new Eve.Services.Core.GameRpcCallbacks()) with
        {
            ReadProjectDiscoveryBonusSamples = discoveryBonus is null ? (_, _, _) => ValueTask.FromResult(0) : discoveryBonus.ReadAsync,
            NotifyProjectDiscoveryStateAfterSubmit = discoveryBonus is not null
        };
        var activeSurveyId = HostConfiguration.ParseActiveSurveyId(Env("EVE_ACTIVE_SURVEY_ID", ""));
        var configuredGameRpcCallbacks = gameRpcCallbacks ?? new Eve.Services.Core.GameRpcCallbacks();
        gameRpcCallbacks = configuredGameRpcCallbacks with
        {
            ReadActiveSurveyIdAsync = configuredGameRpcCallbacks.ReadActiveSurveyIdAsync
                ?? (_ => ValueTask.FromResult(activeSurveyId)),
        };

        GameStoreCache? mailCache = legacy?.Cache;
        PersistenceStore? mailStore = legacy?.Store;
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
        ReadNonDiminishingInjectionCountCallback? readNonDiminishingInjections = mailStore is { } legacyCharacterStore
            ? async (characterId, cancellationToken) =>
            {
                var state = await PersistenceReadHelpers.ReadOptionalTableAsync(
                    legacyCharacterStore, "skillTradingState", cancellationToken);
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
            gameRpcCallbacks: raffleProxyStore is null ? gameRpcCallbacks : (gameRpcCallbacks ?? new Eve.Services.Core.GameRpcCallbacks()) with { RaffleProxy = new Eve.Services.Core.RaffleProxyCallbacks(raffleProxyStore.InvokeAsync) },
            readJumpTimers: readJumpTimers,
            readDynamicBounty: readDynamicBounty,
            readNonDiminishingInjections: readNonDiminishingInjections,
            readControlTowerRows: deployedRecords is { } deployed
                ? new ControlTowerReadModel(deployed).ReadAsync
                : static (_, _) => ValueTask.FromResult<IReadOnlyList<ControlTowerRow>>([]),
            submitInGameReport: inGameReportStore is { } reportStore ? reportStore.SubmitAsync : null,
            keeperRpcCallbacks: keeperRpcCallbacks,
            superWeaponRpcCallbacks: superWeaponCallbacks,
            tourneyQueryCallback: tourneyQueryCallback,
            tournamentMatchCallbacks: tournamentMatches is null ? null : new TourneyMatchCallbacks(tournamentMatches.CreateAsync, tournamentMatches.ReleaseAsync, tournamentMatches.ActionAsync,
                tournamentMatches.NominateFleetAsync, tournamentMatches.AcceptFleetAsync, tournamentMatches.SubmitBanAsync,
                SpaceActionAsync: tournamentMatches.SpaceActionAsync, RefTravelAsync: tournamentMatches.RefTravelAsync, RefHomeAsync: tournamentMatches.RefHomeAsync,
                ActiveCameraAsync: tournamentMatches.ActiveCameraAsync, RosterActionAsync: tournamentMatches.RosterActionAsync,
                ReleaseStampedAsync: tournamentMatches.ReleaseStampedAsync, CameraDetailsAsync: tournamentMatches.CameraDetailsAsync,
                OtherMatchAsync: tournamentMatches.OtherMatchAsync, RemoveRefereeAsync: tournamentMatches.RemoveRefereeAsync),
            spaceModules: space,
            isShipInSpace: space is null ? null : (Func<ClientSession, bool>)space.IsInSpace,
            unanchorControlTower: space is null ? null : (session, itemId, cancellationToken) => space.ControlTowerUnanchor is { } unanchor
                ? new ValueTask(unanchor(session, itemId, cancellationToken))
                : throw new InvalidOperationException("the control towers are not wired on this server"),
            anchorControlTower: space is null ? null : (session, itemId, position, cancellationToken) => space.ControlTowerAnchor is { } anchor
                ? new ValueTask(anchor(session, itemId, position, cancellationToken))
                : throw new InvalidOperationException("the control towers are not wired on this server"),
            contractReadModel: contractReadModel,
            contractWriteModel: contractWriteModel,
            contractInventoryReader: characterServices.Count > 0
                ? CharacterServicesComposition.SingleRegisteredService<InvBrokerService>(characterServices)
                : null,
            contractSkillLevel: contractSkillLevel,
            isCharacterInCurrentCorporation: isCharacterInCurrentCorporation,
            calendarSessions: characterSessions,
            canManageCalendarGroupEvent: canManageCalendarGroupEvent,
            readCurrentCalendarGroups: readCurrentCalendarGroups,
              contractBrokerFeeAdjustment: contractBrokerFeeAdjustment,
              hasContractManagerRole: hasContractManagerRole,
              localizationTextChanges: HostConfiguration.ParseLocalizationTextChanges(Env("EVE_LOCALIZATION_TEXT_CHANGES", "")),
            calendarCspaBilling: calendarCspaBilling))
        {
            registry.Register(service);
        }
        foreach (var service in characterServices)
        {
            registry.Register(service);
        }
        if (characterServices.Count == 0)
        {
            // The in-memory host has no character directory, but the client route still resolves.
            var emptySearchSources = new LookupSearchSources(
                _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
                _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
                _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]),
                _ => Task.FromResult<IReadOnlyList<LookupEntity>>([]));
            registry.Register(new SearchQueryService(emptySearchSources));
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
        if (raffleProxyStore is not null)
            raffleProxyStore.SetPlexPriceProvider((call, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var value = config.Current().Entries.FirstOrDefault(entry => Eve.Macho.JsSemantics.StringValue(entry.Key) == "HyperNetPlexPriceOverride").Value;
                long price = value switch
                {
                    PyInt n => n.Value,
                    PyLong n => n.Value,
                    PyBool b => b.Value ? 1 : 0,
                    PyReal n when double.IsFinite(n.Value) && n.Value >= -9223372036854775808d && n.Value < 9223372036854775808d => (long)Math.Truncate(n.Value),
                    _ when Eve.Macho.JsSemantics.StringValue(value) is { } text && long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
                    _ => 0,
                };
                return ValueTask.FromResult<double?>(price == 0 ? Eve.Services.AdvertisedPlexPrice.Get(call.Session) : price);
            });
        HostServer? server = null;
        var info = new HostMachoNetInfo(registry, config.Current, characterSessions);
        registry.Register(new MachoNetService(info, now, GlobalConfigWrites.Create(config, connected, () => server?.PublishClusterConfig())));
        registry.Register(new EventLogRpcService());
        var charMgr = characterServices.OfType<Eve.Services.CharacterInfo.CharMgrService>().FirstOrDefault();
        Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? readBlockedOwners =
            charMgr is null ? null : charMgr.ReadBlockedOwnersAsync;
        Func<long, CancellationToken, ValueTask<Eve.Services.CharacterInfo.CharMgrChatUserData?>>? readChatUserData =
            charMgr is null ? null : charMgr.ReadChatUserDataAsync;
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
            xmppPlayerChannelStore,
            readChatUserData);
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
                SsoTokenVerifier = SsoTokenVerifier.FromEnvironment(),
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
                SessionClosingAsync = async session =>
                {
                    if (session.CharacterId is not { } characterId || characterId <= 0 ||
                        characterStore is not { } characters || characterSessions is not { } sessions)
                        return;

                    await using var lifecycle = await online.AcquireCharacterLifecycleLockAsync(characterId);
                    if (!ReferenceEquals(sessions.Of(characterId), session))
                        return;

                    await characters.SetLastLogoffAsync(characterId,
                        DateTimeOffset.FromUnixTimeMilliseconds(logoffNow()));
                },
                SessionClosedAsync = async session =>
                {
                    if (tournamentMatches is not null) await tournamentMatches.ReleaseSessionAsync(session.SessionId);
                    try
                    {
                        using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await registry.RetryPendingBoundReleasesAsync(cleanupTimeout.Token);
                    }
                    catch (Exception error) { Console.Error.WriteLine($"eve-host: bound-object cleanup remains pending: {error.Message}"); }
                    if (accruedTimeStore is { } history && session.Get("accruedSessionId") is Eve.Marshal.PyLong rowId)
                        await history.CloseAsync(rowId.Value, DateTime.UtcNow.Ticks);
                },
            },
            dispatcher,
            accounts,
            () => MarshalCodec.Encode(config.Current()));

        return new HostRuntime(server, registry, legacy, foundations, space, crimewatch, simulation, characterSessions, publicGateway, dataSource, xmppListener, xmppCertificate, scientificReplay, discoveryHttp, onlineStatusNotifications, connected);
        }
        catch
        {
            async ValueTask Cleanup(Func<ValueTask> dispose)
            {
                try { await dispose(); }
                catch (Exception error) { Console.Error.WriteLine($"eve-host: startup cleanup failed: {error.GetType().Name}"); }
            }
            if (xmppListener is not null) await Cleanup(xmppListener.DisposeAsync);
            await Cleanup(() => { xmppCertificate?.Dispose(); return ValueTask.CompletedTask; });
            if (scientificReplay is not null) await Cleanup(scientificReplay.DisposeAsync);
            await Cleanup(() => { discoveryHttp?.Dispose(); return ValueTask.CompletedTask; });
            if (onlineStatusNotifications is not null) await Cleanup(onlineStatusNotifications.DisposeAsync);
            if (legacy is not null) await Cleanup(legacy.DisposeAsync);
            if (foundations is not null) await Cleanup(foundations.DisposeAsync);
            await Cleanup(() => { space?.Dispose(); return ValueTask.CompletedTask; });
            await Cleanup(() => { crimewatch?.Dispose(); return ValueTask.CompletedTask; });
            if (simulation is not null) await Cleanup(simulation.DisposeAsync);
            if (dataSource is not null) await Cleanup(dataSource.DisposeAsync);
            throw;
        }
    }
}
