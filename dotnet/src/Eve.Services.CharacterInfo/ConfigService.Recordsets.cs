using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterInfo;

public sealed partial class ConfigService
{
    private static readonly string[] PositionColumns = ["itemID", "x", "y", "z", "yaw", "pitch", "roll"];

    [Method("GetOwnersEx")]
    public ValueTask<PyValue> GetOwnersEx(ServiceCall call) =>
        GetMultiOwnersEx(BulkCall(call, "GetMultiOwnersEx", SingleId(call)));

    [Method("GetCorpTickerNamesEx")]
    public ValueTask<PyValue> GetCorpTickerNamesEx(ServiceCall call) =>
        GetMultiCorpTickerNamesEx(BulkCall(call, "GetMultiCorpTickerNamesEx", SingleId(call)));

    [Method("GetAllianceShortNamesEx")]
    public ValueTask<PyValue> GetAllianceShortNamesEx(ServiceCall call) =>
        GetMultiAllianceShortNamesEx(BulkCall(call, "GetMultiAllianceShortNamesEx", SingleId(call)));

    [Method("GetLocationsEx")]
    public ValueTask<PyValue> GetLocationsEx(ServiceCall call) =>
        GetMultiLocationsEx(BulkCall(call, "GetMultiLocationsEx", SingleId(call, includeZero: true)));

    [Method("GetLocationsLocal")]
    public ValueTask<PyValue> GetLocationsLocal(ServiceCall call) => GetMultiLocationsEx(call);

    [Method("GetPositionEx")]
    public PyValue GetPositionEx(ServiceCall call) =>
        GetPositionsEx(BulkCall(call, "GetPositionsEx", SingleId(call)));

    [Method("GetPositionsEx")]
    public PyValue GetPositionsEx(ServiceCall call)
    {
        var rows = RequestedIds(call.Arg(0))
            .Select(PositionRow)
            .Where(row => row is not null)
            .Cast<PyTuple>();
        return TupleSet(PositionColumns, rows);
    }

    private PyTuple? PositionRow(long id)
    {
        if (stations.Get(id) is { } station)
            return PositionRow(id, station.X, station.Y, station.Z);
        if (id <= int.MaxValue && solarSystems.Get((int)id) is { } system)
            return PositionRow(id, system.X, system.Y, system.Z);
        return null;
    }

    private static PyTuple PositionRow(long id, double x, double y, double z) =>
        Row(PyValue.Of(id), PyValue.Of(x), PyValue.Of(y), PyValue.Of(z),
            PyNone.Instance, PyNone.Instance, PyNone.Instance);

    private static PyValue SingleId(ServiceCall call, bool includeZero = false)
    {
        var id = call.Arg(0) switch
        {
            PyInt integer => (long)integer.Value,
            PyLong integer => integer.Value,
            _ => throw MachoErrors.ThrowUserError("CONFIG_IDENTIFIER_INVALID"),
        };
        if (id < 0 || id == 0 && !includeZero)
            throw MachoErrors.ThrowUserError("CONFIG_IDENTIFIER_INVALID");
        return PyValue.Of(id);
    }

    private static ServiceCall BulkCall(ServiceCall call, string method, PyValue id) => new()
    {
        Method = method,
        Args = [new PyList([id])],
        Kwargs = call.Kwargs,
        Session = call.Session,
        Cancellation = call.Cancellation,
    };
}
