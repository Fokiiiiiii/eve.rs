using Eve.Macho;
using Eve.Marshal;
using Eve.Space;

namespace Eve.Services.Space;

/// <summary>
/// The <c>entity</c> service the client binds to in space: the orders to its drones. The drones are the simulation's (<see cref="SpaceDrones"/>); this reads the client's calls. The reference
/// binds it for whoever asks (it answers the client in the place it is in), so does this.
/// </summary>
[Service("entity")]
public sealed partial class EntityService(ISpaceDrones drones, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _binding.Release(oid);

    /// <summary>The proxy node serves every entity object.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary><c>MachoBindObject(solarSystemID, (method, args, kwargs))</c>: binds, then makes the nested call.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var (bound, oid) = _binding.Bind(call.Session);
        return Py.Tuple(bound, await Binding.NestedAsync(this, call, oid));
    }

    private async ValueTask<PyValue> OrderAsync(ServiceCall call, byte command, long target, PyValue ids) =>
        _binding.Owns(call) ? await drones.CommandAsync(call.Session, command, target, SpaceDrones.IdsOf(ids), call.Cancellation) : None;

    /// <summary><c>CmdReturnHome(droneIDs)</c>: the drones circle their ship.</summary>
    [Method("CmdReturnHome")]
    public ValueTask<PyValue> CmdReturnHome(ServiceCall call) => OrderAsync(call, SimDroneCommands.ReturnHome, 0, call.Arg(0));

    /// <summary><c>CmdReturnBay(droneIDs)</c>: the drones fly to their ship and are taken into the bay.</summary>
    [Method("CmdReturnBay")]
    public ValueTask<PyValue> CmdReturnBay(ServiceCall call) => OrderAsync(call, SimDroneCommands.ReturnBay, 0, call.Arg(0));

    /// <summary><c>CmdEngage(droneIDs, targetID)</c>: the drones go at the target and shoot it.</summary>
    [Method("CmdEngage")]
    public ValueTask<PyValue> CmdEngage(ServiceCall call) =>
        OrderAsync(call, SimDroneCommands.Engage, call.Arg(1) switch { PyInt n => n.Value, PyLong n => n.Value, _ => 0 }, call.Arg(0));

    private const string NotServed = "That drone order is not available yet.";

    /// <summary><c>CmdAssist(characterID, droneIDs)</c> and <c>CmdGuard(characterID, droneIDs)</c> (their subject is a pilot: the arguments are the other way round from the rest): not served yet, each drone refuses.</summary>
    [Method("CmdAssist")]
    public ValueTask<PyValue> CmdAssist(ServiceCall call) => new(_binding.Owns(call) ? SpaceDrones.RefuseAll(SpaceDrones.IdsOf(call.Arg(1)), NotServed) : None);

    [Method("CmdGuard")]
    public ValueTask<PyValue> CmdGuard(ServiceCall call) => new(_binding.Owns(call) ? SpaceDrones.RefuseAll(SpaceDrones.IdsOf(call.Arg(1)), NotServed) : None);

    /// <summary><c>CmdMineRepeatedly(droneIDs, targetID)</c> and <c>CmdSalvage(droneIDs, targetID)</c>: not served yet (only drones that shoot fly), each drone refuses.</summary>
    [Method("CmdMineRepeatedly")]
    public ValueTask<PyValue> CmdMineRepeatedly(ServiceCall call) => new(_binding.Owns(call) ? SpaceDrones.RefuseAll(SpaceDrones.IdsOf(call.Arg(0)), NotServed) : None);

    [Method("CmdSalvage")]
    public ValueTask<PyValue> CmdSalvage(ServiceCall call) => new(_binding.Owns(call) ? SpaceDrones.RefuseAll(SpaceDrones.IdsOf(call.Arg(0)), NotServed) : None);

    /// <summary><c>CmdReconnectToDrones(droneIDs)</c>: drones that were let go and are still in space are taken up again; none are (a drone that is let go leaves the scene), so nothing is refused or done.</summary>
    [Method("CmdReconnectToDrones")]
    public ValueTask<PyValue> CmdReconnectToDrones(ServiceCall call) => new(_binding.Owns(call) ? new PyDict([]) : None);

    /// <summary><c>CmdAbandonDrone(droneIDs)</c>: the drones are let go.</summary>
    [Method("CmdAbandonDrone")]
    public ValueTask<PyValue> CmdAbandonDrone(ServiceCall call) => OrderAsync(call, SimDroneCommands.Abandon, 0, call.Arg(0));
}
