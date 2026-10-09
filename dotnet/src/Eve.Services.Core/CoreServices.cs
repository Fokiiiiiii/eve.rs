namespace Eve.Services.Core;
using Eve.Marshal;
using Eve.Macho;

/// <summary>The services ported so far, wired to their dependencies.</summary>

public static class CoreServices
{
    /// <summary>Creates every core service. Register them with <see cref="ServiceRegistry.Register"/>.</summary>
    public static IReadOnlyList<IService> Create(
        SubscriptionSettings settings,
        IOmegaAccountStore accounts,
        Func<long> nowUnixMs,
        Eve.Calendar.ICalendarStore? calendarStore = null,
        Eve.Calendar.ICalendarAudienceReader? calendarAudience = null,
        AuthenticationRpcService? authentication = null,
        InfoGatheringMgrService? infoGatheringMgr = null,
        EveguardReportService? eveguardReport = null,
        GameRpcCallbacks? gameRpcCallbacks = null,
        ReadJumpTimersCallback? readJumpTimers = null,
        ReadNonDiminishingInjectionCountCallback? readNonDiminishingInjections = null,
        SubmitInGameReportCallback? submitInGameReport = null,
        NodeGraphRpcCallback? nodeGraphRpcCallback = null,
        ReadEscalatingPathDetailsCallback? readEscalatingPathDetails = null,
        KeeperRpcCallbacks? keeperRpcCallbacks = null,
        ReadControlTowerRowsCallback? readControlTowerRows = null,
        ReadCommunityFittingsCallback? readCommunityFittings = null,
        CustomAgencyProviderRpcCallbacks? customAgencyProviderRpcCallbacks = null,
        ReadEpicArcStatusCallback? readEpicArcStatus = null,
        SuperWeaponRpcCallbacks? superWeaponRpcCallbacks = null,
        TourneyQueryCallback? tourneyQueryCallback = null)
    {
        gameRpcCallbacks ??= new GameRpcCallbacks();
        var licenses = new OmegaLicenses(settings, accounts, nowUnixMs);
        var subscriptionManager = new SubscriptionMgrService(settings, licenses);
        var calendarManager = calendarStore is null
            ? new CalendarMgrService()
            : new CalendarMgrService(calendarStore,
                calendarAudience ?? throw new ArgumentNullException(nameof(calendarAudience)));
        var calendarProxy = calendarStore is null
            ? new CalendarProxyService()
            : new CalendarProxyService(calendarStore);
        return
        [
            new PingService(),
            new BrowserLockdownService(),
            new PosMgrService(readControlTowerRows),
            new KiringMgrService(),
            new PetitionerService(),
            new AbyssalContentManagerService(gameRpcCallbacks.AbyssalSpawnReportAsync),
            new DungeonExplorationMgrService(readEscalatingPathDetails, nowUnixMs),
            new KeeperService(keeperRpcCallbacks),
            new MiningScanMgrService(gameRpcCallbacks.ReadMiningScanResultsAsync),
            new NodeGraphService(nodeGraphRpcCallback),
            new PeaceTreatyMgrService(gameRpcCallbacks.ReadPeaceTreaties),
            new PublicQaToolsServerService(gameRpcCallbacks.PublicQaTools),
            new CorpFittingMgrService(readCommunityFittings),
            new CustomAgencyProviderService(customAgencyProviderRpcCallbacks),
            new EpicArcStatusService(readEpicArcStatus),
            new SuperWeaponMgrService(superWeaponRpcCallbacks),
            new TourneyMgrService(tourneyQueryCallback),
            new RaffleMgrService(gameRpcCallbacks.SeedRafflesAsync),
            new RaffleProxyService(),
            new ExpertSystemMgrService(),
            new ContractProxyService(),
            new JumpCloneService(),
            new ServiceGatewayMgrService(gameRpcCallbacks.SetHijackMode, gameRpcCallbacks.IsHijackModeEnabled),
            new SharedSettingsMgrService(gameRpcCallbacks.ReadSharedSettings, gameRpcCallbacks.WriteSharedSettingsAsync, nowUnixMs),
            new OverviewPresetMgrService(gameRpcCallbacks.ReadOverviewPresets, gameRpcCallbacks.WriteOverviewPresets),
            new ShipStanceMgrService(gameRpcCallbacks.SetShipStanceAsync),
            new SolarSystemInterferenceMgrService(gameRpcCallbacks.ReadInterferenceTableAsync, nowUnixMs),
            new SurveyService(gameRpcCallbacks.ReadActiveSurveyIdAsync, gameRpcCallbacks.RecordSurveyEventAsync),
            new WarStatisticMgrService(gameRpcCallbacks.ReadKillmailAsync, nowUnixMs),
            new LPStoreMgrService(),
            new BannedWordsService(),
            new AlphaInjectorMgrService(),
            new CacheService(),
              new ChatAuthenticationService(),
            new CommunityTrackMgrService(),
            new ContextualOfferMgrService(),
              new DynamicBountyMgrService(),
                new JumpTimersService(readJumpTimers),
              new InGameReportService(submitInGameReport),
              new NonDiminishingInjectionMgrService(readNonDiminishingInjections),
            new ProjectDiscoveryService(),
            new LocalizationServerService(),
              new SeasonManagerService(),
            authentication ?? new AuthenticationRpcService(),
            infoGatheringMgr ?? new InfoGatheringMgrService(),
            eveguardReport ?? new EveguardReportService(),
            subscriptionManager,
            new LoginRewardFacilitiesService(subscriptionManager),
            calendarManager,
            calendarProxy,
        ];
    }

    /// <summary>Creates the read-only lookup service for explicit host registration.</summary>
    public static IService CreateLookupService(ILookupDirectory directory) => new LookupSvcService(directory);


    /// <summary>Creates the lookup service from the legacy asynchronous source delegates.</summary>
    public static IService CreateLookupService(LookupSearchSources sources) => new LookupSvcService(sources);

    /// <summary>The names of the services <see cref="Create"/> returns.</summary>
    public static IReadOnlyList<string> Names => [.. Generated.Eve_Services_CoreServiceCatalog.Names, "ProjectDiscovery", "overviewPresetMgr"];
}
