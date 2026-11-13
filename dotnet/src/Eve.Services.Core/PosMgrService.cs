using Eve.Marshal;
using Eve.Macho;
using Eve.Services;

namespace Eve.Services.Core;

public sealed record ControlTowerRow(long TypeId, long StructureId, long SolarSystemId);

public delegate ValueTask<IReadOnlyList<ControlTowerRow>> ReadControlTowerRowsCallback(
    ClientSession session,
    CancellationToken cancellationToken);

/// <summary>Asks for a player control tower to be unanchored (posMgr.UnanchorStructure), in the pilot's system and for its corporation.</summary>
public delegate ValueTask UnanchorControlTowerCallback(ClientSession session, long itemId, CancellationToken cancellationToken);

/// <summary>Anchors a control tower at the V24 client supplied position (the legacy server resolves its moon placement).</summary>
public delegate ValueTask AnchorControlTowerCallback(ClientSession session, long itemId, IReadOnlyList<double> position, CancellationToken cancellationToken);

/// <summary>Executes a client posMgr operation in the owning domain and returns its client wire value.</summary>
public delegate ValueTask<PyValue> PosMgrOperationCallback(ServiceCall call);

[Service("posMgr")]
public sealed partial class PosMgrService(
    ReadControlTowerRowsCallback? readControlTowers = null,
    UnanchorControlTowerCallback? unanchorControlTower = null,
    AnchorControlTowerCallback? anchorControlTower = null,
    PosMgrOperationCallback? operation = null)
{
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var bound = new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid),
            new PyLong(Py.FileTimeFromUnixMs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = [.. ArgsOf(nested.Items.Count > 1 ? nested.Items[1] : PyNone.Instance)],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            }).ConfigureAwait(false);
        }
        return Py.Tuple(bound, result);
    }

    [Method("AnchorStructure")]
    public async ValueTask<PyValue> AnchorStructure(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        var itemId = call.Args.Count > 0 ? call.Args[0] switch { PyInt value => value.Value, PyLong value => value.Value, _ => 0L } : 0L;
        if (itemId <= 0)
            throw MachoErrors.ThrowUserError("POS_STRUCTURE_ID_INVALID");
        if (call.Args.Count < 2 || PositionOf(call.Args[1]) is not { } position)
            throw MachoErrors.ThrowUserError("POS_STRUCTURE_POSITION_INVALID");
        if (anchorControlTower is null)
            throw NotAvailable();
        await anchorControlTower(call.Session, itemId, position, call.Cancellation).ConfigureAwait(false);
        return PyNone.Instance;
    }

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

    [Method("AnchorOrbital")]
    public ValueTask<PyValue> AnchorOrbital(ServiceCall call) => Invoke(call);

    [Method("ChangeStructureProvisionType")]
    public ValueTask<PyValue> ChangeStructureProvisionType(ServiceCall call) => Invoke(call);

    [Method("GetConsumptionFactor")]
    public ValueTask<PyValue> GetConsumptionFactor(ServiceCall call) => Invoke(call);

    [Method("GetMoonForTower")]
    public ValueTask<PyValue> GetMoonForTower(ServiceCall call) => Invoke(call);

    [Method("GetMoonProcessInfoForTower")]
    public ValueTask<PyValue> GetMoonProcessInfoForTower(ServiceCall call) => Invoke(call);

    [Method("GetSiloCapacityByItemID")]
    public ValueTask<PyValue> GetSiloCapacityByItemID(ServiceCall call) => Invoke(call);

    [Method("GetStarbasePermissions")]
    public ValueTask<PyValue> GetStarbasePermissions(ServiceCall call) => Invoke(call);

    [Method("GetTowerNotificationSettings")]
    public ValueTask<PyValue> GetTowerNotificationSettings(ServiceCall call) => Invoke(call);

    [Method("GetTowerSentrySettings")]
    public ValueTask<PyValue> GetTowerSentrySettings(ServiceCall call) => Invoke(call);

    [Method("LinkResourceForTower")]
    public ValueTask<PyValue> LinkResourceForTower(ServiceCall call) => Invoke(call);

    [Method("OnlineOrbital")]
    public ValueTask<PyValue> OnlineOrbital(ServiceCall call) => Invoke(call);

    [Method("RunMoonProcessCycleforTower")]
    public ValueTask<PyValue> RunMoonProcessCycleforTower(ServiceCall call) => Invoke(call);

    [Method("SetStarbasePermissions")]
    public ValueTask<PyValue> SetStarbasePermissions(ServiceCall call) => Invoke(call);

    [Method("SetTowerNotifications")]
    public ValueTask<PyValue> SetTowerNotifications(ServiceCall call) => Invoke(call);

    [Method("SetTowerPassword")]
    public ValueTask<PyValue> SetTowerPassword(ServiceCall call) => Invoke(call);

    [Method("SetTowerSentrySettings")]
    public ValueTask<PyValue> SetTowerSentrySettings(ServiceCall call) => Invoke(call);

    [Method("UnanchorOrbital")]
    public ValueTask<PyValue> UnanchorOrbital(ServiceCall call) => Invoke(call);

    private ValueTask<PyValue> Invoke(ServiceCall call)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return operation is null ? ValueTask.FromException<PyValue>(NotAvailable()) : operation(call);
    }

    private static IEnumerable<PyValue> ArgsOf(PyValue value) => value switch
    {
        PyTuple tuple => tuple.Items,
        PyList list => list.Items,
        _ => [],
    };

    private static PyValue WireId(long value) => value <= int.MaxValue
        ? new PyInt((int)value)
        : new PyLong(value);

    private static double[]? PositionOf(PyValue value)
    {
        var values = value switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => [],
        };
        if (values.Count != 3)
            return null;
        var position = new double[3];
        for (var index = 0; index < position.Length; index++)
        {
            position[index] = values[index] switch
            {
                PyReal real => real.Value,
                PyInt integer => integer.Value,
                PyLong integer => integer.Value,
                _ => double.NaN,
            };
            if (!double.IsFinite(position[index]))
                return null;
        }
        return position;
    }

    private static MachoWrappedException NotAvailable() => MachoErrors.ThrowUserError("CustomNotify",
        ("notify", new PyStr("Control tower operations are not available on this server.")));
}
