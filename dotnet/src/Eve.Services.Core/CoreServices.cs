namespace Eve.Services.Core;

/// <summary>The services ported so far, wired to their dependencies.</summary>
using Eve.Marshal;
using Eve.Macho;

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

public sealed record LookupEntity(
    long Id,
    string? Name,
    long CorporationId = 0,
    long AllianceId = 0,
    long FactionId = 0,
    int Gender = 0,
    string? TickerName = null,
    bool IsNpc = false,
    int? WarPermit = null);

public sealed record LookupLocation(
    long Id,
    string? Name,
    int TypeId,
    int GroupId,
    long? SolarSystemId = null,
    long? ConstellationId = null,
    long? RegionId = null);

public sealed record LookupSearchSources(
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Characters,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Corporations,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Alliances,
    Func<CancellationToken, Task<IReadOnlyList<LookupEntity>>> Factions,
    Func<int, CancellationToken, Task<IReadOnlyList<LookupLocation>>>? Locations = null) : ILookupDirectory
{
    public async ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken) =>
        (await Characters(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupCharacter(row.Id, row.Name ?? string.Empty, Gender: row.Gender, CorporationId: row.CorporationId, AllianceId: row.AllianceId))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken) =>
        (await Corporations(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupCorporation(row.Id, row.Name ?? string.Empty, row.TickerName, row.FactionId, row.IsNpc, WarPermit: row.WarPermit))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken) =>
        (await Alliances(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupAlliance(row.Id, row.Name ?? string.Empty, row.TickerName, row.IsNpc, row.CorporationId, row.WarPermit))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken) =>
        (await Factions(cancellationToken).ConfigureAwait(false))
            .Select(row => new LookupFaction(row.Id, row.Name ?? string.Empty))
            .ToArray();

    public async ValueTask<IReadOnlyList<LookupLocation>> LocationsByGroupAsync(int groupId, CancellationToken cancellationToken) =>
        Locations is null ? [] : await Locations(groupId, cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// The lookup wire contract is a Python list of ordered util.KeyVal objects.
/// The host supplies live read-only snapshots backed by its character, corporation and universe stores.
/// </summary>
/// <summary>Persisted player character data supplied from PgCharacterStore.</summary>
public sealed record LookupCharacter(long Id, string Name, int TypeId = 1373, int Gender = 0, long CorporationId = 0, long? AllianceId = null);

/// <summary>Corporation records supplied from CorporationStore and its NPC seed.</summary>
public sealed record LookupCorporation(long Id, string Name, string? TickerName = null, long? FactionId = null, bool IsNpc = false, bool? AllowWar = null, int? WarPermit = null);

/// <summary>Alliance records supplied from CorporationStore.</summary>
public sealed record LookupAlliance(long Id, string Name, string? TickerName = null, bool IsNpc = false, long ExecutorCorporationId = 0, int? WarPermit = null);

/// <summary>Static faction rows supplied from Universe/SDE data.</summary>
public sealed record LookupFaction(long Id, string Name);

public enum SkyhookResourcePermission
{
    Read,
    Activate,
    Deactivate,
}

public enum SkyhookResourceResultCode
{
    Ok,
    NotFound,
    Forbidden,
    PersistenceFailure,
}

public sealed record SkyhookResourceResult<T>(SkyhookResourceResultCode Code, T? Value) where T : class;

/// <summary>Current bay stock after the Host applies the CCP maturation ramp and theft outcome.</summary>
public sealed record SkyhookReagentResource(
    long TypeId,
    long SecureStock,
    long InsecureStock);

public sealed record SkyhookTheftVulnerability(
    bool Vulnerable,
    long StartUnixSeconds,
    long EndUnixSeconds);

/// <summary>
/// A deployed skyhook backed by sovereignty persistence. Reagent configuration is resolved from
/// the current SDE and joined to the persisted stock and last-cycle state by the host adapter.
/// </summary>
public sealed record SkyhookResourceSnapshot(
    long SkyhookId,
    long TypeId,
    long SolarSystemId,
    long PlanetId,
    long CorporationId,
    long? AllianceId,
    bool ProductionActive,
    long? Workforce,
    string ResourceVersion,
    IReadOnlyList<SkyhookReagentResource> Reagents,
    SkyhookTheftVulnerability? TheftVulnerability,
    int? State = null);

public sealed record SkyhookReagentAmounts(long Secure, long Insecure);
public sealed record SkyhookWorkforce(long? Amount);
public sealed record SkyhookProductionStatus(bool Active);

/// <summary>
/// Implements the resource operations consumed by sovereigntyResourceSvc.py.
/// The host supplies SDE-backed resource definitions and durable sovereignty state; the read
/// authorization callback must apply the existing theft-vulnerability visibility rules.
/// The activation writer must revalidate owner and corporation role in the same transaction
/// that persists the status, so a role change cannot race the preflight check.
/// </summary>
public sealed class SkyhookResourceService
{
    private const long SkyhookTypeId = 81080;

    private readonly Func<long, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> _readSkyhook;
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyList<SkyhookResourceSnapshot>>> _readCorporationSkyhooks;
    private readonly Func<ServiceCall, SkyhookResourceSnapshot, SkyhookResourcePermission, CancellationToken, ValueTask<bool>> _authorize;
    private readonly Func<ServiceCall, SkyhookResourceSnapshot, bool, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> _persistActivation;


    public SkyhookResourceService(
        Func<long, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> readSkyhook,
        Func<long, CancellationToken, ValueTask<IReadOnlyList<SkyhookResourceSnapshot>>> readCorporationSkyhooks,
        Func<ServiceCall, SkyhookResourceSnapshot, SkyhookResourcePermission, CancellationToken, ValueTask<bool>> authorize,
        Func<ServiceCall, SkyhookResourceSnapshot, bool, CancellationToken, ValueTask<SkyhookResourceSnapshot?>> persistActivation)
    {
        _readSkyhook = readSkyhook ?? throw new ArgumentNullException(nameof(readSkyhook));
        _readCorporationSkyhooks = readCorporationSkyhooks ?? throw new ArgumentNullException(nameof(readCorporationSkyhooks));
        _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
        _persistActivation = persistActivation ?? throw new ArgumentNullException(nameof(persistActivation));
    }

    public async ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> GetSkyhookAsync(
        ServiceCall call, long skyhookId)
    {
        var site = await ReadDeployedSkyhookAsync(skyhookId, call.Cancellation);
        if (site is null)
            return new(SkyhookResourceResultCode.NotFound, null);
        if (!HasCharacter(call) || !await _authorize(call, site, SkyhookResourcePermission.Read, call.Cancellation))
            return new(SkyhookResourceResultCode.Forbidden, null);
        return new(SkyhookResourceResultCode.Ok, site);
    }

    public async ValueTask<SkyhookResourceResult<IReadOnlyList<SkyhookResourceSnapshot>>> GetAllByCorporationAsync(
        ServiceCall call, long corporationId)
    {
        var sessionCorporationId = call.Session.GetInt("corporationID") ?? 0;
        if (sessionCorporationId == 0)
            sessionCorporationId = call.Session.GetInt("corpid") ?? 0;
        if (!HasCharacter(call) || corporationId <= 0 || sessionCorporationId != corporationId)
            return new(SkyhookResourceResultCode.Forbidden, null);

        var candidates = await _readCorporationSkyhooks(corporationId, call.Cancellation);
        var visible = new List<SkyhookResourceSnapshot>();
        foreach (var site in candidates)
        {
            if (!IsDeployedSkyhook(site) || site.CorporationId != corporationId)
                continue;
            if (await _authorize(call, site, SkyhookResourcePermission.Read, call.Cancellation))
                visible.Add(site);
        }

        visible.Sort(static (left, right) =>
        {
            var systemOrder = left.SolarSystemId.CompareTo(right.SolarSystemId);
            return systemOrder != 0 ? systemOrder : left.SkyhookId.CompareTo(right.SkyhookId);
        });
        return new(SkyhookResourceResultCode.Ok, visible);
    }

    public ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> ActivateAsync(
        ServiceCall call, long skyhookId) => SetProductionActiveAsync(call, skyhookId, true);

    public ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> DeactivateAsync(
        ServiceCall call, long skyhookId) => SetProductionActiveAsync(call, skyhookId, false);

    public async ValueTask<SkyhookResourceResult<SkyhookProductionStatus>> IsProductionActiveAsync(
        ServiceCall call, long skyhookId)
    {
        var result = await GetSkyhookAsync(call, skyhookId);
        return result.Code == SkyhookResourceResultCode.Ok
            ? new(SkyhookResourceResultCode.Ok, new(result.Value!.ProductionActive))
            : new(result.Code, null);
    }

    public async ValueTask<SkyhookResourceResult<SkyhookReagentAmounts>> GetReagentsInSkyhookNowAsync(
        ServiceCall call, long skyhookId)
    {
        var result = await GetSkyhookAsync(call, skyhookId);
        if (result.Code != SkyhookResourceResultCode.Ok)
            return new(result.Code, null);

        var reagent = result.Value!.Reagents.FirstOrDefault();
        if (!IsValid(reagent))
            return new(SkyhookResourceResultCode.Ok, null);

        return new(SkyhookResourceResultCode.Ok, new(reagent!.SecureStock, reagent.InsecureStock));
    }

    public async ValueTask<SkyhookResourceResult<SkyhookWorkforce>> GetCurrentWorkforceForSkyhookAsync(
        ServiceCall call, long skyhookId)
    {
        var result = await GetSkyhookAsync(call, skyhookId);
        return result.Code == SkyhookResourceResultCode.Ok
            ? new(SkyhookResourceResultCode.Ok, new(result.Value!.Workforce))
            : new(result.Code, null);
    }

    private async ValueTask<SkyhookResourceResult<SkyhookResourceSnapshot>> SetProductionActiveAsync(
        ServiceCall call, long skyhookId, bool active)
    {
        var site = await ReadDeployedSkyhookAsync(skyhookId, call.Cancellation);
        if (site is null)
            return new(SkyhookResourceResultCode.NotFound, null);
        if (!HasCharacter(call) || call.Session.GetInt("corpid") != site.CorporationId ||
            !await _authorize(call, site,
                active ? SkyhookResourcePermission.Activate : SkyhookResourcePermission.Deactivate,
                call.Cancellation))
            return new(SkyhookResourceResultCode.Forbidden, null);
        if (site.ProductionActive == active)
            return new(SkyhookResourceResultCode.Ok, site);

        var updated = await _persistActivation(call, site, active, call.Cancellation);
        if (updated is null || updated.SkyhookId != site.SkyhookId ||
            updated.CorporationId != site.CorporationId || updated.ProductionActive != active ||
            !IsDeployedSkyhook(updated))
            return new(SkyhookResourceResultCode.PersistenceFailure, null);
        return new(SkyhookResourceResultCode.Ok, updated);
    }

    private async ValueTask<SkyhookResourceSnapshot?> ReadDeployedSkyhookAsync(
        long skyhookId, CancellationToken cancellationToken)
    {
        if (skyhookId <= 0)
            return null;
        var site = await _readSkyhook(skyhookId, cancellationToken);
        return site is not null && site.SkyhookId == skyhookId && IsDeployedSkyhook(site) ? site : null;
    }

    private static bool HasCharacter(ServiceCall call) => call.Session.CharacterId is > 0;

    private static bool IsDeployedSkyhook(SkyhookResourceSnapshot site) =>
        site.TypeId == SkyhookTypeId && site.SkyhookId > 0 && site.SolarSystemId > 0 &&
        site.PlanetId > 0 && site.CorporationId > 0 && site.Reagents is not null;

    private static bool IsValid(SkyhookReagentResource? reagent) =>
        reagent is not null && reagent.TypeId > 0 &&
        reagent.SecureStock >= 0 && reagent.InsecureStock >= 0;

}

/// <summary>
/// The Host adapter exposes the owner rows from the existing character, corporation,
/// alliance, and universe stores. The core service owns EVE.js filtering and wire shape.
/// </summary>
public interface ILookupDirectory
{
    ValueTask<IReadOnlyList<LookupCharacter>> CharactersAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupCorporation>> CorporationsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupAlliance>> AlliancesAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupFaction>> FactionsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<LookupLocation>> LocationsByGroupAsync(int groupId, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<LookupLocation>>([]);
}

/// <summary>
/// EVE.js v0.12.9 lookupSvc owner lookup methods, pinned to
/// 828823a75f211fdecb945b087a26c225f111491f.
/// </summary>
public sealed class SkyhookResourceMgrService : IService
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "GetMyCorporationSkyhooks",
    };

    private readonly SkyhookResourceService resources;

    public SkyhookResourceMgrService(SkyhookResourceService resources)
    {
        this.resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public string Name => "colonyResourcesMgr";

    public bool HasMethod(string method) => Methods.Contains(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (call.Method != "GetMyCorporationSkyhooks")
            return PyNone.Instance;

        var corporationId = call.Session.GetInt("corporationID") ?? 0;
        if (corporationId == 0)
            corporationId = call.Session.GetInt("corpid") ?? 0;
        if (corporationId <= 0)
            return new PyList([]);

        var result = await resources.GetAllByCorporationAsync(call, corporationId);
        if (result.Code != SkyhookResourceResultCode.Ok)
        {
            var message = result.Code switch
            {
                SkyhookResourceResultCode.NotFound => "Skyhook not found.",
                SkyhookResourceResultCode.Forbidden => "You cannot access corporation skyhooks.",
                SkyhookResourceResultCode.PersistenceFailure => "Skyhook data could not be loaded.",
                _ => "Skyhook request failed.",
            };
            throw MachoErrors.ThrowUserError(
                "CustomNotify",
                ("notify", new PyStr(message)));
        }

        var rows = result.Value?
            .OrderBy(skyhook => skyhook.SolarSystemId)
            .ThenBy(skyhook => skyhook.PlanetId)
            .ThenBy(skyhook => skyhook.SkyhookId)
            .Select(SerializeSkyhook)
            .ToArray() ?? [];
        return new PyList(rows);
    }

    private static PyValue SerializeSkyhook(SkyhookResourceSnapshot skyhook)
    {
        // Production activation and theft vulnerability do not define the structure state.
        var state = skyhook.State is >= 210 and <= 214 ? skyhook.State.Value : 0;
        var allianceId = skyhook.AllianceId is > 0 ? skyhook.AllianceId.Value : 0;
        return Py.KeyVal(
            ("skyhookID", new PyInt(skyhook.SkyhookId)),
            ("itemID", new PyInt(skyhook.SkyhookId)),
            ("solarSystemID", new PyInt(skyhook.SolarSystemId)),
            ("planetID", new PyInt(skyhook.PlanetId)),
            ("typeID", new PyInt(skyhook.TypeId)),
            ("state", new PyInt(state)),
            ("corporationID", new PyInt(skyhook.CorporationId)),
            ("ownerCorpID", new PyInt(skyhook.CorporationId)),
            ("allianceID", new PyInt(allianceId)));
    }
}

public sealed class LookupSvcService : IService
{
    private readonly ILookupDirectory directory;
    private const int MaxLookupResults = 500;
    private const int CharacterTypeId = 1373;
    private const int CorporationTypeId = 2;
    private const int AllianceTypeId = 16159;
    private const int FactionTypeId = 30;
    private const int CharacterGroupId = 1;
    private const int CorporationGroupId = 2;
    private const int AllianceGroupId = 32;
    private const int FactionGroupId = 19;

    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "LookupCharacters",
        "LookupEvePlayerCharacters",
        "LookupPlayerCharacters",
        "LookupCorporations",
        "LookupFactions",
        "LookupLocationsByGroup",
        "LookupKnownLocationsByGroup",
        "LookupOwners",
        "LookupPCOwners",
        "LookupNoneNPCAccountOwners",
        "LookupWarableCorporationsOrAlliances",
    };

    public string Name => "lookupSvc";

    public LookupSvcService(ILookupDirectory directory)
    {
        this.directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    public bool HasMethod(string method) => Methods.Contains(method);

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        return call.Method switch
        {
            "LookupCharacters" or "LookupEvePlayerCharacters" or "LookupPlayerCharacters" => await LookupCharactersAsync(call),
            "LookupCorporations" => await LookupCorporationsAsync(call),
            "LookupFactions" => await LookupFactionsAsync(call),
            "LookupLocationsByGroup" or "LookupKnownLocationsByGroup" => await LookupLocationsByGroupAsync(call),
            "LookupOwners" => await LookupOwnersAsync(call, includeNpcCorporations: true, includeAlliances: true, includeFactions: true),
            "LookupPCOwners" => await LookupOwnersAsync(call, includeNpcCorporations: false, includeAlliances: true, includeFactions: false),
            "LookupNoneNPCAccountOwners" => await LookupOwnersAsync(call, includeNpcCorporations: false, includeAlliances: false, includeFactions: false),
            "LookupWarableCorporationsOrAlliances" => await LookupWarableCorporationsOrAlliancesAsync(call),
            _ => PyNone.Instance,
        };
    }

    private async ValueTask<PyValue> LookupCharactersAsync(ServiceCall call)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var characters = await directory.CharactersAsync(call.Cancellation);
        var entries = characters
            .Where(character => character.Id > 0)
            .Select(character => new OwnerEntry(
                character.Id,
                NameOr(character.Name, $"Character {character.Id}"),
                character.TypeId > 0 ? character.TypeId : CharacterTypeId,
                CharacterGroupId,
                character.Gender,
                CharacterId: character.Id,
                CorporationId: Math.Max(character.CorporationId, 0),
                AllianceId: Math.Max(character.AllianceId ?? 0, 0)));
        return AsList(Filter(entries, search, exactMode).Select(BuildCharacterRow));
    }

    private async ValueTask<PyValue> LookupCorporationsAsync(ServiceCall call)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var corporations = await directory.CorporationsAsync(call.Cancellation);
        var entries = corporations.Where(corporation => corporation.Id > 0)
            .Select(corporation => new OwnerEntry(
                corporation.Id,
                NameOr(corporation.Name, $"Corporation {corporation.Id}"),
                CorporationTypeId,
                CorporationGroupId,
                0,
                CorporationId: corporation.Id,
                FactionId: Math.Max(corporation.FactionId ?? 0, 0),
                TickerName: EmptyToNull(corporation.TickerName),
                IncludeTickerName: true,
                IsNpc: corporation.IsNpc));
        return AsList(Filter(entries, search, exactMode).Select(BuildCorporationRow));
    }

    private async ValueTask<PyValue> LookupFactionsAsync(ServiceCall call)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var factions = await directory.FactionsAsync(call.Cancellation);
        var entries = factions.Where(faction => faction.Id > 0)
            .Select(faction => new OwnerEntry(
                faction.Id,
                NameOr(faction.Name, $"Faction {faction.Id}"),
                FactionTypeId,
                FactionGroupId,
                0,
                FactionId: faction.Id));
        return AsList(Filter(entries, search, exactMode).Select(BuildFactionRow));
    }

    private async ValueTask<PyValue> LookupLocationsByGroupAsync(ServiceCall call)
    {
        var rawGroup = JsSemantics.NumberValue(call.Arg(0));
        var groupId = double.IsFinite(rawGroup) && Math.Truncate(rawGroup) is >= 0 and <= int.MaxValue
            ? (int)Math.Truncate(rawGroup)
            : 0;
        var search = SearchString(call.Arg(1)) ?? string.Empty;
        var exactMode = ExactMode(call.Arg(2));
        var locations = await directory.LocationsByGroupAsync(groupId, call.Cancellation);
        var rows = locations
            .Where(location => location.GroupId == groupId && location.Id > 0 && !string.IsNullOrWhiteSpace(location.Name))
            .Where(location => Matches(location.Name!, search, exactMode))
            .OrderBy(location => Rank(location.Name!, search))
            .ThenBy(location => location.Name, StringComparer.CurrentCulture)
            .ThenBy(location => location.Id)
            .Take(MaxLookupResults)
            .Select(BuildLocationRow);
        return AsList(rows);
    }

    private async ValueTask<PyValue> LookupOwnersAsync(
        ServiceCall call,
        bool includeNpcCorporations,
        bool includeAlliances,
        bool includeFactions)
    {
        var search = SearchString(call.Arg(0));
        var exactMode = ExactMode(call.Arg(1));
        var characters = await directory.CharactersAsync(call.Cancellation);
        var corporations = await directory.CorporationsAsync(call.Cancellation);
        var alliances = includeAlliances
            ? await directory.AlliancesAsync(call.Cancellation)
            : [];
        var factions = includeFactions
            ? await directory.FactionsAsync(call.Cancellation)
            : [];

        var entries = new List<OwnerEntry>(
            characters.Count + corporations.Count + alliances.Count + factions.Count);
        entries.AddRange(characters.Where(character => character.Id > 0).Select(character =>
            new OwnerEntry(
                character.Id,
                NameOr(character.Name, $"Character {character.Id}"),
                character.TypeId > 0 ? character.TypeId : CharacterTypeId,
                CharacterGroupId,
                character.Gender,
                CharacterId: character.Id,
                CorporationId: Math.Max(character.CorporationId, 0),
                AllianceId: Math.Max(character.AllianceId ?? 0, 0))));
        entries.AddRange(corporations
            .Where(corporation => corporation.Id > 0 && (includeNpcCorporations || !corporation.IsNpc))
            .Select(corporation => new OwnerEntry(
                corporation.Id,
                NameOr(corporation.Name, $"Corporation {corporation.Id}"),
                CorporationTypeId,
                CorporationGroupId,
                0,
                CorporationId: corporation.Id,
                FactionId: Math.Max(corporation.FactionId ?? 0, 0),
                TickerName: EmptyToNull(corporation.TickerName),
                IncludeTickerName: true,
                IsNpc: corporation.IsNpc)));
        entries.AddRange(alliances.Where(alliance => alliance.Id > 0).Select(alliance =>
            new OwnerEntry(
                alliance.Id,
                NameOr(alliance.Name, $"Alliance {alliance.Id}"),
                AllianceTypeId,
                AllianceGroupId,
                0,
                AllianceId: alliance.Id,
                TickerName: EmptyToNull(alliance.TickerName),
                IncludeTickerName: true,
                IsNpc: alliance.IsNpc)));
        entries.AddRange(factions.Where(faction => faction.Id > 0).Select(faction =>
            new OwnerEntry(
                faction.Id,
                NameOr(faction.Name, $"Faction {faction.Id}"),
                FactionTypeId,
                FactionGroupId,
                0,
                FactionId: faction.Id)));

        return AsList(Filter(entries, search, exactMode).Select(BuildOwnerRow));
    }

    private async ValueTask<PyValue> LookupWarableCorporationsOrAlliancesAsync(ServiceCall call)
    {
        var search = (SearchString(call.Arg(0)) ?? string.Empty).ToLowerInvariant();
        var exact = JsTruthy(call.Arg(1));
        var corporations = await directory.CorporationsAsync(call.Cancellation);
        var alliances = await directory.AlliancesAsync(call.Cancellation);
        var corporationsById = corporations.ToDictionary(corporation => corporation.Id);
        var rows = new List<PyValue>();

        foreach (var corporation in corporations.OrderBy(corporation => corporation.Id))
        {
            if (corporation.Id <= 0 || !WarableNameMatches(corporation.Name, search, exact))
            {
                continue;
            }
            rows.Add(WarableRow(corporation.Id, corporation.Name, CorporationTypeId, CorporationPermit(corporation)));
        }

        foreach (var alliance in alliances.OrderBy(alliance => alliance.Id))
        {
            if (alliance.Id <= 0 || !WarableNameMatches(alliance.Name, search, exact))
            {
                continue;
            }
            var permit = alliance.WarPermit is { } alliancePermit
                ? (alliancePermit == 0 ? 0 : 1)
                : corporationsById.TryGetValue(alliance.ExecutorCorporationId, out var executor)
                    ? CorporationPermit(executor)
                    : 0;
            rows.Add(WarableRow(alliance.Id, alliance.Name, AllianceTypeId, permit));
        }

        return new PyList(rows);
    }

    private static PyValue BuildCharacterRow(OwnerEntry entry) => Py.KeyVal(
        ("characterID", new PyInt(entry.Id)),
        ("characterName", new PyStr(entry.Name)),
        ("ownerID", new PyInt(entry.Id)),
        ("ownerName", new PyStr(entry.Name)),
        ("typeID", new PyInt(entry.TypeId)),
        ("groupID", new PyInt(CharacterGroupId)),
        ("corporationID", new PyInt(entry.CorporationId ?? 0)),
        ("allianceID", new PyInt(entry.AllianceId ?? 0)));

    private static PyValue BuildCorporationRow(OwnerEntry entry) => Py.KeyVal(
        ("corporationID", new PyInt(entry.Id)),
        ("corporationName", new PyStr(entry.Name)),
        ("ownerID", new PyInt(entry.Id)),
        ("ownerName", new PyStr(entry.Name)),
        ("typeID", new PyInt(CorporationTypeId)),
        ("groupID", new PyInt(CorporationGroupId)),
        ("tickerName", entry.TickerName is { } ticker ? new PyStr(ticker) : PyNone.Instance),
        ("factionID", new PyInt(entry.FactionId ?? 0)),
        ("isNPC", PyValue.Of(entry.IsNpc ?? false)));

    private static PyValue BuildFactionRow(OwnerEntry entry) => Py.KeyVal(
        ("factionID", new PyInt(entry.Id)),
        ("factionName", new PyStr(entry.Name)),
        ("ownerID", new PyInt(entry.Id)),
        ("ownerName", new PyStr(entry.Name)),
        ("typeID", new PyInt(FactionTypeId)),
        ("groupID", new PyInt(FactionGroupId)));

    private static PyValue BuildLocationRow(LookupLocation location) => Py.KeyVal(
        ("itemID", new PyInt(location.Id)),
        ("itemName", new PyStr(location.Name ?? string.Empty)),
        ("typeID", new PyInt(location.TypeId)),
        ("groupID", new PyInt(location.GroupId)),
        ("solarSystemID", location.SolarSystemId is > 0 and var systemId ? new PyInt(systemId) : PyNone.Instance),
        ("constellationID", location.ConstellationId is > 0 and var constellationId ? new PyInt(constellationId) : PyNone.Instance),
        ("regionID", location.RegionId is > 0 and var regionId ? new PyInt(regionId) : PyNone.Instance));

    private static PyValue BuildOwnerRow(OwnerEntry entry)
    {
        var fields = new List<(string Key, PyValue Value)>
        {
            ("ownerID", new PyInt(entry.Id)),
            ("ownerName", new PyStr(entry.Name)),
            ("typeID", new PyInt(entry.TypeId)),
            ("groupID", new PyInt(entry.GroupId)),
            ("gender", new PyInt(entry.Gender)),
        };
        if (entry.CharacterId is { } characterId) fields.Add(("characterID", new PyInt(characterId)));
        if (entry.CorporationId is { } corporationId) fields.Add(("corporationID", new PyInt(corporationId)));
        if (entry.AllianceId is { } allianceId) fields.Add(("allianceID", new PyInt(allianceId)));
        if (entry.FactionId is { } factionId) fields.Add(("factionID", new PyInt(factionId)));
        if (entry.IncludeTickerName)
        {
            fields.Add(("tickerName", entry.TickerName is { } ticker ? new PyStr(ticker) : PyNone.Instance));
        }
        if (entry.IsNpc is { } isNpc) fields.Add(("isNPC", PyValue.Of(isNpc)));
        return Py.KeyVal(fields.ToArray());
    }

    private static PyValue WarableRow(long id, string name, int typeId, int permit) => Py.KeyVal(
        ("ownerID", new PyInt(id)),
        ("ownerName", new PyStr(name)),
        ("typeID", new PyInt(typeId)),
        ("warPermit", new PyInt(permit)));

    private static PyList AsList(IEnumerable<PyValue> items) => new(items.ToArray());

    private static IReadOnlyList<OwnerEntry> Filter(IEnumerable<OwnerEntry> entries, string search, int exactMode)
    {
        if (Collapse(search).Length == 0)
        {
            return [];
        }

        return entries
            .Where(entry => Matches(entry.Name, search, exactMode))
            .OrderBy(entry => Rank(entry.Name, search))
            .ThenBy(entry => entry.Name, StringComparer.CurrentCulture)
            .ThenBy(entry => entry.Id)
            .Take(MaxLookupResults)
            .ToArray();
    }

    private static bool Matches(string name, string search, int exactMode)
    {
        var rawTarget = Normalize(name);
        var collapsedTarget = Collapse(rawTarget);
        var rawSearch = Normalize(search);
        var collapsedSearch = Collapse(rawSearch);
        if (collapsedSearch.Length == 0)
        {
            return false;
        }

        if (exactMode is 1 or 2 or 3)
        {
            return rawTarget == rawSearch || collapsedTarget == collapsedSearch;
        }

        var terms = rawSearch.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Collapse)
            .Where(term => term.Length > 0)
            .ToArray();
        return terms.Length == 0
            ? collapsedTarget.Contains(collapsedSearch, StringComparison.Ordinal)
            : terms.All(term => collapsedTarget.Contains(term, StringComparison.Ordinal));
    }

    private static int Rank(string name, string search)
    {
        var rawName = Normalize(name);
        var collapsedName = Collapse(rawName);
        var rawSearch = Normalize(search);
        var collapsedSearch = Collapse(rawSearch);
        if (rawName == rawSearch || collapsedName == collapsedSearch) return 0;
        if (rawName.StartsWith(rawSearch, StringComparison.Ordinal) || collapsedName.StartsWith(collapsedSearch, StringComparison.Ordinal)) return 1;
        return 2;
    }

    private static int ExactMode(PyValue value)
    {
        var numeric = JsSemantics.NumberValue(value);
        if (!double.IsFinite(numeric)) return 0;
        var integral = Math.Truncate(numeric);
        return integral is >= int.MinValue and <= int.MaxValue ? (int)integral : 0;
    }

    private static string SearchString(PyValue value) => JsSemantics.StringValue(value)?.Trim() ?? string.Empty;
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string Collapse(string value)
    {
        var result = new System.Text.StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                result.Append(character);
            }
        }
        return result.ToString();
    }

    private static string NameOr(string? value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;
    private static string? EmptyToNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool JsTruthy(PyValue value) => value switch
    {
        PyNone => false,
        PyBool boolean => boolean.Value,
        PyInt number => number.Value != 0,
        PyReal number => number.Value != 0 && !double.IsNaN(number.Value),
        PyStr text => text.Value.Length > 0,
        _ => true,
    };

    private static int CorporationPermit(LookupCorporation corporation) =>
        corporation.WarPermit is { } permit ? (permit == 0 ? 0 : 1)
        : corporation.AllowWar is { } allowWar ? (allowWar ? 1 : 0)
        : corporation.IsNpc ? 0 : 1;

    private static bool WarableNameMatches(string name, string search, bool exact)
    {
        var loweredName = (name ?? string.Empty).ToLowerInvariant();
        if (exact)
        {
            return loweredName == search || Collapse(loweredName) == Collapse(search);
        }
        return loweredName.Contains(search, StringComparison.Ordinal)
            || Collapse(loweredName).Contains(Collapse(search), StringComparison.Ordinal);
    }

    private sealed record OwnerEntry(
        long Id,
        string Name,
        int TypeId,
        int GroupId,
        int Gender,
        long? CharacterId = null,
        long? CorporationId = null,
        long? AllianceId = null,
        long? FactionId = null,
        string? TickerName = null,
        bool IncludeTickerName = false,
        bool? IsNpc = null);
}
