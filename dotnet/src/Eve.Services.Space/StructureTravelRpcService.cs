using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>
/// RPC surface for structure travel. The host can wire the callback to its travel runtime when
/// structure jumps are supported; current SpaceSessions only handles stargate-to-stargate jumps.
/// </summary>
public sealed class StructureTravelRpcService : IService
{
    private readonly Func<ServiceCall, ValueTask<PyValue>>? _travel;
    private readonly Func<long, CancellationToken, ValueTask<long?>>? _jumpBridgeDestination;
    private readonly Func<long, CancellationToken, ValueTask<(long StructureId, long SolarSystemId)?>>? _linkedStructure;

    public StructureTravelRpcService(
        string name,
        Func<ServiceCall, ValueTask<PyValue>>? travel = null,
        Func<long, CancellationToken, ValueTask<long?>>? jumpBridgeDestination = null,
        Func<long, CancellationToken, ValueTask<(long StructureId, long SolarSystemId)?>>? linkedStructure = null)
    {
        if (name is not ("structureCynoBeaconMgr" or "structureJumpBridgeMgr"))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown structure travel service.");

        Name = name;
        _travel = travel;
        _jumpBridgeDestination = jumpBridgeDestination;
        _linkedStructure = linkedStructure;
    }

    public string Name { get; }

    public bool HasMethod(string method) => Name switch
    {
        "structureCynoBeaconMgr" => method is
            "CmdBridgeToStructureBeacon" or
            "CmdGroupJumpToStructureBeacon" or
            "CmdJumpToStructureBeacon",
        "structureJumpBridgeMgr" => method is
            "CmdJumpThroughStructureStargate" or
            "GetJbStructureDestination" or
            "GetLinkedStructure",
        _ => false,
    };

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        if (Name == "structureJumpBridgeMgr" && call.Method is "GetJbStructureDestination" or "GetLinkedStructure")
        {
            var structureId = call.Arg(0) switch
            {
                PyInt id when id.Value > 0 => id.Value,
                PyLong id when id.Value > 0 => id.Value,
                _ => 0,
            };
            if (structureId <= 0)
                return PyNone.Instance;

            if (call.Method == "GetJbStructureDestination")
            {
                var destination = _jumpBridgeDestination is { } getDestination
                    ? await getDestination(structureId, call.Cancellation).ConfigureAwait(false)
                    : null;
                return destination is > 0 ? new PyInt(destination.Value) : PyNone.Instance;
            }

            var linked = _linkedStructure is { } getLinked
                ? await getLinked(structureId, call.Cancellation).ConfigureAwait(false)
                : null;
            return linked is { StructureId: > 0, SolarSystemId: > 0 } pair
                ? new PyList([new PyInt(pair.StructureId), new PyInt(pair.SolarSystemId)])
                : PyNone.Instance;
        }

        if (_travel is null)
        {

            var text = Name == "structureCynoBeaconMgr"
                ? "A Pharolux or Upwell cynosural beacon cannot be jumped to: the structure service that keeps the beacon's state (the Upwell structure phase) is not built on this server."
                : "Structure travel is not wired on this server.";
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
        }

        return await _travel(call).ConfigureAwait(false);
    }
}
