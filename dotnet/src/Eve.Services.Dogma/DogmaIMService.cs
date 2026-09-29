using System.Collections.Concurrent;
using Eve.Characters;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Skills;

namespace Eve.Services.Dogma;

/// <summary>
/// The dogma service the client binds to for the place it is in (<c>dogmaIM</c>), and its first call, <c>GetAllInfo</c>: everything
/// the client needs to know about the character's ship, its fitting and the character. Working out attribute values is the job
/// of <c>Eve.Dogma</c>; this only loads what a character has, asks, and puts the answer in the shapes the client reads.
/// </summary>
[Service("dogmaIM")]
public sealed partial class DogmaIMService(
    DogmaStatic statics, PgCharacterStore characters, PgItemStore items, PgSkillStore skills, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    private readonly DogmaInfo _info = new(statics);

    /// <summary>What a bound dogma object is: the session that opened it and the place it was opened for.</summary>
    private sealed record Context(long SessionId, long LocationId);

    private readonly ConcurrentDictionary<string, Context> _bound = new(StringComparer.Ordinal);

    private static PyValue None => PyNone.Instance;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>A flag the client passes as a boolean, or None for "as usual".</summary>
    private static bool Flag(PyValue? value, bool fallback) => value switch
    {
        PyBool b => b.Value,
        PyInt n => n.Value != 0,
        PyLong n => n.Value != 0,
        _ => fallback,
    };

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    /// <summary>The proxy node serves every dogma object.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    private PyValue Bind(long locationId, ClientSession session)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = new Context(session.SessionId, locationId);
        session.Closed += () => _bound.TryRemove(oid, out _);
        return new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
    }

    /// <summary>
    /// <c>MachoBindObject((locationID, groupID), (method, args, kwargs))</c>: binds dogma for the place the client is in (its station), then makes
    /// the nested call, whose answer goes back with the bound object.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        if (IdOf(parameters) is not { } location || call.Session.GetInt("stationid") != location)
        {
            return None; // only the station the character is docked at can be bound to
        }
        var bound = Bind(location, call.Session);
        var oid = JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)bound).Value).Decoded!).Items[0]);
        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList ? (nested.Items[1] is PyTuple a ? a.Items : ((PyList)nested.Items[1]).Items) : [],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(bound, result);
    }

    /// <summary>
    /// <c>GetAllInfo(getCharInfo, getShipInfo, getStructureInfo)</c>: the ship the character sits in, what is fitted to it, the character and
    /// its brain. Each flag left out (None) is asked for, except the structure, which is asked for only inside one.
    /// </summary>
    [Method("GetAllInfo")]
    public async ValueTask<PyValue> GetAllInfo(ServiceCall call)
    {
        if (call.BoundObjectId is not { } oid || !_bound.TryGetValue(oid, out var context) || context.SessionId != call.Session.SessionId
            || call.Session.CharacterId is not { } character)
        {
            return None;
        }
        if (await DogmaSnapshot.LoadAsync(statics, characters, items, skills, character, call.Cancellation) is not { } snapshot)
        {
            return None;
        }
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        return _info.GetAllInfo(snapshot, Flag(call.Arg(0), true), Flag(call.Arg(1), true), Flag(call.Arg(2), false), fileTime);
    }
}
