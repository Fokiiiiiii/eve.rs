using System.Globalization;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>Read-only sovereignty and faction-warfare state assembled from the host's SDE and persisted records.</summary>
public sealed record SovFwReadSnapshot(
    IReadOnlyDictionary<long, SovClaimRecord> ClaimsBySolarSystemId,
    IReadOnlyDictionary<long, SovHubRecord> HubsBySolarSystemId,
    IReadOnlyDictionary<long, IReadOnlyList<SovStructureRecord>> StructuresBySolarSystemId,
    IReadOnlyList<FwOccupationRecord> FwOccupationStates,
    IReadOnlyDictionary<long, long?> FuelAccessGroupIdsBySolarSystemId)
{
    public static SovFwReadSnapshot Empty { get; } = new(
        new Dictionary<long, SovClaimRecord>(),
        new Dictionary<long, SovHubRecord>(),
        new Dictionary<long, IReadOnlyList<SovStructureRecord>>(),
        [],
        new Dictionary<long, long?>());
}

public sealed record SovClaimRecord(long? ClaimStructureId, long? CorporationId, long? AllianceId);

public sealed record SovHubRecord(long? HubId, long? CorporationId, long? AllianceId, long? ClaimTimeFileTime);

public sealed record SovStructureRecord(
    long ItemId,
    long TypeId,
    long? OwnerId,
    long? CorporationId,
    long? AllianceId,
    long SolarSystemId,
    int? CampaignEventType = null,
    long? CampaignStartTimeFileTime = null,
    IReadOnlyDictionary<long, double>? CampaignScoresByTeam = null,
    long? VulnerableStartTimeFileTime = null,
    long? VulnerableEndTimeFileTime = null,
    double DefenseMultiplier = 1,
    bool IsCapital = false);

public sealed record FwOccupationRecord(long WarzoneId, long SolarSystemId, long OccupierId);

/// <summary>Creates the EVE.js read-only sovMgr and fwWarzoneSolarsystem endpoints.</summary>
public static class SovFwService
{
    internal static long PositiveInteger(PyValue value) => value switch
    {
        PyInt number when number.Value > 0 => number.Value,
        PyLong number when number.Value > 0 => number.Value,
        PyReal number when double.IsFinite(number.Value) && number.Value > 0 &&
            number.Value <= long.MaxValue && Math.Truncate(number.Value) == number.Value => (long)number.Value,
        PyStr text when long.TryParse(text.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0 => number,
        _ => 0,
    };

    public static IReadOnlyList<IService> Create(
        Func<ServiceCall, ValueTask<SovFwReadSnapshot>> readSnapshot,
        Func<ServiceCall, ValueTask<bool>> isOnLocalFuelAccessGroup)
    {
        ArgumentNullException.ThrowIfNull(readSnapshot);
        ArgumentNullException.ThrowIfNull(isOnLocalFuelAccessGroup);
        return
        [
            new SovMgrService(readSnapshot, isOnLocalFuelAccessGroup),
            new FwWarzoneSolarsystemService(readSnapshot),
        ];
    }
}

[Service("sovMgr")]
public sealed partial class SovMgrService
{
    private readonly Func<ServiceCall, ValueTask<SovFwReadSnapshot>> _readSnapshot;
    private readonly Func<ServiceCall, ValueTask<bool>> _isOnLocalFuelAccessGroup;

    public SovMgrService(
        Func<ServiceCall, ValueTask<SovFwReadSnapshot>> readSnapshot,
        Func<ServiceCall, ValueTask<bool>> isOnLocalFuelAccessGroup)
    {
        _readSnapshot = readSnapshot;
        _isOnLocalFuelAccessGroup = isOnLocalFuelAccessGroup;
    }

    [Method("GetSovStructuresInfoForLocalSolarSystem")]
    public async ValueTask<PyValue> GetSovStructuresInfoForLocalSolarSystem(ServiceCall call)
    {
        var systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        var snapshot = await _readSnapshot(call);
        return StructureList(snapshot, systemId);
    }

    [Method("GetSovStructuresInfoForSolarSystem")]
    public async ValueTask<PyValue> GetSovStructuresInfoForSolarSystem(ServiceCall call)
    {
        var snapshot = await _readSnapshot(call);
        return StructureList(snapshot, SovFwService.PositiveInteger(call.Arg(0)));
    }

    [Method("GetSystemSovereigntyInfo")]
    public async ValueTask<PyValue> GetSystemSovereigntyInfo(ServiceCall call)
    {
        var snapshot = await _readSnapshot(call);
        return snapshot.ClaimsBySolarSystemId.TryGetValue(SovFwService.PositiveInteger(call.Arg(0)), out var claim)
            ? ObjectEx1("sovereignty.data_types.SovClaimInfo",
                NullableId(claim.ClaimStructureId), NullableId(claim.CorporationId), NullableId(claim.AllianceId))
            : PyNone.Instance;
    }

    [Method("GetInfrastructureHubInfo")]
    public async ValueTask<PyValue> GetInfrastructureHubInfo(ServiceCall call)
    {
        var snapshot = await _readSnapshot(call);
        return snapshot.HubsBySolarSystemId.TryGetValue(SovFwService.PositiveInteger(call.Arg(0)), out var hub)
            ? ObjectEx1("sovereignty.data_types.SovHubInfo",
                NullableId(hub.HubId), NullableId(hub.CorporationId), NullableId(hub.AllianceId),
                Py.FileTimeLong(hub.ClaimTimeFileTime ?? 0))
            : PyNone.Instance;
    }

    [Method("GetSovHubFuelAccessGroup")]
    public async ValueTask<PyValue> GetSovHubFuelAccessGroup(ServiceCall call)
    {
        var snapshot = await _readSnapshot(call);
        return snapshot.FuelAccessGroupIdsBySolarSystemId.TryGetValue(SovFwService.PositiveInteger(call.Arg(0)), out var groupId)
            ? NullableId(groupId)
            : PyNone.Instance;
    }

    [Method("IsOnLocalSovHubFuelAccessGroup")]
    public async ValueTask<PyValue> IsOnLocalSovHubFuelAccessGroup(ServiceCall call) =>
        await _isOnLocalFuelAccessGroup(call) ? PyBool.True : PyBool.False;

    private static PyValue StructureList(SovFwReadSnapshot snapshot, long systemId) =>
        snapshot.StructuresBySolarSystemId.TryGetValue(systemId, out var structures)
            ? Py.List([.. structures.Select(StructurePayload)])
            : Py.List();

    private static PyValue StructurePayload(SovStructureRecord structure) => Py.KeyVal(
        ("itemID", new PyInt(structure.ItemId)),
        ("typeID", new PyInt(structure.TypeId)),
        ("ownerID", NullableId(structure.OwnerId)),
        ("corporationID", NullableId(structure.CorporationId)),
        ("allianceID", NullableId(structure.AllianceId)),
        ("solarSystemID", NullableId(structure.SolarSystemId)),
        ("campaignState", CampaignState(structure)),
        ("vulnerabilityState", VulnerabilityState(structure)),
        ("defenseMultiplier", Py.Number(structure.DefenseMultiplier)),
        ("isCapital", structure.IsCapital ? PyBool.True : PyBool.False));

    private static PyValue CampaignState(SovStructureRecord structure)
    {
        if (structure.CampaignEventType is not > 0 || structure.CampaignStartTimeFileTime is not > 0)
        {
            return PyNone.Instance;
        }

        var scores = new PyDict([
            .. (structure.CampaignScoresByTeam ?? new Dictionary<long, double>())
                .OrderBy(pair => pair.Key)
                .Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), Py.Number(pair.Value)))
        ]);
        return Py.List(
            new PyInt(structure.CampaignEventType.Value),
            NullableId(structure.AllianceId),
            Py.FileTimeLong(structure.CampaignStartTimeFileTime.Value),
            scores);
    }

    private static PyValue VulnerabilityState(SovStructureRecord structure) =>
        structure.VulnerableStartTimeFileTime is > 0 && structure.VulnerableEndTimeFileTime is > 0
            ? Py.List(Py.FileTimeLong(structure.VulnerableStartTimeFileTime.Value),
                Py.FileTimeLong(structure.VulnerableEndTimeFileTime.Value))
            : PyNone.Instance;

    private static PyValue ObjectEx1(string typeName, params PyValue[] args) => new PyObjectEx(
        ObjectExKind.Ex1,
        new PyTuple([new PyToken(typeName), new PyList(args)]),
        [],
        []);

    private static PyValue NullableId(long? id) => id is > 0 ? new PyInt(id.Value) : PyNone.Instance;

}

[Service("fwWarzoneSolarsystem")]
public sealed partial class FwWarzoneSolarsystemService
{
    private const long Amarr = 500003;
    private const long Caldari = 500001;
    private const long Gallente = 500004;
    private const long Minmatar = 500002;
    private const long AmarrMinmatarWarzone = 1;
    private const long CaldariGallenteWarzone = 2;

    private static readonly (long Id, long[] Factions)[] Warzones =
    [
        (AmarrMinmatarWarzone, [Amarr, Minmatar]),
        (CaldariGallenteWarzone, [Caldari, Gallente]),
    ];

    private static readonly (long Faction, long System)[] Headquarters =
    [
        (Amarr, 30002974),
        (Caldari, 30045324),
        (Gallente, 30003788),
        (Minmatar, 30002055),
    ];

    private readonly Func<ServiceCall, ValueTask<SovFwReadSnapshot>> _readSnapshot;

    public FwWarzoneSolarsystemService(Func<ServiceCall, ValueTask<SovFwReadSnapshot>> readSnapshot) =>
        _readSnapshot = readSnapshot;

    [Method("GetAllWarzonesOccupationStates")]
    public async ValueTask<PyValue> GetAllWarzonesOccupationStates(ServiceCall call) =>
        BuildOccupationStates(await _readSnapshot(call));

    [Method("GetAllWarzonesOccupationStatesUncached")]
    public async ValueTask<PyValue> GetAllWarzonesOccupationStatesUncached(ServiceCall call) =>
        BuildOccupationStates(await _readSnapshot(call));

    [Method("GetAllWarzones")]
    public PyValue GetAllWarzones(ServiceCall call) => NumericDict(
        Warzones.Select(zone => new KeyValuePair<long, PyValue>(zone.Id,
            Py.List([.. zone.Factions.Select(faction => (PyValue)new PyInt(faction))]))));

    [Method("GetHQSystemIDs")]
    public PyValue GetHQSystemIDs(ServiceCall call) => NumericDict(
        Headquarters.Select(entry => new KeyValuePair<long, PyValue>(entry.Faction, new PyInt(entry.System))));

    [Method("GetLocalOccupationState")]
    public async ValueTask<PyValue> GetLocalOccupationState(ServiceCall call)
    {
        var systemId = SovFwService.PositiveInteger(call.Arg(0));
        if (systemId == 0)
        {
            systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;
        }

        var snapshot = await _readSnapshot(call);
        foreach (var (warzoneId, _) in Warzones)
        {
            var state = ValidStates(snapshot, warzoneId).FirstOrDefault(item => item.SolarSystemId == systemId);
            if (state is not null)
            {
                return Py.Tuple(new PyInt(systemId), OccupationPayload(state));
            }
        }
        return Py.Tuple(new PyInt(systemId), PyNone.Instance);
    }

    private static PyValue BuildOccupationStates(SovFwReadSnapshot snapshot) => NumericDict(
        Warzones.Select(zone => new KeyValuePair<long, PyValue>(zone.Id,
            NumericDict(ValidStates(snapshot, zone.Id).Select(state =>
                new KeyValuePair<long, PyValue>(state.SolarSystemId, OccupationPayload(state)))))));

    private static IEnumerable<FwOccupationRecord> ValidStates(SovFwReadSnapshot snapshot, long warzoneId)
    {
        var allowedFactions = Warzones.Single(zone => zone.Id == warzoneId).Factions;
        return snapshot.FwOccupationStates
            .Where(state => state.WarzoneId == warzoneId && state.SolarSystemId > 0 && allowedFactions.Contains(state.OccupierId))
            .GroupBy(state => state.SolarSystemId)
            .Select(group => group.First())
            .OrderBy(state => state.SolarSystemId);
    }

    private static PyValue OccupationPayload(FwOccupationRecord state) => Py.KeyVal(
        ("occupierID", new PyInt(state.OccupierId)),
        ("solarSystemID", new PyInt(state.SolarSystemId)),
        ("warzoneID", new PyInt(state.WarzoneId)),
        ("adjacencyState", new PyInt(0)));

    private static PyDict NumericDict(IEnumerable<KeyValuePair<long, PyValue>> entries) => new(
        [.. entries.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), entry.Value))]);

}
