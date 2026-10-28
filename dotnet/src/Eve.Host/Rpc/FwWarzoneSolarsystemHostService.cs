using Eve.FactionWarfare;
using Eve.Marshal;
using Eve.Services;
using Eve.Services.Core;

namespace Eve.Host;

[Service("fwWarzoneSolarsystem")]
public sealed partial class FwWarzoneSolarsystemHostService(
    Func<ServiceCall, ValueTask<SovFwReadSnapshot>> readSnapshot,
    Func<CancellationToken, ValueTask<IReadOnlyDictionary<long, IReadOnlyCollection<long>>>> readNeighbors,
    Func<CancellationToken, ValueTask<IReadOnlyDictionary<long, long>>> readHeadquarters)
{
    [Method("GetAllWarzonesOccupationStates")]
    public async ValueTask<PyValue> GetAllWarzonesOccupationStates(ServiceCall call) =>
        await OccupationStatesAsync(call);

    [Method("GetAllWarzonesOccupationStatesUncached")]
    public async ValueTask<PyValue> GetAllWarzonesOccupationStatesUncached(ServiceCall call) =>
        await OccupationStatesAsync(call);

    [Method("GetAllWarzones")]
    public PyValue GetAllWarzones(ServiceCall call) => NumericDict(
        FwWarzones.Ids.Select(warzoneId => new KeyValuePair<long, PyValue>(
            warzoneId, Py.List([.. FwWarzones.FactionsOf(warzoneId).Select(id => (PyValue)new PyInt(id))]))));

    [Method("GetHQSystemIDs")]
    public async ValueTask<PyValue> GetHQSystemIDs(ServiceCall call) => NumericDict(
        (await readHeadquarters(call.Cancellation)).Select(entry =>
            new KeyValuePair<long, PyValue>(entry.Key, new PyInt(entry.Value))));

    [Method("GetLocalOccupationState")]
    public async ValueTask<PyValue> GetLocalOccupationState(ServiceCall call)
    {
        var systemId = call.Args.Count > 0 ? PositiveInteger(call.Arg(0)) : 0;
        if (systemId == 0)
            systemId = call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid") ?? 0;

        var states = await OccupationStatesAsync(call);
        foreach (var warzone in AssertDict(states).Entries)
        {
            if (AssertDict(warzone.Value).Entries.FirstOrDefault(entry => PositiveInteger(entry.Key) == systemId) is { Value: { } payload })
                return Py.Tuple(new PyInt(systemId), payload);
        }
        return Py.Tuple(new PyInt(systemId), PyNone.Instance);
    }

    private async ValueTask<PyValue> OccupationStatesAsync(ServiceCall call)
    {
        var snapshot = await readSnapshot(call);
        var neighbors = await readNeighbors(call.Cancellation);
        return NumericDict(FwWarzones.Ids.Select(warzoneId =>
        {
            var states = ValidStates(snapshot, warzoneId);
            var occupiers = states.ToDictionary(state => state.SolarSystemId, state => state.OccupierId);
            var adjacency = FwWarzones.AdjacencyStates(warzoneId, occupiers, neighbors);
            var payloads = states.Select(state => new KeyValuePair<long, PyValue>(
                state.SolarSystemId,
                Py.KeyVal(
                    ("occupierID", new PyInt(state.OccupierId)),
                    ("solarSystemID", new PyInt(state.SolarSystemId)),
                    ("warzoneID", new PyInt(state.WarzoneId)),
                    ("adjacencyState", new PyStr(adjacency[state.SolarSystemId])))));
            return new KeyValuePair<long, PyValue>(warzoneId, NumericDict(payloads));
        }));
    }

    private static IReadOnlyList<FwOccupationRecord> ValidStates(SovFwReadSnapshot snapshot, long warzoneId)
    {
        var allowedFactions = FwWarzones.FactionsOf(warzoneId);
        return snapshot.FwOccupationStates
            .Where(state => state.WarzoneId == warzoneId && state.SolarSystemId > 0 && allowedFactions.Contains(state.OccupierId))
            .GroupBy(state => state.SolarSystemId)
            .Select(group => group.First())
            .OrderBy(state => state.SolarSystemId)
            .ToArray();
    }

    private static long PositiveInteger(PyValue value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        PyReal real when double.IsFinite(real.Value) && real.Value > 0 &&
            real.Value <= long.MaxValue && Math.Truncate(real.Value) == real.Value => (long)real.Value,
        _ => 0,
    };

    private static PyDict AssertDict(PyValue value) => value as PyDict
        ?? throw new InvalidOperationException("Faction Warfare occupation data must be a Python dict.");

    private static PyDict NumericDict(IEnumerable<KeyValuePair<long, PyValue>> entries) => new(
        [.. entries.Select(entry => new KeyValuePair<PyValue, PyValue>(new PyInt(entry.Key), entry.Value))]);
}
