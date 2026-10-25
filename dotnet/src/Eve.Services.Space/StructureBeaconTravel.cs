using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>What the cyno beacon travel needs of a structure: its type, its system and whether its Cyno Beacon service is online.</summary>
public sealed record StructureBeaconInfo(int TypeId, long SolarSystemId, bool ServiceOnline);

/// <summary>Checks persisted beacon state and delegates the accepted operation to its existing travel runtime.</summary>
public sealed class StructureBeaconTravel
{
    public const int PharoluxCynoBeaconType = 35840;

    private readonly Func<long, CancellationToken, ValueTask<StructureBeaconInfo?>> _beacon;
    private readonly Func<long, CancellationToken, ValueTask<bool>> _jammed;
    private readonly Func<long, long, long, CancellationToken, ValueTask<bool>> _hasService;
    private readonly Func<ClientSession, long, int, int, string?, CancellationToken, Task<PyValue>> _jump;
    private readonly Func<ServiceCall, long, StructureBeaconInfo, ValueTask<PyValue>>? _bridge;
    private readonly Func<ServiceCall, long, StructureBeaconInfo, ValueTask<PyValue>>? _groupJump;

    public StructureBeaconTravel(
        Func<long, CancellationToken, ValueTask<StructureBeaconInfo?>> beacon,
        Func<long, CancellationToken, ValueTask<bool>> jammed,
        Func<long, long, long, CancellationToken, ValueTask<bool>> hasService,
        Func<ClientSession, long, int, int, string?, CancellationToken, Task<PyValue>> jump,
        Func<ServiceCall, long, StructureBeaconInfo, ValueTask<PyValue>>? bridge = null,
        Func<ServiceCall, long, StructureBeaconInfo, ValueTask<PyValue>>? groupJump = null)
    {
        _beacon = beacon;
        _jammed = jammed;
        _hasService = hasService;
        _jump = jump;
        _bridge = bridge;
        _groupJump = groupJump;
    }

    public async ValueTask<PyValue> TravelAsync(ServiceCall call)
    {
        if (call.Method is not ("CmdJumpToStructureBeacon" or "CmdBridgeToStructureBeacon" or "CmdGroupJumpToStructureBeacon"))
            return PyNone.Instance;

        var structureId = Id(call.Arg(0));
        var requestedSystem = Id(call.Arg(1));
        if (structureId <= 0 || requestedSystem <= 0 ||
            await _beacon(structureId, call.Cancellation) is not { } beacon ||
            beacon.TypeId != PharoluxCynoBeaconType)
            throw Notice("That cynosural beacon is no longer available.");
        if (beacon.SolarSystemId != requestedSystem)
            throw Notice("That cynosural beacon is not in the requested solar system.");
        if (call.Session.CharacterId is not { } characterId ||
            (call.Session.GetInt("solarsystemid2") ?? call.Session.GetInt("solarsystemid")) is not { } here || here <= 0)
            throw Notice("You must be in space to use a cynosural beacon.");
        if (here == beacon.SolarSystemId)
            throw Notice("You are already in that solar system.");
        if (!beacon.ServiceOnline)
            throw Notice("That cynosural beacon service is offline.");
        if (await _jammed(beacon.SolarSystemId, call.Cancellation))
            throw Notice("That solar system is cynosural jammed.");
        if (!await _hasService(characterId, structureId, Eve.Structures.StructureServiceIds.CynoBeacon, call.Cancellation))
            throw Notice("You do not have access to use that cynosural beacon.");

        return call.Method switch
        {
            "CmdJumpToStructureBeacon" => await _jump(
                call.Session,
                structureId,
                beacon.TypeId,
                checked((int)beacon.SolarSystemId),
                call.BoundObjectId,
                call.Cancellation),
            "CmdBridgeToStructureBeacon" => await (_bridge ?? throw Notice("Structure cyno bridging is not available."))(
                call, structureId, beacon),
            "CmdGroupJumpToStructureBeacon" => await (_groupJump ?? throw Notice("Structure group jumps are not available."))(
                call, structureId, beacon),
            _ => PyNone.Instance,
        };
    }

    private static long Id(PyValue? value) => value switch
    {
        PyInt id when id.Value > 0 => id.Value,
        PyLong id when id.Value > 0 => id.Value,
        _ => 0,
    };

    private static Exception Notice(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
