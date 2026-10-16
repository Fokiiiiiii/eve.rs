using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record ControlTowerRow(long TypeId, long StructureId, long SolarSystemId);

public delegate ValueTask<IReadOnlyList<ControlTowerRow>> ReadControlTowerRowsCallback(
    ClientSession session,
    CancellationToken cancellationToken);

/// <summary>Asks for a player control tower to be unanchored (posMgr.UnanchorStructure), in the pilot's system and for its corporation.</summary>
public delegate ValueTask UnanchorControlTowerCallback(ClientSession session, long itemId, CancellationToken cancellationToken);

[Service("posMgr")]
public sealed partial class PosMgrService(ReadControlTowerRowsCallback? readControlTowers = null, UnanchorControlTowerCallback? unanchorControlTower = null)
{
    [Method("UnanchorStructure")]
    public async ValueTask<PyValue> UnanchorStructure(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var itemId = call.Args.Count > 0 ? call.Args[0] switch { PyInt value => value.Value, PyLong value => value.Value, _ => 0L } : 0L;
        if (unanchorControlTower is null || itemId <= 0)
        {
            return PyNone.Instance;
        }
        await unanchorControlTower(call.Session, itemId, call.Cancellation);
        return PyNone.Instance;
    }

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
