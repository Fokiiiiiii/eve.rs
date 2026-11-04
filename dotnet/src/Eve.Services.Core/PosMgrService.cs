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
        if (itemId <= 0)
            throw MachoErrors.ThrowUserError("POS_STRUCTURE_ID_INVALID");
        if (unanchorControlTower is null)
            throw NotAvailable();
        await unanchorControlTower(call.Session, itemId, call.Cancellation);
        return PyNone.Instance;
    }

    [Method("GetControlTowers")]
    public async ValueTask<PyValue> GetControlTowers(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        if (readControlTowers is null)
            throw NotAvailable();

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

    private static MachoWrappedException NotAvailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Control tower operations are not available on this server.")));
}
