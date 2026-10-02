using System.Collections.Concurrent;
using Eve.Crimewatch;
using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Crimewatch;

/// <summary>
/// The crimewatch service the client binds for the place it is in (<c>crimewatch</c>, bound by <c>(locationID, groupID)</c> like dogma). The client asks it for everything it shows of
/// crimewatch when it enters a place (<c>GetClientStates</c>: the five combat timers, its engagements, who is flagged, its ship safety level) and keeps the rest from the events it is
/// sent. The server keeps one bound object per session and hands it again on every bind (as the reference does). Security status is the character's <c>security_status</c> relation
/// (<c>Eve.Relations</c>); the service takes it as a function so that it knows nothing of where it is kept.
/// </summary>
[Service("crimewatch")]
public sealed partial class CrimewatchService(
    PgCrimewatchStore store, Func<long> nowUnixMs, Func<long, CancellationToken, Task<double>>? securityStatusOf = null) : IBoundObjectReleaseHandler
{
    // the states of the five combat timers (the client's weaponsTimerState…, in the order of the tuple of GetClientStates)
    private const int WeaponsIdle = 100;
    private const int PvpIdle = 200;
    private const int CriminalIdle = 300;
    private const int NpcIdle = 400;
    private const int DisapprovalIdle = 500;

    private readonly ConcurrentDictionary<long, string> _boundOfSession = new();

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session)
    {
        if (session is not null)
        {
            _boundOfSession.TryRemove(new KeyValuePair<long, string>(session.SessionId, oid));
        }
    }

    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    private PyValue Bind(ClientSession session)
    {
        var oid = _boundOfSession.GetOrAdd(session.SessionId, _ =>
        {
            session.Closed += () => _boundOfSession.TryRemove(session.SessionId, out string? _);
            return $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        });
        return new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
    }

    /// <summary><c>MachoBindObject((locationID, groupID), (method, args, kwargs))</c>: the bound object of the session, and the answer of the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var bound = Bind(call.Session);
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

    private static long? Number(PyValue value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => (long)r.Value, _ => null };

    /// <summary>
    /// <c>GetClientStates()</c>: <c>((weapons, pvp, npc, criminal, disapproval), engagements, (criminals, suspects), safetyLevel)</c>. Each timer is <c>(state, expiry)</c>, idle with no
    /// expiry for now; the engagements are a dictionary of the other character to when it ends; the flagged are two sets of character ids.
    /// </summary>
    [Method("GetClientStates")]
    public async ValueTask<PyValue> GetClientStates(ServiceCall call)
    {
        var safety = call.Session.CharacterId is { } character ? await store.SafetyOfAsync(character, call.Cancellation) : SafetyLevel.Full;
        return Py.Tuple(
            Py.Tuple(
                Py.Tuple(new PyInt(WeaponsIdle), None), Py.Tuple(new PyInt(PvpIdle), None), Py.Tuple(new PyInt(NpcIdle), None),
                Py.Tuple(new PyInt(CriminalIdle), None), Py.Tuple(new PyInt(DisapprovalIdle), None)),
            new PyDict([]),
            Py.Tuple(InventoryWire.Set([]), InventoryWire.Set([])),
            new PyInt(safety));
    }

    /// <summary><c>GetSafetyLevel()</c>: 0 (none), 1 (partial) or 2 (full).</summary>
    [Method("GetSafetyLevel")]
    public async ValueTask<PyValue> GetSafetyLevel(ServiceCall call) =>
        new PyInt(call.Session.CharacterId is { } character ? await store.SafetyOfAsync(character, call.Cancellation) : SafetyLevel.Full);

    /// <summary><c>SetSafetyLevel(level)</c>: keeps the level (brought to 0, 1 or 2) and answers with what was kept.</summary>
    [Method("SetSafetyLevel")]
    public async ValueTask<PyValue> SetSafetyLevel(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character)
        {
            return new PyInt(SafetyLevel.Full);
        }
        double? requested = call.Arg(0) switch { PyReal real => real.Value, var other => Number(other) };
        return new PyInt(await store.SetSafetyAsync(character, requested, nowUnixMs(), call.Cancellation));
    }

    private async ValueTask<PyValue> SecurityStatus(long? character, CancellationToken cancellationToken) =>
        Py.Number(character is { } id && securityStatusOf is not null ? await securityStatusOf(id, cancellationToken) : 0);

    /// <summary><c>GetMySecurityStatus()</c>: the character's security status.</summary>
    [Method("GetMySecurityStatus")]
    public ValueTask<PyValue> GetMySecurityStatus(ServiceCall call) => SecurityStatus(call.Session.CharacterId, call.Cancellation);

    /// <summary><c>GetCharacterSecurityStatus(characterID)</c>: that character's security status (the caller's own when none is named).</summary>
    [Method("GetCharacterSecurityStatus")]
    public ValueTask<PyValue> GetCharacterSecurityStatus(ServiceCall call) => SecurityStatus(Number(call.Arg(0)) is { } named and > 0 ? named : call.Session.CharacterId, call.Cancellation);

    /// <summary><c>GetSecurityStatusTransactions()</c>: what changed the security status (nothing has yet).</summary>
    [Method("GetSecurityStatusTransactions")]
    public PyValue GetSecurityStatusTransactions(ServiceCall call) => new PyList([]);
}
