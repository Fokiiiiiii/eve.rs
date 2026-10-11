using Eve.Marshal;

namespace Eve.Services.Core;

/// <summary>
/// Read-only callbacks for the structure directory. The host owns the persisted data source and
/// returns its client-compatible PyValue without this RPC adapter changing row order or field types.
/// </summary>
/// <summary>
/// Reads owner-scoped structure directory maps from deployed structure records.
/// SDE data may supply metadata for a persisted deployment, but must not create a deployed entry by itself.
/// </summary>
public interface IStructureDirectoryReadCallbacks
{
    ValueTask<PyValue> GetJumpBridgesWithMyAccessAsync(ServiceCall call);

    ValueTask<PyValue> GetNearbyJumpBridgesAsync(ServiceCall call);

    ValueTask<PyValue> GetStructureDescriptionAsync(ServiceCall call);

    /// <summary>Returns deployed structure map rows in the client's CRowset format.</summary>
    ValueTask<PyValue> GetStructureMapDataAsync(long solarSystemId, CancellationToken cancellationToken);

    /// <summary>Returns accessible online cyno beacon rows as six-field lists.</summary>
    ValueTask<PyValue> GetMyAccessibleOnlineCynoBeaconStructuresAsync(ServiceCall call);

    /// <summary>Returns solar-system IDs with online cyno beacons.</summary>
    ValueTask<PyValue> GetSolarSystemsWithBeaconsAsync(CancellationToken cancellationToken);

    /// <summary>Returns valid war-HQ rows only for the authenticated corporation or alliance.</summary>
    ValueTask<PyValue> GetValidWarHQsAsync(ServiceCall call, long ownerId, CancellationToken cancellationToken);

    /// <summary>Updates a persisted structure description after Host ownership and role checks.</summary>
    ValueTask<bool> SetStructureDescriptionAsync(ServiceCall call);

    /// <summary>Returns IDs of persisted, deployed structures with online docking in one system.</summary>
    ValueTask<IReadOnlyList<long>> GetStructureIdsInSolarSystemAsync(
        long solarSystemId,
        CancellationToken cancellationToken);

    /// <summary>Returns info only for an actually deployed structure row in persistent state.</summary>
    ValueTask<PersistedStructureInfo?> GetStructureInfoAsync(long structureId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns exactly the requested deployed structures' profile data for the Docking service.
    /// The Host must resolve the setting through its existing SDE/client mapping (SettingIdForService),
    /// include owner corporation/alliance and every assigned access list, and mark any missing profile,
    /// setting, or access-list data incomplete. A built-in default profile may be represented only by
    /// its explicit authoritative owner-corporation rule. Do not synthesize structures from static type data.
    /// </summary>
    ValueTask<IReadOnlyList<StructureDockingAccessSnapshot>> GetDockingAccessSnapshotsAsync(
        long characterId,
        long corporationId,
        long allianceId,
        IReadOnlyList<long> structureIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns a PyDict keyed by structure ID. Each value is the structure-info map consumed by the client,
    /// including its services collection. Return only deployed rows loaded from persistent state.
    /// </summary>
    ValueTask<PyDict> GetMyCharacterStructuresAsync(long characterId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the same structure-ID keyed map for the authenticated character's current corporation,
    /// containing only deployed rows loaded from persistent state.
    /// </summary>
    ValueTask<PyDict> GetMyCorporationStructuresAsync(
        long characterId,
        long corporationId,
        CancellationToken cancellationToken);
}

/// <summary>An access-list member row normalized from persistent structure-profile state.</summary>
public sealed record StructureAccessListMember(long MemberId, bool IsAllowed);

/// <summary>One access list assigned to the profile's Docking service.</summary>
public sealed record StructureAccessListSnapshot(
    long AccessListId,
    bool IsLoaded,
    IReadOnlyList<StructureAccessListMember>? Members);

/// <summary>
/// Persisted structure plus the Docking profile data needed for access evaluation.
/// All ACLs referenced by the mapped Docking setting must be present; unavailable data fails closed.
/// </summary>
public sealed record StructureDockingAccessSnapshot(
    long StructureId,
    long OwnerCorporationId,
    long OwnerAllianceId,
    bool PersistentlyDeployed,
    bool ProfileResolved,
    bool DockingSettingResolved,
    IReadOnlyList<StructureAccessListSnapshot?>? AccessLists);

/// <summary>A client info payload tied to one deployed structure row in persistent state.</summary>
public sealed record PersistedStructureInfo(long StructureId, bool PersistentlyDeployed, PyValue Info);

/// <summary>Read-only RPC adapter for the client's structureDirectory service.</summary>
public sealed class StructureDirectoryService(IStructureDirectoryReadCallbacks read) : IService
{
    private readonly IStructureDirectoryReadCallbacks _read =
        read ?? throw new ArgumentNullException(nameof(read));

    public string Name => "structureDirectory";

    public bool HasMethod(string method) => method is
        "GetJumpBridgesWithMyAccess" or
        "GetNearbyJumpBridges" or
        "GetStructureDescription" or
        "GetStructureMapData" or
        "GetMyAccessibleOnlineCynoBeaconStructures" or
        "GetSolarSystemsWithBeacons" or
        "GetValidWarHQs" or
        "SetStructureDescription" or
        "GetStructureInfo" or
        "CheckMyDockingAccessToStructures" or
        "GetMyDockableStructures" or
        "GetMyCharacterStructures" or
        "GetMyCorporationStructures";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        switch (call.Method)
        {
            case "GetJumpBridgesWithMyAccess":
                return await _read.GetJumpBridgesWithMyAccessAsync(call);
            case "GetNearbyJumpBridges":
                return await _read.GetNearbyJumpBridgesAsync(call);
            case "GetStructureDescription":
                return await _read.GetStructureDescriptionAsync(call);
            case "GetStructureMapData":
            {
                var solarSystemId = PositiveId(call.Arg(0));
                if (solarSystemId <= 0)
                    solarSystemId = call.Session?.GetInt("solarsystemid2") ??
                        call.Session?.GetInt("solarsystemid") ?? 0;
                return solarSystemId > 0
                    ? await _read.GetStructureMapDataAsync(solarSystemId, call.Cancellation)
                    : new PyList([]);
            }
            case "GetMyAccessibleOnlineCynoBeaconStructures":
                return call.Session?.CharacterId is > 0
                    ? await _read.GetMyAccessibleOnlineCynoBeaconStructuresAsync(call)
                    : new PyList([]);
            case "GetSolarSystemsWithBeacons":
                return await _read.GetSolarSystemsWithBeaconsAsync(call.Cancellation);
            case "GetValidWarHQs":
            {
                var ownerId = PositiveId(call.Arg(0));
                var corporationId = call.Session?.GetInt("corpid") ?? 0;
                var allianceId = call.Session?.GetInt("allianceid") ?? 0;
                return ownerId > 0 && (ownerId == corporationId || ownerId == allianceId)
                    ? await _read.GetValidWarHQsAsync(call, ownerId, call.Cancellation)
                    : new PyList([]);
            }
            case "SetStructureDescription":
            {
                var structureId = PositiveId(call.Arg(0));
                if (structureId <= 0 || !await _read.SetStructureDescriptionAsync(call))
                    throw StructureDescriptionDenied();
                return PyNone.Instance;
            }
            case "GetStructureInfo":
            {
                var structureId = PositiveId(call.Arg(0));
                if (structureId <= 0)
                    return PyNone.Instance;
                var structure = await _read.GetStructureInfoAsync(structureId, call.Cancellation);
                return structure is { PersistentlyDeployed: true } &&
                    structure.StructureId == structureId && structure.Info is not PyNone
                    ? structure.Info
                    : PyNone.Instance;
            }
            case "CheckMyDockingAccessToStructures":
            {
                var characterId = call.Session?.CharacterId ?? 0;
                var corporationId = call.Session?.GetInt("corpid") ?? 0;
                var allianceId = call.Session?.GetInt("allianceid") ?? 0;
                var requested = PositiveIds(call.Arg(0));
                if (characterId <= 0 || requested.Count == 0)
                    return new PyList([]);

                var snapshots = await _read.GetDockingAccessSnapshotsAsync(
                    characterId, corporationId, allianceId, requested, call.Cancellation);
                return DockableStructures(requested, snapshots, characterId, corporationId, allianceId);
            }
            case "GetMyDockableStructures":
            {
                var characterId = call.Session?.CharacterId ?? 0;
                var corporationId = call.Session?.GetInt("corpid") ?? 0;
                var allianceId = call.Session?.GetInt("allianceid") ?? 0;
                var solarSystemId = PositiveId(call.Arg(0));
                if (solarSystemId <= 0)
                    solarSystemId = call.Session?.GetInt("solarsystemid2") ??
                        call.Session?.GetInt("solarsystemid") ?? 0;
                if (characterId <= 0 || solarSystemId <= 0)
                    return new PyList([]);

                var structureIds = await _read.GetStructureIdsInSolarSystemAsync(
                    solarSystemId, call.Cancellation);
                var requested = structureIds.Where(id => id > 0).Distinct().ToArray();
                if (requested.Length == 0)
                    return new PyList([]);

                var snapshots = await _read.GetDockingAccessSnapshotsAsync(
                    characterId, corporationId, allianceId, requested, call.Cancellation);
                return DockableStructures(requested, snapshots, characterId, corporationId, allianceId);
            }
            case "GetMyCharacterStructures":
            {
                var characterId = call.Session?.CharacterId ?? 0;
                return characterId > 0
                    ? await _read.GetMyCharacterStructuresAsync(characterId, call.Cancellation)
                    : new PyDict([]);
            }
            case "GetMyCorporationStructures":
            {
                var characterId = call.Session?.CharacterId ?? 0;
                var corporationId = call.Session?.GetInt("corpid") ?? 0;
                return characterId > 0 && corporationId > 0
                    ? await _read.GetMyCorporationStructuresAsync(characterId, corporationId, call.Cancellation)
                    : new PyDict([]);
            }
            default:
                return PyNone.Instance;
        }
    }

    private static PyList DockableStructures(
        IReadOnlyList<long> requested,
        IReadOnlyList<StructureDockingAccessSnapshot> snapshots,
        long characterId,
        long corporationId,
        long allianceId)
    {
        var requestedSet = new HashSet<long>(requested);
        var byId = new Dictionary<long, StructureDockingAccessSnapshot>();
        var ambiguous = new HashSet<long>();
        foreach (var snapshot in snapshots)
        {
            if (!requestedSet.Contains(snapshot.StructureId))
                continue;
            if (!byId.TryAdd(snapshot.StructureId, snapshot))
                ambiguous.Add(snapshot.StructureId);
        }

        return new PyList(requested
            .Where(id => !ambiguous.Contains(id) &&
                byId.TryGetValue(id, out var snapshot) &&
                CanDock(snapshot, id, characterId, corporationId, allianceId))
            .Select(id => (PyValue)new PyInt(id))
            .ToArray());
    }

    private static Eve.Services.MachoWrappedException StructureDescriptionDenied() =>
        Eve.Services.MachoErrors.ThrowUserError(
            "CrpAccessDenied",
            ("reason", new PyList([
                new PyInt(101),
                new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"),
            ])));

    private static long PositiveId(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };

    private static IReadOnlyList<long> PositiveIds(PyValue? value)
    {
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        var seen = new HashSet<long>();
        var result = new List<long>(values.Count);
        foreach (var item in values)
        {
            var id = PositiveId(item);
            if (id > 0 && seen.Add(id))
                result.Add(id);
        }
        return result;
    }

    public static bool CanDock(
        StructureDockingAccessSnapshot structure,
        long requestedStructureId,
        long characterId,
        long corporationId,
        long allianceId)
    {
        if (structure.StructureId != requestedStructureId ||
            !structure.PersistentlyDeployed ||
            structure.OwnerCorporationId <= 0 ||
            structure.OwnerAllianceId < 0 ||
            !structure.ProfileResolved ||
            !structure.DockingSettingResolved ||
            structure.AccessLists is not { Count: > 0 } lists ||
            lists.Any(list => list is null || !list.IsLoaded || list.Members is null))
            return false;

        var seenAccessLists = new HashSet<long>();
        foreach (var list in lists)
        {
            if (list is null || !seenAccessLists.Add(list.AccessListId))
                return false;
        }

        foreach (var list in lists)
        {
            if (list is not null && IsAllowedInAccessList(
                    list, characterId, corporationId, allianceId))
                return true;
        }
        return false;
    }

    private static bool IsAllowedInAccessList(
        StructureAccessListSnapshot list,
        long characterId,
        long corporationId,
        long allianceId)
    {
        var members = list.Members!;
        var specificityOrder = new List<long>(4);
        if (characterId > 0)
            specificityOrder.Add(characterId);
        if (corporationId > 0)
            specificityOrder.Add(corporationId);
        if (allianceId > 0)
            specificityOrder.Add(allianceId);
        specificityOrder.Add(0); // Explicit Everyone member in this access list.

        foreach (var memberId in specificityOrder)
        {
            var matches = members.Where(member => member.MemberId == memberId).ToArray();
            if (matches.Length > 1)
                return false;
            if (matches.Length == 1)
                return matches[0].IsAllowed;
        }
        return false;
    }
}


public sealed record StructureGuestSnapshot(
    long CharacterId,
    long CorporationId,
    long AllianceId,
    long WarFactionId);

/// <summary>Adapter for the client's structureGuests.GetGuests snapshot RPC.</summary>
public sealed class StructureGuestsService(
    Func<long, CancellationToken, ValueTask<IReadOnlyList<StructureGuestSnapshot>>> getGuests,
    Action<Eve.Services.ClientSession, long, IReadOnlySet<long>>? seedObserverGuestLedger = null) : IService
{
    private readonly Func<long, CancellationToken, ValueTask<IReadOnlyList<StructureGuestSnapshot>>> _getGuests =
        getGuests ?? throw new ArgumentNullException(nameof(getGuests));
    private readonly Action<Eve.Services.ClientSession, long, IReadOnlySet<long>>? _seedObserverGuestLedger =
        seedObserverGuestLedger;

    public string Name => "structureGuests";

    public bool HasMethod(string method) => method == "GetGuests";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var structureId = Macho.JsSemantics.ToPositiveInteger(call.Arg(0));
        if (structureId <= 0)
            structureId = call.Session?.GetInt("structureid") ?? 0;
        if (structureId <= 0)
            return new PyDict([]);

        var guests = await _getGuests(structureId, call.Cancellation);
        if (call.Session is { } observer)
            _seedObserverGuestLedger?.Invoke(
                observer,
                structureId,
                guests.Where(guest => guest.CharacterId > 0).Select(guest => guest.CharacterId).ToHashSet());
        var entries = guests
            .Where(guest => guest.CharacterId > 0)
            .GroupBy(guest => guest.CharacterId)
            .Select(group => group.First())
            .OrderBy(guest => guest.CharacterId)
            .Select(guest => new KeyValuePair<PyValue, PyValue>(
                new PyInt(guest.CharacterId),
                new PyList([
                    new PyInt(guest.CorporationId),
                    new PyInt(guest.AllianceId),
                    new PyInt(guest.WarFactionId),
                ])))
            .ToArray();
        return new PyDict(entries);
    }
}

/// <summary>
/// Read callbacks for the client's structureSettings RPCs.
/// Implementations must read deployed structure state (not SDE type rows) and enforce each structure's
/// owner corporation/alliance and persisted access-profile/access-group rules for the supplied character.
/// CharacterGetServices returns a service-ID keyed map; absent or inaccessible structures return an empty map,
/// and the two Has calls return false.
/// </summary>
public interface IStructureSettingsReadCallbacks
{
    ValueTask<PyDict> CharacterGetServicesAsync(long characterId, long structureId, CancellationToken cancellationToken);

    ValueTask<PyValue> CharacterGetServiceAsync(
        long characterId, long structureId, long serviceId, CancellationToken cancellationToken);

    ValueTask<bool> CharacterHasServiceAsync(long characterId, long structureId, long serviceId, CancellationToken cancellationToken);

    ValueTask<bool> CharacterHasSettingAsync(long characterId, long structureId, long settingId, CancellationToken cancellationToken);
}

/// <summary>Read-only adapter for the client's structureSettings service.</summary>
public sealed class StructureSettingsService(IStructureSettingsReadCallbacks read) : IService
{
    private readonly IStructureSettingsReadCallbacks _read =
        read ?? throw new ArgumentNullException(nameof(read));

    public string Name => "structureSettings";

    public bool HasMethod(string method) => method is
        "CharacterGetServices" or
        "CharacterHasService" or
        "CharacterHasSetting" or
        "CharacterGetService" or
        "CharacterCheckService";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        var characterId = call.Session?.CharacterId ?? 0;
        var structureId = Integer(call.Arg(0));
        if (call.Method == "CharacterCheckService")
        {
            var serviceId = Integer(call.Arg(1));
            if (characterId <= 0 || structureId <= 0 || serviceId <= 0 ||
                !await _read.CharacterHasServiceAsync(characterId, structureId, serviceId, call.Cancellation))
                throw ServiceUnavailable(serviceId);
            return PyBool.True;
        }

        if (call.Method == "CharacterGetService")
        {
            var serviceId = Integer(call.Arg(1));
            return characterId > 0 && structureId > 0 && serviceId > 0
                ? await _read.CharacterGetServiceAsync(characterId, structureId, serviceId, call.Cancellation)
                : PyNone.Instance;
        }

        if (characterId <= 0 || structureId <= 0)
            return call.Method == "CharacterGetServices" ? new PyDict([]) : PyBool.False;

        if (call.Method == "CharacterGetServices")
            return await _read.CharacterGetServicesAsync(characterId, structureId, call.Cancellation);

        if (call.Method == "CharacterHasService")
        {
            var serviceId = Integer(call.Arg(1));
            return serviceId == 0
                ? PyBool.False
                : await _read.CharacterHasServiceAsync(characterId, structureId, serviceId, call.Cancellation)
                    ? PyBool.True
                    : PyBool.False;
        }

        var settingId = Macho.JsSemantics.ToPositiveInteger(call.Arg(1));
        return settingId <= 0
            ? PyBool.False
            : await _read.CharacterHasSettingAsync(characterId, structureId, settingId, call.Cancellation)
                ? PyBool.True
                : PyBool.False;
    }

    private static Eve.Services.MachoWrappedException ServiceUnavailable(long serviceId)
    {
        var error = serviceId switch
        {
            5 => "StructureMarketDenied",
            1 => "StructureDockingDenied",
            15 => "ActivateJumpBridgeDenied",
            16 => "ConnectToCynoBeaconDenied",
            _ => "StructureGenericSettingDenied",
        };
        return Eve.Services.MachoErrors.ThrowUserError(error);
    }

    private static long Integer(PyValue? value)
    {
        var number = Macho.JsSemantics.ToNumber(value);
        return double.IsFinite(number) && Math.Truncate(number) == number &&
            Math.Abs(number) <= 9_007_199_254_740_991.0
                ? (long)number
                : 0;
    }
}

public interface IStructureAssetSafetyReadCallbacks
{
    /// <summary>
    /// Returns only persisted personal wraps owned by this character, with the exact client keys:
    /// assetWrapID, solarSystemID, wrapName, ejectTime, daysUntilCanDeliverConst (5),
    /// daysUntilAutoMoveConst (20), and nearestNPCStationInfo.
    /// </summary>
    ValueTask<PyList> GetItemsInSafetyForCharacterAsync(
        long characterId,
        CancellationToken cancellationToken);

    /// <summary>Returns persisted corporation wraps after checking the caller's CEO/Director access.</summary>
    ValueTask<PyList> GetItemsInSafetyForCorpAsync(
        long characterId,
        long corporationId,
        CancellationToken cancellationToken);

    /// <summary>Returns names only for persisted wraps this caller may inspect.</summary>
    ValueTask<PyDict> GetWrapNamesAsync(
        long characterId,
        long corporationId,
        IReadOnlyList<long> wrapIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns (eligible same-system NPC stations/outposts/Upwell structures, nearest NPC station info).
    /// Use persisted wraps and destination records; manual delivery is available only after five days.
    /// </summary>
    ValueTask<PyTuple> GetStructuresICanDeliverToAsync(
        long characterId,
        long corporationId,
        long solarSystemId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves all eligible personal assets from a persistent standard Upwell structure into a
    /// safety wrap, or ejects them in Wormhole/Pochven. Return PersonalWrapCreated or ItemsEjected only
    /// after durable completion; validate source/system, control state, items, and non-dock-dependent rights.
    /// </summary>
    ValueTask<AssetSafetyActionState> MovePersonalAssetsToSafetyAsync(
        ServiceCall call,
        long solarSystemId,
        long structureId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically moves corporation assets or ejects them in Wormhole/Pochven. Re-read corporation
    /// membership/roles and require CEO or Director in the same transaction as the move.
    /// </summary>
    ValueTask<AssetSafetyActionState> MoveCorpAssetsToSafetyAsync(
        ServiceCall call,
        long solarSystemId,
        long structureId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Atomically charges the correct fee and delivers this owner's persisted wrap to a valid same-system
    /// NPC station/outpost/Upwell destination after day five; mark it delivered and notify the owner.
    /// Revalidate wrap ownership, timer, source system, destination type and state in the transaction.
    /// </summary>
    ValueTask<AssetSafetyActionState> MoveSafetyWrapToStructureAsync(
        ServiceCall call,
        long assetWrapId,
        long solarSystemId,
        long destinationId,
        CancellationToken cancellationToken);

    /// <summary>Moves a persisted wrap's eject timer to the requested GM day offset (5 or 30).</summary>
    ValueTask<bool> MoveEjectTimeGMAsync(
        ServiceCall call,
        long assetWrapId,
        int days,
        CancellationToken cancellationToken);

    /// <summary>
    /// Idempotently auto-delivers every due wrap at day twenty using CCP's security-space/nearest-NPC
    /// station rules and fees. Run from the Host's durable scheduler and before service reads.
    /// </summary>
    ValueTask ProcessDueAutomaticDeliveriesAsync(CancellationToken cancellationToken);
}

/// <summary>Authoritative outcome of a persisted Asset Safety transition.</summary>
public enum AssetSafetyActionState
{
    NotCompleted,
    PersonalWrapCreated,
    CorporationWrapCreated,
    ItemsEjected,
    WrapDelivered,
    NoItemsToMove,
    NotAuthorized,
    StructureUnavailable,
    SolarSystemMismatch,
    AssetSafetyUnavailable,
    DeliveryNotReady,
    InvalidDestination,
    DestinationAccessDenied,
    PersistenceFailure,
}

public sealed class StructureAssetSafetyService(IStructureAssetSafetyReadCallbacks read) : IService
{
    private readonly IStructureAssetSafetyReadCallbacks _read =
        read ?? throw new ArgumentNullException(nameof(read));

    public string Name => "structureAssetSafety";

    public bool HasMethod(string method) => method is
        "GetItemsInSafetyForCharacter" or
        "GetItemsInSafetyForCorp" or
        "MovePersonalAssetsToSafety" or
        "MoveCorpAssetsToSafety" or
        "GetWrapNames" or
        "GetStructuresICanDeliverTo" or
        "MoveSafetyWrapToStructure" or
        "MoveEjectTimeGM";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        var characterId = call.Session?.CharacterId ?? 0;
        var corporationId = call.Session?.GetInt("corpid") ?? 0;
        switch (call.Method)
        {
            case "GetItemsInSafetyForCharacter":
                if (characterId <= 0)
                    return new PyList([]);
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                return await _read.GetItemsInSafetyForCharacterAsync(characterId, call.Cancellation);

            case "GetItemsInSafetyForCorp":
                if (characterId <= 0 || corporationId <= 0)
                    return new PyList([]);
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                return await _read.GetItemsInSafetyForCorpAsync(characterId, corporationId, call.Cancellation);

            case "GetWrapNames":
            {
                if (characterId <= 0)
                    return new PyDict([]);
                var wrapIds = PositiveIds(call.Arg(0));
                if (wrapIds.Count == 0)
                    return new PyDict([]);
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                return await _read.GetWrapNamesAsync(characterId, corporationId, wrapIds, call.Cancellation);
            }

            case "GetStructuresICanDeliverTo":
            {
                var solarSystemId = PositiveId(call.Arg(0));
                if (characterId <= 0 || solarSystemId <= 0)
                    return new PyTuple([new PyList([]), PyNone.Instance]);
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                var targets = await _read.GetStructuresICanDeliverToAsync(
                    characterId, corporationId, solarSystemId, call.Cancellation);
                if (targets.Items.Count != 2)
                    throw new InvalidOperationException("ASSET_SAFETY_DELIVERY_TARGETS_INVALID");
                return targets;
            }

            case "MovePersonalAssetsToSafety":
            {
                var solarSystemId = PositiveId(call.Arg(0));
                var structureId = PositiveId(call.Arg(1));
                RequireAssetSafetyRequest(characterId, solarSystemId, structureId);
                if (call.Session?.GetInt("stationid") == structureId)
                    throw Eve.Services.MachoErrors.ThrowUserError(
                        "CustomNotify",
                        ("notify", new PyWString(
                            "You cannot move personal assets to Asset Safety while docked at the source structure.")));
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                var result = await _read.MovePersonalAssetsToSafetyAsync(
                    call, solarSystemId, structureId, call.Cancellation);
                RequireSuccessfulAction(result, AssetSafetyActionState.PersonalWrapCreated, allowEjection: true);
                return PyNone.Instance;
            }

            case "MoveCorpAssetsToSafety":
            {
                var solarSystemId = PositiveId(call.Arg(0));
                var structureId = PositiveId(call.Arg(1));
                RequireAssetSafetyRequest(characterId, corporationId, solarSystemId, structureId);
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                var result = await _read.MoveCorpAssetsToSafetyAsync(
                    call, solarSystemId, structureId, call.Cancellation);
                RequireSuccessfulAction(result, AssetSafetyActionState.CorporationWrapCreated, allowEjection: true);
                return PyNone.Instance;
            }

            case "MoveSafetyWrapToStructure":
            {
                var assetWrapId = PositiveId(call.Arg(0));
                var solarSystemId = PositiveId(call.Arg(1));
                var destinationId = PositiveId(call.Kwarg("destinationID"));
                if (characterId <= 0 || assetWrapId <= 0 || solarSystemId <= 0 || destinationId <= 0)
                    throw Eve.Services.MachoErrors.ThrowUserError(
                        "CustomNotify", ("notify", new PyWString("Invalid Asset Safety delivery request.")));
                await _read.ProcessDueAutomaticDeliveriesAsync(call.Cancellation);
                var result = await _read.MoveSafetyWrapToStructureAsync(
                    call, assetWrapId, solarSystemId, destinationId, call.Cancellation);
                RequireSuccessfulAction(result, AssetSafetyActionState.WrapDelivered);
                return PyNone.Instance;
            }

            case "MoveEjectTimeGM":
            {
                var assetWrapId = PositiveId(call.Arg(0));
                var days = PositiveId(call.Arg(1));
                var role = call.Session?.GetInt("role") ?? 0;
                if ((role & (1L << 54)) == 0 || assetWrapId <= 0 || days is not 5 and not 30)
                    throw MoveEjectTimeDenied();
                if (!await _read.MoveEjectTimeGMAsync(call, assetWrapId, (int)days, call.Cancellation))
                    throw MoveEjectTimeDenied();
                return PyNone.Instance;
            }

            default:
                return PyNone.Instance;
        }
    }

    private static Eve.Services.MachoWrappedException MoveEjectTimeDenied() =>
        Eve.Services.MachoErrors.ThrowUserError(
            "CustomInfo", ("info", new PyWString("This Asset Safety timer cannot be changed.")));

    private static long PositiveId(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };

    private static IReadOnlyList<long> PositiveIds(PyValue? value)
    {
        var values = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => Array.Empty<PyValue>(),
        };
        var seen = new HashSet<long>();
        var result = new List<long>(values.Count);
        foreach (var item in values)
        {
            var id = PositiveId(item);
            if (id > 0 && seen.Add(id))
                result.Add(id);
        }
        return result;
    }

    private static void RequireAssetSafetyRequest(long characterId, long solarSystemId, long structureId)
    {
        if (characterId <= 0 || solarSystemId <= 0 || structureId <= 0)
            throw Eve.Services.MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyWString("Invalid Asset Safety request.")));
    }

    private static void RequireAssetSafetyRequest(
        long characterId, long corporationId, long solarSystemId, long structureId)
    {
        if (characterId <= 0 || corporationId <= 0 || solarSystemId <= 0 || structureId <= 0)
            throw Eve.Services.MachoErrors.ThrowUserError(
                "CustomNotify", ("notify", new PyWString("Invalid corporation Asset Safety request.")));
    }

    private static void RequireSuccessfulAction(
        AssetSafetyActionState state,
        AssetSafetyActionState committedState,
        bool allowEjection = false)
    {
        if (state == committedState ||
            (allowEjection && state == AssetSafetyActionState.ItemsEjected))
            return;
        if (state == AssetSafetyActionState.NoItemsToMove)
            throw Eve.Services.MachoErrors.ThrowUserError("NoItemsToPutInAssetSafety");

        var message = state switch
        {
            AssetSafetyActionState.NotAuthorized => "You are not authorized to access these assets.",
            AssetSafetyActionState.StructureUnavailable => "The Upwell structure is not available.",
            AssetSafetyActionState.SolarSystemMismatch => "The structure is not in the requested solar system.",
            AssetSafetyActionState.AssetSafetyUnavailable => "Asset Safety is unavailable in this solar system.",
            AssetSafetyActionState.DeliveryNotReady => "This Asset Safety wrap is not ready for delivery.",
            AssetSafetyActionState.InvalidDestination => "The delivery destination is not valid.",
            AssetSafetyActionState.DestinationAccessDenied => "You cannot deliver to that destination.",
            _ => "The Asset Safety operation was not committed.",
        };
        throw Eve.Services.MachoErrors.ThrowUserError(
            "CustomNotify", ("notify", new PyWString(message)));
    }
}
