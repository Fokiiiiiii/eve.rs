using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Universe;

namespace Eve.Services.Space;

public sealed record ShipcasterLandingPad(
    long ItemId,
    int SolarSystemId,
    int FactionId,
    bool IsBuilt = false,
    PyValue? LinkTimestamp = null,
    bool IsDisrupted = false);

public sealed class ShipcasterRpcService : IService
{
    private const string LandingPadDataClass = "shipcaster.landingPadData.LandingPadData";
    private static readonly int[] KnownFactionIds = [500001, 500002, 500003, 500004, 500010, 500011];

    private readonly SolarSystemCatalog _solarSystems;
    private readonly Func<IReadOnlyList<ShipcasterLandingPad>>? _readLandingPads;
    private readonly Func<IReadOnlyList<int>>? _readFactionsWithShipcaster;
    private readonly Func<ServiceCall, ValueTask<PyValue>>? _travel;
    private readonly Func<ServiceCall, bool>? _canCharacterJump;

    public ShipcasterRpcService(
        string name,
        SolarSystemCatalog solarSystems,
        Func<IReadOnlyList<ShipcasterLandingPad>>? readLandingPads = null,
        Func<IReadOnlyList<int>>? readFactionsWithShipcaster = null,
        Func<ServiceCall, ValueTask<PyValue>>? travel = null,
        Func<ServiceCall, bool>? canCharacterJump = null)
    {
        if (name is not ("shipCasterLandingPadMgr" or "shipCasterLauncherMgr" or "shipcasterTravelMgr" or "starterShipcasterTravelMgr"))
            throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown shipcaster service.");

        Name = name;
        _solarSystems = solarSystems;
        _readLandingPads = readLandingPads;
        _readFactionsWithShipcaster = readFactionsWithShipcaster;
        _travel = travel;
        _canCharacterJump = canCharacterJump;
    }

    public string Name { get; }

    public bool HasMethod(string method) => Name switch
    {
        "shipCasterLandingPadMgr" => method is "GetAllLandingPads" or "GetFactionLandingPads",
        "shipCasterLauncherMgr" => method is "GetFactionsWithShipcaster",
        "shipcasterTravelMgr" => method is "CmdJumpThroughShipcaster",
        "starterShipcasterTravelMgr" => method is "CanCharacterJump" or "CmdJumpThroughStarterShipcaster",
        _ => false,
    };

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method))
            return PyNone.Instance;

        if (call.Method is "GetAllLandingPads" or "GetFactionLandingPads")
        {
            var factionId = call.Method == "GetAllLandingPads" ? null : FactionId(call.Arg(0));
            return new PyList(LandingPads(factionId).Select(ToWire).ToArray());
        }

        if (call.Method == "GetFactionsWithShipcaster")
        {
            var factions = _readFactionsWithShipcaster is { } readFactions
                ? readFactions()
                : _readLandingPads is not null
                    ? LandingPads(null).Select(pad => pad.FactionId).ToArray()
                    : throw MissingBackend("Shipcaster faction data");
            return new PyList(factions
                .Where(KnownFactionIds.Contains)
                .Distinct()
                .OrderBy(factionId => factionId)
                .Select(factionId => (PyValue)new PyInt(factionId))
                .ToArray());
        }

        if (call.Method == "CanCharacterJump")
            return (_canCharacterJump?.Invoke(call) ?? false) ? PyBool.True : PyBool.False;

        if (_travel is not null)
            return await _travel(call).ConfigureAwait(false);

        var notify = Name == "shipcasterTravelMgr"
            ? "No active shipcaster destination is available."
            : "No starter shipcaster destination is available.";
        throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr(notify)));
    }

    private IReadOnlyList<ShipcasterLandingPad> LandingPads(int? factionId) =>
        (_readLandingPads ?? throw MissingBackend("Shipcaster landing-pad data"))()
        .Where(pad => pad.ItemId > 0 &&
            _solarSystems.Get(pad.SolarSystemId) is not null &&
            KnownFactionIds.Contains(pad.FactionId) &&
            (factionId is null || pad.FactionId == factionId))
        .OrderBy(pad => pad.FactionId)
        .ThenBy(pad => pad.SolarSystemId)
        .ThenBy(pad => pad.ItemId)
        .ToArray();

    private static MachoWrappedException MissingBackend(string backend) => MachoErrors.ThrowUserError(
        "CustomNotify",
        ("notify", new PyStr($"{backend} is not available on this server.")));

    private static PyObjectEx ToWire(ShipcasterLandingPad pad) => new(
        ObjectExKind.Ex1,
        new PyTuple(
        [
            new PyToken(LandingPadDataClass),
            new PyList(
            [
                new PyLong(pad.ItemId),
                new PyInt(pad.SolarSystemId),
                new PyInt(pad.FactionId),
                pad.IsBuilt ? PyBool.True : PyBool.False,
                pad.LinkTimestamp ?? PyNone.Instance,
                pad.IsDisrupted ? PyBool.True : PyBool.False,
            ]),
        ]),
        [],
        []);

    private static int? FactionId(PyValue value)
    {
        var raw = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer when integer.Value is >= int.MinValue and <= int.MaxValue => (int)integer.Value,
            PyReal real when double.IsFinite(real.Value) && real.Value is >= int.MinValue and <= int.MaxValue => (int)Math.Truncate(real.Value),
            PyStr text when int.TryParse(text.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        return raw is >= int.MinValue and <= int.MaxValue && KnownFactionIds.Contains((int)raw)
            ? (int)raw
            : null;
    }
}
