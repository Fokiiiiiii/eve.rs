using System.Collections.Concurrent;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Space;

/// <summary>
/// How a service that the client binds to for a place (<c>ship</c> for the station, <c>beyonce</c> for the solar system) hands out and
/// recognises its bound objects: <c>MachoBindObject((locationID, groupID), (method, args, kwargs))</c> binds, then makes the nested call, and
/// the answer goes back with the bound object.
/// </summary>
internal sealed class Binding(Func<long> nowUnixMs)
{
    private readonly ConcurrentDictionary<string, long> _bound = new(StringComparer.Ordinal);

    private static PyValue None => PyNone.Instance;

    /// <summary>The place a bind is for: the first of the pair the client sends.</summary>
    public static long? PlaceOf(PyValue parameters)
    {
        var place = parameters switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        return place switch { PyInt number => number.Value, PyLong number => number.Value, _ => null };
    }

    /// <summary>Hands the session a bound object: <c>substruct(substream((oid, time)))</c>.</summary>
    public (PyValue Bound, string Oid) Bind(ClientSession session)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = session.SessionId;
        session.Closed += () => _bound.TryRemove(oid, out _);
        return (new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs()))))), oid);
    }

    /// <summary>Whether the call is addressed to a bound object this session was given.</summary>
    public bool Owns(ServiceCall call) => call.BoundObjectId is { } oid && _bound.TryGetValue(oid, out var session) && session == call.Session.SessionId;

    public void Release(string oid) => _bound.TryRemove(oid, out _);

    /// <summary>Makes the call nested in a bind, on the bound object just handed out; None when there is none.</summary>
    public static async ValueTask<PyValue> NestedAsync(IService service, ServiceCall bind, string oid)
    {
        if (bind.Arg(1) is not PyTuple { Items.Count: > 0 } nested || JsSemantics.StringValue(nested.Items[0]) is not { } method)
        {
            return None;
        }
        return await service.CallAsync(new ServiceCall
        {
            Method = method,
            Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList ? (nested.Items[1] is PyTuple a ? a.Items : ((PyList)nested.Items[1]).Items) : [],
            Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
            Session = bind.Session,
            CallId = bind.CallId,
            BoundObjectId = oid,
            Cancellation = bind.Cancellation,
        });
    }
}
