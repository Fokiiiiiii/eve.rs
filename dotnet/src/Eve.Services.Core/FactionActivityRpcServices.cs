using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record RwManagerRpcCallbacks(
    Func<long, CancellationToken, ValueTask<IReadOnlyDictionary<long, long>>>? ReadClosestStationsAsync = null,
    Func<long, CancellationToken, ValueTask<bool>>? HasInstancesAsync = null);

public sealed class RWManagerService(RwManagerRpcCallbacks? callbacks = null) : IService
{
    private static readonly long[] ResourceWarCorporations = [1000283, 1000284, 1000285, 1000286];

    public string Name => "RWManager";

    public bool HasMethod(string method) => method is "get_closest_rw_stations" or "solarsystem_contains_rw_instances";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        var systemId = RequestedSystemId(call);
        switch (call.Method)
        {
            case "get_closest_rw_stations":
            {
                var stations = callbacks?.ReadClosestStationsAsync is { } read
                    ? await read(systemId, call.Cancellation)
                    : null;
                var entries = (stations ?? new Dictionary<long, long>())
                    .Where(entry => ResourceWarCorporations.Contains(entry.Key) && entry.Key > 0 && entry.Value > 0)
                    .OrderBy(entry => entry.Key)
                    .Select(entry => new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), new PyInt(entry.Value)))
                    .ToArray();
                return new PyDict(entries);
            }
            case "solarsystem_contains_rw_instances":
                return callbacks?.HasInstancesAsync is { } hasInstances
                    ? (await hasInstances(systemId, call.Cancellation) ? PyBool.True : PyBool.False)
                    : PyBool.False;
            default:
                return PyNone.Instance;
        }
    }

    private static long RequestedSystemId(ServiceCall call) =>
        PositiveInteger(call.Arg(0)) is var requested && requested > 0
            ? requested
            : call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;

    internal static long PositiveInteger(PyValue? value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 && number.Value <= long.MaxValue && Math.Truncate(number.Value) == number.Value => (long)number.Value,
        _ => 0,
    };
}

public sealed record FwCharacterWarfareState(long? DirectFactionId, long DirectEnlistmentCooldownFileTime);

public sealed record FwCharacterEnlistmentRpcCallbacks(
    Func<long, CancellationToken, ValueTask<FwCharacterWarfareState?>>? ReadCharacterStateAsync = null,
    Func<long, CancellationToken, ValueTask<long?>>? ReadCorporationFactionIdAsync = null,
    Func<long, CancellationToken, ValueTask<IReadOnlyList<long>>>? ReadAllowedFactionsAsync = null,
    Func<long, long, IReadOnlyList<long>, CancellationToken, ValueTask<bool>>? SetAllowedFactionsAsync = null,
    Func<long, long?, long, CancellationToken, ValueTask<bool>>? SaveCharacterStateAsync = null,
    Func<long>? CurrentFileTime = null);

public sealed class FwCharacterEnlistmentMgrService(FwCharacterEnlistmentRpcCallbacks? callbacks = null) : IService
{
    private readonly Dictionary<long, FwCharacterWarfareState> _characters = [];
    private readonly Dictionary<long, IReadOnlyList<long>> _allowedFactions = [];
    private readonly object _gate = new();

    public string Name => "fwCharacterEnlistmentMgr";

    public bool HasMethod(string method) => method is
        "CreateMyDirectEnlistment" or "GetCorpAllowedEnlistmentFactions" or
        "GetMyDirectEnlistmentCooldownTimestamp" or "GetMyEnlistment" or
        "RemoveMyDirectEnlistment" or "SetMyCorpAllowedEnlistmentFactions";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        switch (call.Method)
        {
            case "GetMyEnlistment":
            {
                var state = await ReadCharacterStateAsync(CharacterId(call.Session).GetValueOrDefault(), call.Cancellation);
                var directFactionId = Positive(state?.DirectFactionId ?? 0);
                var corporationFactionId = Positive(await ReadCorporationFactionIdAsync(CorporationId(call.Session), call.Cancellation) ?? 0);
                return new PyList(
                [
                    Number(directFactionId ?? corporationFactionId),
                    Number(directFactionId),
                    Number(corporationFactionId),
                ]);
            }
            case "GetCorpAllowedEnlistmentFactions":
            {
                var corporationId = RWManagerService.PositiveInteger(call.Arg(0));
                var factions = await ReadAllowedFactionsAsync(corporationId, call.Cancellation);
                return ToList(factions);
            }
            case "SetMyCorpAllowedEnlistmentFactions":
            {
                var characterId = CharacterId(call.Session).GetValueOrDefault();
                var corporationId = CorporationId(call.Session);
                if (characterId <= 0 || corporationId <= 0)
                    return PyNone.Instance;
                var factions = ReadFactionIds(call.Arg(0));
                var saved = callbacks?.SetAllowedFactionsAsync is { } save
                    ? await save(characterId, corporationId, factions, call.Cancellation)
                    : SetFallbackAllowedFactions(corporationId, factions);
                return saved ? ToList(factions) : PyNone.Instance;
            }
            case "GetMyDirectEnlistmentCooldownTimestamp":
            {
                var state = await ReadCharacterStateAsync(CharacterId(call.Session).GetValueOrDefault(), call.Cancellation);
                return new PyLong(Math.Max(0, state?.DirectEnlistmentCooldownFileTime ?? 0));
            }
            case "CreateMyDirectEnlistment":
            {
                var factionId = RWManagerService.PositiveInteger(call.Arg(0));
                var characterId = CharacterId(call.Session).GetValueOrDefault();
                if (characterId <= 0 || factionId <= 0)
                    return PyNone.Instance;
                if (await SaveCharacterStateAsync(characterId, factionId, 0, call.Cancellation))
                    call.Session.Change(("warfactionid", new PyInt(factionId)));
                return PyNone.Instance;
            }
            case "RemoveMyDirectEnlistment":
            {
                var characterId = CharacterId(call.Session).GetValueOrDefault();
                if (characterId <= 0)
                    return PyNone.Instance;
                var corporationFactionId = await ReadCorporationFactionIdAsync(CorporationId(call.Session), call.Cancellation);
                var cooldown = checked((callbacks?.CurrentFileTime?.Invoke() ?? DateTimeOffset.UtcNow.ToFileTime()) + TimeSpan.TicksPerDay);
                if (await SaveCharacterStateAsync(characterId, null, cooldown, call.Cancellation))
                    call.Session.Change(("warfactionid", Number(Positive(corporationFactionId ?? 0))));
                return PyNone.Instance;
            }
            default:
                return PyNone.Instance;
        }
    }

    private async ValueTask<FwCharacterWarfareState?> ReadCharacterStateAsync(long characterId, CancellationToken cancellationToken)
    {
        if (characterId <= 0)
            return null;
        if (callbacks?.ReadCharacterStateAsync is { } read)
            return await read(characterId, cancellationToken);
        lock (_gate)
            return _characters.GetValueOrDefault(characterId, new FwCharacterWarfareState(null, 0));
    }

    private async ValueTask<long?> ReadCorporationFactionIdAsync(long corporationId, CancellationToken cancellationToken) =>
        corporationId > 0 && callbacks?.ReadCorporationFactionIdAsync is { } read
            ? await read(corporationId, cancellationToken)
            : null;

    private async ValueTask<bool> SaveCharacterStateAsync(long characterId, long? factionId, long cooldown, CancellationToken cancellationToken)
    {
        if (callbacks?.SaveCharacterStateAsync is { } save)
        {
            var saved = await save(characterId, factionId, cooldown, cancellationToken);
            if (saved)
                lock (_gate)
                    _characters[characterId] = new FwCharacterWarfareState(factionId, cooldown);
            return saved;
        }
        lock (_gate)
            _characters[characterId] = new FwCharacterWarfareState(factionId, cooldown);
        return true;
    }

    private IReadOnlyList<long> GetFallbackAllowedFactions(long corporationId)
    {
        lock (_gate)
            return _allowedFactions.GetValueOrDefault(corporationId, []);
    }

    private bool SetFallbackAllowedFactions(long corporationId, IReadOnlyList<long> factions)
    {
        lock (_gate)
            _allowedFactions[corporationId] = factions.ToArray();
        return true;
    }

    private async ValueTask<IReadOnlyList<long>> ReadAllowedFactionsAsync(long corporationId, CancellationToken cancellationToken) =>
        callbacks?.ReadAllowedFactionsAsync is { } read
            ? await read(corporationId, cancellationToken)
            : GetFallbackAllowedFactions(corporationId);

    private static IReadOnlyList<long> ReadFactionIds(PyValue? value) => value switch
    {
        PyList list => list.Items.Select(RWManagerRpcServiceId).Where(id => id is not null).Select(id => id!.Value).ToArray(),
        PyTuple tuple => tuple.Items.Select(RWManagerRpcServiceId).Where(id => id is not null).Select(id => id!.Value).ToArray(),
        _ => [],
    };

    private static long? RWManagerRpcServiceId(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value >= long.MinValue && number.Value <= long.MaxValue && Math.Truncate(number.Value) == number.Value => (long)number.Value,
        _ => null,
    };

    private static PyList ToList(IEnumerable<long> values) => new([.. values.Select(value => (PyValue)new PyInt(value))]);

    private static long? CharacterId(ClientSession session) =>
        session.GetInt("charid") ?? session.GetInt("characterID");

    private static long CorporationId(ClientSession session) =>
        session.GetInt("corpid") ?? session.GetInt("corporationID") ?? 0;

    private static long? Positive(long value) => value > 0 ? value : null;

    private static PyValue Number(long? value) => value is { } number ? new PyInt(number) : PyNone.Instance;
}

public sealed record FwVictoryPointState(double Score, double Threshold = 75000);

public sealed record FwVictoryPointRpcCallbacks(
    Func<CancellationToken, ValueTask<IReadOnlyDictionary<long, FwVictoryPointState>>>? ReadStateBySolarSystemAsync = null);

public sealed class FwVictoryPointMgrService(FwVictoryPointRpcCallbacks? callbacks = null) : IService
{
    private const string VictoryPointStateClass = "factionwarfare.victoryPointState.VictoryPointState";
    private const double DefaultThreshold = 75000;

    public string Name => "fwVictoryPointMgr";

    public bool HasMethod(string method) => method is "GetAllVictoryPointStates" or "GetLocalVictoryPointState";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        var states = callbacks?.ReadStateBySolarSystemAsync is { } read
            ? await read(call.Cancellation)
            : new Dictionary<long, FwVictoryPointState>();
        var normalized = states
            .Where(entry => entry.Key > 0)
            .OrderBy(entry => entry.Key)
            .ToDictionary(entry => entry.Key, entry => Normalize(entry.Value));

        if (call.Method == "GetAllVictoryPointStates")
            return new PyDict(normalized.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), StatePayload(entry.Value))).ToArray());

        if (call.Method == "GetLocalVictoryPointState")
        {
            var systemId = RWManagerService.PositiveInteger(call.Arg(0));
            if (systemId <= 0)
                systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
            return new PyList([new PyInt(systemId), normalized.TryGetValue(systemId, out var state) ? StatePayload(state) : PyNone.Instance]);
        }

        return PyNone.Instance;
    }

    private static FwVictoryPointState Normalize(FwVictoryPointState state) => new(
        double.IsFinite(state.Score) ? Math.Max(0, state.Score) : 0,
        double.IsFinite(state.Threshold) ? Math.Max(1, state.Threshold) : DefaultThreshold);

    public static PyValue StatePayload(FwVictoryPointState state) => new PyObjectEx(
        ObjectExKind.Ex1,
        new PyTuple([new PyToken(VictoryPointStateClass), new PyList([Number(state.Score), Number(state.Threshold)])]),
        [],
        []);

    private static PyValue Number(double value) =>
        value <= long.MaxValue && value >= long.MinValue && Math.Truncate(value) == value
            ? new PyInt((long)value)
            : new PyReal(value);
}

public sealed record InsurgencyCampaignSnapshot(
    long AntipiratePointsScored,
    long StateExpiryTimestamp,
    long PirateFactionId,
    long WarzoneId,
    IReadOnlyList<long> CoveredSolarSystemIds,
    long CampaignId,
    long PiratePointsRequired,
    long PiratePointsScored,
    long StructureId,
    int FsmState,
    long OriginSolarSystemId,
    long AntipiratePointsRequired);

public sealed record InsurgencySolarsystemRpcCallbacks(
    Func<long?, CancellationToken, ValueTask<IReadOnlyList<InsurgencyCampaignSnapshot>>>? ReadCampaignSnapshotsAsync = null);

public sealed class InsurgencySolarsystemService(InsurgencySolarsystemRpcCallbacks? callbacks = null) : IService
{
    private const string CampaignSnapshotClass = "pirateinsurgency.campaignClientSnapshot.CampaignClientSnapshot";

    public string Name => "insurgencySolarsystem";

    public bool HasMethod(string method) => method is "GetAllVisibleCampaigns" or "GetLocalCampaignClientSnapshot";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (call.Method == "GetAllVisibleCampaigns")
        {
            var snapshots = callbacks?.ReadCampaignSnapshotsAsync is { } read
                ? await read(null, call.Cancellation)
                : [];
            return new PyList([.. snapshots.Select(CampaignPayload)]);
        }

        if (call.Method == "GetLocalCampaignClientSnapshot")
        {
            var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
            var snapshots = callbacks?.ReadCampaignSnapshotsAsync is { } read
                ? await read(systemId > 0 ? systemId : null, call.Cancellation)
                : [];
            var snapshot = systemId > 0
                ? snapshots.FirstOrDefault(candidate => candidate.OriginSolarSystemId == systemId || candidate.CoveredSolarSystemIds.Contains(systemId))
                : null;
            return new PyList([new PyInt(systemId), snapshot is null ? PyNone.Instance : CampaignPayload(snapshot)]);
        }

        return PyNone.Instance;
    }

    /// <summary>The client's CampaignClientSnapshot object for a campaign (the payload of a campaign notice too).</summary>
    public static PyValue CampaignPayload(InsurgencyCampaignSnapshot campaign) => new PyObjectEx(
        ObjectExKind.Ex2,
        new PyTuple(
        [
            new PyTuple([new PyToken(CampaignSnapshotClass)]),
            new PyDict(
            [
                Entry("_antipiratePointsScored", new PyInt(campaign.AntipiratePointsScored)),
                Entry("_stateExpiryTimestamp", new PyLong(campaign.StateExpiryTimestamp)),
                Entry("_pirateFactionID", new PyInt(campaign.PirateFactionId)),
                Entry("_warzoneID", new PyInt(campaign.WarzoneId)),
                Entry("_coveredSolarsystemIDs", InventoryWire.Set(campaign.CoveredSolarSystemIds.Select(id => (PyValue)new PyInt(id)))),
                Entry("_campaignID", new PyInt(campaign.CampaignId)),
                Entry("_piratePointsRequired", new PyInt(campaign.PiratePointsRequired)),
                Entry("_piratePointsScored", new PyInt(campaign.PiratePointsScored)),
                Entry("_structureID", campaign.StructureId > 0 ? new PyInt(campaign.StructureId) : PyNone.Instance),
                Entry("_fsmState", new PyInt(campaign.FsmState)),
                Entry("_originSolarsystemID", new PyInt(campaign.OriginSolarSystemId)),
                Entry("_antipiratePointsRequired", new PyInt(campaign.AntipiratePointsRequired)),
            ]),
        ]),
        [],
        []);

    private static KeyValuePair<PyValue, PyValue> Entry(string name, PyValue value) => new(new PyStr(name), value);
}
