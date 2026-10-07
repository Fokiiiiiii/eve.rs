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

    public StructureTravelRpcService(string name, Func<ServiceCall, ValueTask<PyValue>>? travel = null)
    {
        if (name is not ("structureCynoBeaconMgr" or "structureJumpBridgeMgr"))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown structure travel service.");

        Name = name;
        _travel = travel;
    }

    public string Name { get; }

    public bool HasMethod(string method) => Name switch
    {
        "structureCynoBeaconMgr" => method is
            "CmdBridgeToStructureBeacon" or
            "CmdGroupJumpToStructureBeacon" or
            "CmdJumpToStructureBeacon",
        "structureJumpBridgeMgr" => method is "CmdJumpThroughStructureStargate",
        _ => false,
    };

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        if (_travel is null)
        {
            throw MachoErrors.ThrowUserError(
                "CustomNotify",
                ("notify", new PyStr("Structure travel is not wired on this server.")));
        }

        return await _travel(call).ConfigureAwait(false);
    }
}
