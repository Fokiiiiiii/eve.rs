using Eve.Macho;
using Eve.Marshal;

namespace Eve.Services.Space;

/// <summary>
/// The <c>ship</c> service the client binds to for the station it is docked at, and its <c>Undock</c>: the ship leaves the hangar, the
/// character is in space, and the simulation takes over (<see cref="SpaceSessions"/>).
/// </summary>
[Service("ship")]
public sealed partial class ShipService(SpaceSessions space, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _binding.Release(oid);

    /// <summary>The proxy node serves every ship object.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// <c>MachoBindObject((stationID, groupID), (method, args, kwargs))</c>: binds to the place the character is at (its station, or the
    /// solar system it is in space), then makes the nested call.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var place = Binding.PlaceOf(call.Arg(0));
        if (place is null || (call.Session.GetInt("stationid") != place && call.Session.GetInt("solarsystemid") != place))
        {
            return None;
        }
        var (bound, oid) = _binding.Bind(call.Session);
        return Py.Tuple(bound, await Binding.NestedAsync(this, call, oid));
    }

    /// <summary><c>Undock(shipID, ignoreContraband, onlineModules=)</c>: answers the bound object and the time it can be used from again.</summary>
    [Method("Undock")]
    public async ValueTask<PyValue> Undock(ServiceCall call) =>
        _binding.Owns(call) ? await space.UndockAsync(call.Session, call.BoundObjectId, call.Cancellation) : None;
}
