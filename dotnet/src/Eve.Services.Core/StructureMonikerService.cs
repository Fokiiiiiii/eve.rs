using System.Collections.Concurrent;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Core;

/// <summary>The bound <c>structure</c> moniker used by the structure fuel jettison menu.</summary>
public sealed class StructureMonikerService(
    Func<ServiceCall, long, IReadOnlyList<long>, ValueTask>? jettisonStructureFuelAsync,
    Func<long> nowUnixMs) : IService
{
    private readonly ConcurrentDictionary<string, long> _boundStructures = new(StringComparer.Ordinal);

    public string Name => "structure";

    public bool HasMethod(string method) => method is "MachoResolveObject" or "MachoBindObject" or "JettisonStructureFuel";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (call.Method == "MachoResolveObject")
            return new PyInt(MachoMessage.ProxyNodeId);

        if (call.Method == "MachoBindObject")
        {
            var structureId = PositiveId(call.Arg(0));
            if (structureId == 0)
                throw Denied();
            var objectId = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
            _boundStructures[objectId] = structureId;
            var bound = new PySubStruct(new PySubStream(Py.Tuple(
                new PyStr(objectId), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
            PyValue result = PyNone.Instance;
            if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested &&
                JsSemantics.StringValue(nested.Items[0]) is { } method)
            {
                result = await CallBoundAsync(call, objectId, method,
                    nested.Items.Count > 1 ? nested.Items[1] : PyNone.Instance,
                    nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance).ConfigureAwait(false);
            }
            return Py.Tuple(bound, result);
        }

        if (call.Method == "JettisonStructureFuel")
            return await CallBoundAsync(call, call.BoundObjectId, call.Method, new PyTuple(call.Args), call.Kwargs).ConfigureAwait(false);

        return PyNone.Instance;
    }

    private async ValueTask<PyValue> CallBoundAsync(ServiceCall call, string? objectId, string method, PyValue? args, PyValue? kwargs)
    {
        if (method != "JettisonStructureFuel")
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The structure method is unavailable.")));
        if (objectId is null || !_boundStructures.TryGetValue(objectId, out var structureId) || jettisonStructureFuelAsync is null)
            throw Denied();
        var itemIds = PositiveIds(args is PyTuple tuple ? tuple.Items.FirstOrDefault() : args);
        await jettisonStructureFuelAsync(new ServiceCall
        {
            Method = method,
            Args = args is PyTuple values ? values.Items : [],
            Kwargs = kwargs ?? PyNone.Instance,
            Session = call.Session,
            CallId = call.CallId,
            BoundObjectId = objectId,
            Cancellation = call.Cancellation,
        }, structureId, itemIds).ConfigureAwait(false);
        return PyNone.Instance;
    }

    private static long PositiveId(PyValue? value) => value switch
    {
        PyInt integer when integer.Value > 0 => integer.Value,
        PyLong integer when integer.Value > 0 => integer.Value,
        _ => 0,
    };

    private static IReadOnlyList<long> PositiveIds(PyValue? value)
    {
        var ids = value switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Select structure fuel items to jettison."))),
        };
        if (ids.Count > 500)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Select no more than 500 structure fuel items.")));
        if (ids.Any(value => PositiveId(value) == 0))
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("Invalid structure fuel item.")));
        return ids.Select(PositiveId).Distinct().ToArray();
    }

    private static Exception Denied() => MachoErrors.ThrowUserError(
        "CrpAccessDenied", ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));
}
