using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The <c>beyonce</c> service the client binds to for the solar system it is in: binding it starts the destiny stream, and its commands steer
/// the ship. The simulation does the work (<see cref="SpaceSessions"/>); this reads the client's calls.
/// </summary>
[Service("beyonce")]
public sealed partial class BeyonceService(SpaceSessions space, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _binding.Release(oid);

    private static double? Number(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        PyReal number => number.Value,
        _ => null,
    };

    /// <summary>The proxy node serves every ballpark.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// <c>MachoBindObject((solarSystemID, groupID), (method, args, kwargs))</c>: binds the ballpark of the solar system the ship is in. Once the
    /// answer has gone the simulation sends the scene.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        if (Binding.PlaceOf(call.Arg(0)) is not { } place || call.Session.GetInt("solarsystemid") != place || !space.IsInSpace(call.Session))
        {
            return None;
        }
        var (bound, oid) = _binding.Bind(call.Session);
        var result = await Binding.NestedAsync(this, call, oid);
        call.Session.AfterResponse(() => space.Bind(call.Session));
        return Py.Tuple(bound, result);
    }

    /// <summary><c>GetFormations()</c>: the formations a fleet can fly in, which the client keeps for the length of the session.</summary>
    [Method("GetFormations")]
    public PyValue GetFormations(ServiceCall call) => ObjectCaching.MethodResult(Formations(), nowUnixMs());

    private static PyValue Formations()
    {
        static PyValue Point(int x, int y, int z) => Py.Tuple(new PyInt(x), new PyInt(y), new PyInt(z));
        return new PyList([
            Py.Tuple(new PyStr("Diamond"), new PyList([Point(100, 0, 0), Point(0, 100, 0), Point(-100, 0, 0), Point(0, -100, 0)])),
            Py.Tuple(new PyStr("Arrow"), new PyList([Point(100, 0, -50), Point(50, 0, 0), Point(-100, 0, -50), Point(-50, 0, 0)])),
        ]);
    }

    /// <summary><c>UpdateStateRequest()</c>: the client wants the state of every ball again.</summary>
    [Method("UpdateStateRequest")]
    public PyValue UpdateStateRequest(ServiceCall call)
    {
        if (_binding.Owns(call))
        {
            space.UpdateState(call.Session);
        }
        return None;
    }

    /// <summary><c>CmdSetSpeedFraction(fraction)</c>: how much of its top speed the ship is to fly at.</summary>
    [Method("CmdSetSpeedFraction")]
    public PyValue CmdSetSpeedFraction(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } fraction)
        {
            space.Command(call.Session, SimCommands.SetSpeedFraction, fraction);
        }
        return None;
    }

    /// <summary><c>CmdGotoDirection(x, y, z)</c>: fly along this direction.</summary>
    [Method("CmdGotoDirection")]
    public PyValue CmdGotoDirection(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } x && Number(call.Arg(1)) is { } y && Number(call.Arg(2)) is { } z)
        {
            space.Command(call.Session, SimCommands.GotoDirection, x, y, z);
        }
        return None;
    }

    /// <summary><c>CmdStop()</c>: slow to a stop.</summary>
    [Method("CmdStop")]
    public PyValue CmdStop(ServiceCall call)
    {
        if (_binding.Owns(call))
        {
            space.Command(call.Session, SimCommands.Stop);
        }
        return None;
    }

    /// <summary><c>CmdFollowBall(targetID, range)</c>: go to a body and stay <c>range</c> metres off it.</summary>
    [Method("CmdFollowBall")]
    public PyValue CmdFollowBall(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } target)
        {
            space.Command(call.Session, SimCommands.FollowBall, target, Number(call.Arg(1)) ?? 0);
        }
        return None;
    }

    /// <summary><c>CmdOrbit(targetID, distance)</c>: circle a body <c>distance</c> metres off it.</summary>
    [Method("CmdOrbit")]
    public PyValue CmdOrbit(ServiceCall call)
    {
        if (_binding.Owns(call) && Number(call.Arg(0)) is { } target)
        {
            space.Command(call.Session, SimCommands.Orbit, target, Number(call.Arg(1)) ?? 0);
        }
        return None;
    }

    /// <summary><c>CmdAlignTo(targetID)</c> (or <c>dstID=</c>): turn towards a body and fly at it.</summary>
    [Method("CmdAlignTo")]
    public PyValue CmdAlignTo(ServiceCall call)
    {
        var target = Number(call.Arg(0)) is { } positional && positional != 0
            ? positional
            : call.Kwarg("dstID") is { } named ? Number(named) : null;
        if (_binding.Owns(call) && target is { } id && id > 0)
        {
            space.Command(call.Session, SimCommands.AlignTo, id);
        }
        return None;
    }

    /// <summary>
    /// <c>CmdWarpToStuff(type, target, minRange=)</c>: warp to a body of the system (<c>type</c> is <c>"item"</c>; the other kinds of place, bookmarks,
    /// scanned sites and the like, are not there yet and are told as a warp that cannot be done).
    /// </summary>
    [Method("CmdWarpToStuff")]
    public async ValueTask<PyValue> CmdWarpToStuff(ServiceCall call)
    {
        if (!_binding.Owns(call))
        {
            return None;
        }
        if (JsSemantics.PlainText(call.Arg(0)) != "item" || Number(call.Arg(1)) is not { } target)
        {
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("You cannot warp there right now.")));
        }
        var range = call.Kwarg("minRange") is { } named ? Number(named) ?? 0 : 0;
        return await space.WarpAsync(call.Session, (long)target, range, call.Cancellation);
    }


    /// <summary>Autopilot uses the same warp command but arrives 10 km from its destination.</summary>
    [Method("CmdWarpToStuffAutopilot")]
    public async ValueTask<PyValue> CmdWarpToStuffAutopilot(ServiceCall call)
    {
        if (!_binding.Owns(call))
        {
            return None;
        }
        var target = Number(call.Arg(0)) ?? 0;
        return await space.WarpAsync(call.Session, (long)target, 10_000, call.Cancellation);
    }

    /// <summary>
    /// <c>CmdStargateJump(fromGate, toGate, shipID)</c>: go into a stargate. The answer is the bound object and the time it can be used from again;
    /// the ship is in the other solar system a few seconds later, when the client is told the session has changed.
    /// </summary>
    [Method("CmdStargateJump")]
    public async ValueTask<PyValue> CmdStargateJump(ServiceCall call) =>
        _binding.Owns(call) && Number(call.Arg(0)) is { } gate
            ? await space.JumpAsync(call.Session, (long)gate, (long)(Number(call.Arg(1)) ?? 0), (long)(Number(call.Arg(2)) ?? 0), call.BoundObjectId, call.Cancellation)
            : None;

    /// <summary>
    /// <c>CmdDock(stationID, shipID)</c>: dock at a station. The answer is the time the docking was accepted; the docking itself is done a few
    /// seconds later, when the client is told the session has changed.
    /// </summary>
    [Method("CmdDock")]
    public async ValueTask<PyValue> CmdDock(ServiceCall call) =>
        _binding.Owns(call) && Number(call.Arg(0)) is { } station ? await space.DockAsync(call.Session, (long)station, call.Cancellation) : None;
}
