using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;

namespace Eve.Services.Space;

public sealed record ShipFittingInfo(string Name, string Icon, long ShipTypeId, PyValue? Insurance);

/// <summary>
/// The <c>ship</c> service the client binds to for the station it is docked at, and its <c>Undock</c>: the ship leaves the hangar, the
/// character is in space, and the simulation takes over (<see cref="SpaceSessions"/>).
/// </summary>
[Service("ship")]
public sealed partial class ShipService(
    SpaceSessions space,
    Func<long> nowUnixMs,
    ISpaceContainers? containers = null,
    ISpaceDrones? drones = null,
    Func<long, CancellationToken, ValueTask<ShipFittingInfo?>>? getShipFittingInfo = null,
    CharacterShips? ships = null,
    Func<Eve.Inventory.Item, ItemRow>? rowOf = null) : IBoundObjectReleaseHandler
{
    private readonly Binding _binding = new(nowUnixMs);

    private static PyValue None => PyNone.Instance;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _binding.Release(oid);

    /// <summary>Returns the current EVE FILETIME when a ship has no stored dirt record yet.</summary>
    [Method("GetDirtTimestamp")]
    public PyValue GetDirtTimestamp(ServiceCall call) =>
        new PyLong(checked(116444736000000000L + nowUnixMs() * 10_000L));

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

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>
    /// <c>AssembleShip(itemIDs, name=, subSystems=)</c> on the station's ship moniker (<c>invItemFunctions.AssembleShip</c>): the packaged ships of the hangar become
    /// assembled, and a Strategic Cruiser takes the subsystems the client chose (the reference's rules: <see cref="CharacterShips.AssembleAsync"/>). Answers the id of
    /// the ship assembled, a list of their ids for several, or None when none was. The name is given to a single ship only.
    /// </summary>
    [Method("AssembleShip")]
    public async ValueTask<PyValue> AssembleShip(ServiceCall call)
    {
        if (ships is null || rowOf is null || !_binding.Owns(call) || call.Session.CharacterId is not { } character
            || call.Session.GetInt("stationid") is not { } station)
        {
            return None;
        }
        var shipIds = SpaceDrones.IdsOf(call.Arg(0));
        var subsystemIds = SpaceDrones.IdsOf(call.Kwarg("subSystems") ?? call.Arg(2));
        var name = (call.Kwarg("name") ?? call.Arg(1)) switch
        {
            PyStr text => text.Value,
            PyWString text => text.Value,
            _ => null,
        };
        var changes = await ships.AssembleAsync(character, station, shipIds, subsystemIds, shipIds.Count == 1 ? name : null, call.Cancellation);
        var fileTime = Py.FileTimeFromUnixMs(nowUnixMs());
        foreach (var changed in changes)
        {
            ShipNotifications.Send(call.Session, character, changed, rowOf, fileTime);
            foreach (var stack in changed.Stacks ?? [])
            {
                ShipNotifications.SendItemChange(call.Session, stack, rowOf);
            }
        }
        return changes.Count switch
        {
            0 => None,
            1 => InventoryWire.Id(changes[0].ItemId),
            _ => new PyList([.. changes.Select(change => InventoryWire.Id(change.ItemId))]),
        };
    }

    /// <summary><c>GetShipFittingInfo(fittingID)</c> returns name, icon, hull type and insurance in the fitting UI's tuple shape.</summary>
    [Method("GetShipFittingInfo")]
    public async ValueTask<PyValue> GetShipFittingInfo(ServiceCall call)
    {
        var fittingId = Math.Max(0, IdOf(call.Arg(0)) ?? 0);
        var fitting = fittingId > 0 && getShipFittingInfo is not null
            ? await getShipFittingInfo(fittingId, call.Cancellation)
            : null;
        fitting ??= new ShipFittingInfo(
            $"Fitting {fittingId}",
            "res:/UI/Texture/classes/Fitting/tabFittings.png",
            0,
            PyNone.Instance);
        return Py.Tuple(
            new PyStr(fitting.Name),
            new PyStr(fitting.Icon),
            new PyInt(fitting.ShipTypeId),
            fitting.Insurance ?? PyNone.Instance);
    }

    /// <summary>
    /// <c>Jettison(itemIDs)</c>: the stacks of the hold go into a can ahead of the ship. Answers <c>(the ids that went into a can, the ids launched as cans of their own)</c>; the client
    /// counts what is in neither as not jettisoned.
    /// </summary>
    [Method("Jettison")]
    public async ValueTask<PyValue> Jettison(ServiceCall call)
    {
        if (containers is null || !_binding.Owns(call))
        {
            return None;
        }
        var asked = (call.Arg(0) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] }).Select(IdOf).OfType<long>().ToList();
        var jettisoned = await containers.JettisonAsync(call.Session, asked, call.Cancellation);
        return Py.Tuple(Py.Tuple([.. jettisoned.Select(InventoryWire.Id)]), Py.Tuple());
    }

    /// <summary>
    /// <c>LaunchDrones([(itemID, quantity)], ...)</c>: drones go out of the drone bay. Answers a dictionary of item to the ids of the drones that went out and the refusals of the rest
    /// (<c>("CustomNotify", {notify})</c>); an empty one when the ship is not in space.
    /// </summary>
    [Method("LaunchDrones")]
    public async ValueTask<PyValue> LaunchDrones(ServiceCall call)
    {
        if (drones is null || !_binding.Owns(call))
        {
            return None;
        }
        var asked = (call.Arg(0) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] })
            .Select(entry => entry switch { PyTuple t => t.Items, PyList l => l.Items, var other => [other] })
            .Where(parts => parts.Count > 0 && IdOf(parts[0]) is > 0)
            .Select(parts => new DroneRequest(IdOf(parts[0])!.Value, parts.Count > 1 && IdOf(parts[1]) is { } quantity ? (int)Math.Clamp(quantity, 1, int.MaxValue) : 1))
            .ToList();
        return await drones.LaunchAsync(call.Session, asked, call.Cancellation);
    }

    /// <summary><c>LaunchFromShip(itemIDs)</c>: the mobile warp disruptors of the hold go out into space. Answers a tuple of the ids that went out and an empty list of refusals
    /// (a refusal is the client's notice).</summary>
    [Method("LaunchFromShip")]
    public async ValueTask<PyValue> LaunchFromShip(ServiceCall call)
    {
        if (containers is null || !_binding.Owns(call))
        {
            return None;
        }
        var launched = new List<PyValue>();
        foreach (var id in SpaceDrones.IdsOf(call.Arg(0)))
        {
            if (await containers.LaunchFromShipAsync(call.Session, id, call.Cancellation) is { } disruptor)
            {
                launched.Add(new PyLong(disruptor));
            }
        }
        return Py.Tuple(new PyList(launched), new PyList(new List<PyValue>()));
    }

    /// <summary><c>ScoopDrone(droneIDs)</c>: drones that nobody commands are taken into the drone bay. Answers a dictionary of the drones that could not be.</summary>
    [Method("ScoopDrone")]
    public async ValueTask<PyValue> ScoopDrone(ServiceCall call) =>
        drones is null || !_binding.Owns(call) ? None : await drones.ScoopAsync(call.Session, SpaceDrones.IdsOf(call.Arg(0)), call.Cancellation);

    /// <summary><c>Undock(shipID, ignoreContraband, onlineModules=)</c>: answers the bound object and the time it can be used from again.</summary>
    /// <summary>The client call <c>Board(shipID, oldShipID)</c> used by its active-ship switch.</summary>
    /// <summary>The client call <c>LeaveShip(shipID)</c> returns the capsule made active by the session.</summary>
    [Method("Undock")]
    public async ValueTask<PyValue> Undock(ServiceCall call) =>
        _binding.Owns(call) ? await space.UndockAsync(call.Session, call.BoundObjectId, call.Cancellation) : None;

    /// <summary><c>Board(shipID, currentShipID)</c>: selects an owned ship in the station hangar.</summary>
    [Method("Board")]
    public async ValueTask<PyValue> Board(ServiceCall call)
    {
        if (!_binding.Owns(call) || IdOf(call.Arg(0)) is not { } shipId || IdOf(call.Arg(1)) is not { } currentShipId)
        {
            return None;
        }
        return await space.BoardAsync(call.Session, shipId, currentShipId, call.Cancellation);
    }

    /// <summary><c>LeaveShip(shipID)</c>: makes a capsule available for the client's following Board call.</summary>
    [Method("LeaveShip")]
    public async ValueTask<PyValue> LeaveShip(ServiceCall call)
    {
        if (!_binding.Owns(call) || IdOf(call.Arg(0)) is not { } shipId || shipId <= 0)
        {
            return None;
        }
        return await space.LeaveShipAsync(call.Session, shipId, call.Cancellation);
    }


    /// <summary><c>SafeLogoff()</c> starts the space logoff countdown when all server-side conditions allow it.</summary>
    [Method("SafeLogoff")]
    public ValueTask<PyValue> SafeLogoff(ServiceCall call) =>
        _binding.Owns(call) ? space.SafeLogoffAsync(call.Session, call.Cancellation) : ValueTask.FromResult<PyValue>(None);

    /// <summary><c>AbortSafeLogoff()</c> cancels a pending safe logoff countdown.</summary>
    [Method("AbortSafeLogoff")]
    public PyValue AbortSafeLogoff(ServiceCall call) =>
        _binding.Owns(call) ? space.AbortSafeLogoff(call.Session) : None;


    /// <summary><c>Eject()</c> leaves the piloted hull in space and continues in a capsule.</summary>
    [Method("Eject")]
    public ValueTask<PyValue> Eject(ServiceCall call) =>
        _binding.Owns(call) ? space.EjectAsync(call.Session, call.Cancellation) : ValueTask.FromResult<PyValue>(None);
}
