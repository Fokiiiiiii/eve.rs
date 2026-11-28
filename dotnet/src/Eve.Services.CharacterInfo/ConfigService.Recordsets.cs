using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.CharacterInfo;

public sealed partial class ConfigService
{
    private static readonly string[] PositionColumns = ["itemID", "x", "y", "z", "yaw", "pitch", "roll"];

    [Method("GetOwnersEx")]
    public ValueTask<PyValue> GetOwnersEx(ServiceCall call) =>
        GetMultiOwnersEx(BulkCall(call));

    [Method("GetCorpTickerNamesEx")]
    public ValueTask<PyValue> GetCorpTickerNamesEx(ServiceCall call) =>
        GetMultiCorpTickerNamesEx(BulkCall(call));

    [Method("GetAllianceShortNamesEx")]
    public ValueTask<PyValue> GetAllianceShortNamesEx(ServiceCall call) =>
        GetMultiAllianceShortNamesEx(BulkCall(call));

    [Method("GetLocationsEx")]
    public ValueTask<PyValue> GetLocationsEx(ServiceCall call) =>
        GetMultiLocationsEx(BulkCall(call));

    [Method("GetLocationsLocal")]
    public ValueTask<PyValue> GetLocationsLocal(ServiceCall call) => GetMultiLocationsEx(call);

    [Method("GetPositionEx")]
    public PyValue GetPositionEx(ServiceCall call) =>
        GetPositionsEx(BulkCall(call));

    [Method("GetPositionsEx")]
    public PyValue GetPositionsEx(ServiceCall call)
    {
        var rows = new List<PyTuple>();
        foreach (var id in RequestedIds(call))
        {
            if (PositionRow(id) is { } row)
                rows.Add(row);
            else
                LogRejectedIdentifiers(call.Method, [PyValue.Of(id)]);
        }
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

    private static ServiceCall BulkCall(ServiceCall call) => new()
    {
        Method = call.Method,
        Args = [new PyList([call.Arg(0)])],
        Kwargs = call.Kwargs,
        Session = call.Session,
        Cancellation = call.Cancellation,
    };
}
