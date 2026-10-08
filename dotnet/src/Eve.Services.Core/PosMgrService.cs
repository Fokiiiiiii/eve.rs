using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record ControlTowerRow(long TypeId, long StructureId, long SolarSystemId);

public delegate ValueTask<IReadOnlyList<ControlTowerRow>> ReadControlTowerRowsCallback(
    ClientSession session,
    CancellationToken cancellationToken);

[Service("posMgr")]
public sealed partial class PosMgrService(ReadControlTowerRowsCallback? readControlTowers = null)
{
    [Method("GetControlTowers")]
    public async ValueTask<PyValue> GetControlTowers(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (readControlTowers is null)
            return new PyList([]);

        var rows = await readControlTowers(call.Session, call.Cancellation).ConfigureAwait(false);
        return new PyList([.. rows
            .Where(row => row.TypeId > 0 && row.StructureId > 0 && row.SolarSystemId > 0)
            .Select(row => (PyValue)new PyList([
                WireId(row.TypeId),
                WireId(row.StructureId),
                WireId(row.SolarSystemId),
            ]))]);
    }

    private static PyValue WireId(long value) => value <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);
}
