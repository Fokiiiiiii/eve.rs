using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Space;

/// <summary>What the cyno beacon travel needs of a structure: its type, its system and whether its Cyno Beacon service is online.</summary>
public sealed record StructureBeaconInfo(int TypeId, long SolarSystemId, bool ServiceOnline);

/// <summary>
/// The structure cyno beacon (<c>structureCynoBeaconMgr</c>, EveJS <c>structureCynoBeaconMgrService.js</c> <c>resolveUsableBeacon</c>): the checks the reference
/// makes before a jump to a Pharolux beacon, each with its notice. A beacon that passes every check is jumped through the jump drive (SpaceJumpDrive).
/// </summary>
public sealed class StructureBeaconTravel
{
    public const int PharoluxCynoBeaconType = 35840;

    private readonly Func<long, CancellationToken, ValueTask<StructureBeaconInfo?>> _beacon;
    private readonly Func<long, CancellationToken, ValueTask<bool>> _jammed;
    private readonly Func<long, long, long, CancellationToken, ValueTask<bool>> _hasService;
    private readonly Func<ClientSession, long, int, int, string?, CancellationToken, Task<PyValue>> _jump;

    public StructureBeaconTravel(
        Func<long, CancellationToken, ValueTask<StructureBeaconInfo?>> beacon,
        Func<long, CancellationToken, ValueTask<bool>> jammed,
        Func<long, long, long, CancellationToken, ValueTask<bool>> hasService,
        Func<ClientSession, long, int, int, string?, CancellationToken, Task<PyValue>> jump)
    {
        _beacon = beacon;
        _jammed = jammed;
        _hasService = hasService;
        _jump = jump;
    }

    public async ValueTask<PyValue> TravelAsync(ServiceCall call)
    {
        if (call.Method != "CmdJumpToStructureBeacon")
            throw Notice("Group jumps and bridges to structure cynosural beacons are not supported.");

        var structureId = Id(call.Arg(0));
        var requestedSystem = Id(call.Arg(1));
        if (structureId <= 0 || await _beacon(structureId, call.Cancellation) is not { } beacon || beacon.TypeId != PharoluxCynoBeaconType)
            throw Notice("That cynosural beacon is no longer available.");
        if (requestedSystem > 0 && beacon.SolarSystemId != requestedSystem)
            throw Notice("That cynosural beacon is not in the requested solar system.");
        if (call.Session.CharacterId is not { } characterId ||
            call.Session.GetInt("solarsystemid2") is not { } here || here <= 0)
            throw Notice("You must be in space to use a cynosural beacon.");
        if (here == beacon.SolarSystemId)
            throw Notice("You are already in that solar system.");
        if (!beacon.ServiceOnline)
            throw Notice("That cynosural beacon service is offline.");
        if (await _jammed(beacon.SolarSystemId, call.Cancellation))
            throw Notice("That solar system is cynosural jammed.");
        if (!await _hasService(characterId, structureId, Eve.Structures.StructureServiceIds.CynoBeacon, call.Cancellation))
            throw Notice("You do not have access to use that cynosural beacon.");

        return await _jump(
            call.Session,
            structureId,
            beacon.TypeId,
            checked((int)beacon.SolarSystemId),
            call.BoundObjectId,
            call.Cancellation);
    }

    private static long Id(PyValue? value) => value switch
    {
        PyInt id => id.Value,
        PyLong id => id.Value,
        _ => 0,
    };

    private static Exception Notice(string text) => MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(text)));
}
