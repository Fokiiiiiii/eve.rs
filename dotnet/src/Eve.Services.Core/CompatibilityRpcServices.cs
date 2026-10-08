using System.Collections.Concurrent;
using Eve.Marshal;

namespace Eve.Services.Core;

public static class ServerBuildInfo
{
    public const double BootVersion = 24.01;
    public const int BootBuild = 3_396_210;
}

// Compatibility-only manual IService stubs. Do not register these in CoreServices.Create;
// Program registers the backed Eve.Host implementations for runtime dispatch.

public sealed class ShipCosmeticsMgrRpcService(
    Func<ServiceCall, ValueTask<PyValue?>>? handler = null) : IService
{
    public string Name => "shipCosmeticsMgr";
    public bool HasMethod(string method) => method is
        "ActivateSkinLicense" or "ApplySkinToShip" or "GMExpireSkinLicense" or
        "GetEnabledCosmetics" or "GetFirstPartySkinData" or "GiveSkin" or "RemoveSkin";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return ValueTask.FromResult<PyValue>(PyNone.Instance);
        return CompatibilityRpc.Invoke(call, handler, () =>
            call.Method == "GetEnabledCosmetics" ? new PyDict([]) : PyNone.Instance);
    }
}

public sealed class DynamicItemRpcService(
    Func<ServiceCall, ValueTask<PyValue?>>? handler = null) : IService
{
    public string Name => "dynamicItemService";
    public bool HasMethod(string method) => method == "CreateDynamicItem";

    public ValueTask<PyValue> CallAsync(ServiceCall call) =>
        !HasMethod(call.Method)
            ? ValueTask.FromResult<PyValue>(PyNone.Instance)
            : CompatibilityRpc.Invoke(call, handler, () => PyNone.Instance);
}

public sealed class CharacterEnergyMgrRpcService(
    Func<long>? nowUnixMs = null,
    Func<ServiceCall, ValueTask<PyValue?>>? handler = null) : IService
{
    private const string StateClass = "characterenergy.characterEnergyState.CharacterEnergyState";
    public string Name => "characterEnergyMgr";
    public bool HasMethod(string method) => method == "GetMyEnergyState";

    public ValueTask<PyValue> CallAsync(ServiceCall call) =>
        !HasMethod(call.Method)
            ? ValueTask.FromResult<PyValue>(PyNone.Instance)
            : CompatibilityRpc.Invoke(call, handler, State);

    private PyValue State()
    {
        var fileTime = Py.FileTimeFromUnixMs((nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))());
        return new PyObjectEx(ObjectExKind.Ex1,
            PyTuple.Of(
                new PyToken(StateClass),
                PyTuple.Of(
                    new PyReal(100),
                    new PyLong(fileTime),
                    new PyReal(1),
                    new PyReal(792),
                    new PyReal(0),
                    new PyReal(100))),
            [], []);
    }
}

public sealed class PaperDollServerRpcService
{
    private readonly Func<long, CancellationToken, Task<(byte[]? Doll, byte[]? Portrait)>>? _appearance;
    private readonly Func<long, CancellationToken, ValueTask<PyValue?>>? _portrait;
    private readonly Func<ServiceCall, ValueTask<PyValue?>>? _update;

    public PaperDollServerRpcService(
        Func<long, CancellationToken, Task<(byte[]? Doll, byte[]? Portrait)>>? appearance = null,
        Func<long, CancellationToken, ValueTask<PyValue?>>? portrait = null,
        Func<ServiceCall, ValueTask<PyValue?>>? update = null)
    {
        _appearance = appearance;
        _portrait = portrait;
        _update = update;
    }

    public string Name => "paperDollServer";
    public bool HasMethod(string method) => method is
        "GetPaperDollData" or "GetPaperDollDataBatch" or "GetPaperDollPortraitDataFor" or
        "UpdateExistingCharacterFull" or "UpdateExistingCharacterLimited";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        switch (call.Method)
        {
            case "GetPaperDollData":
            {
                var id = CharacterId(call);
                if (id <= 0 || _appearance is null) return PyNone.Instance;
                var (doll, _) = await _appearance(id, call.Cancellation);
                return Decode(doll);
            }
            case "GetPaperDollDataBatch":
            {
                var result = new List<KeyValuePair<PyValue, PyValue>>();
                foreach (var id in Ids(call.Arg(0)).Distinct())
                {
                    PyValue value = PyNone.Instance;
                    if (id > 0 && _appearance is not null)
                    {
                        var (doll, _) = await _appearance(id, call.Cancellation);
                        value = Decode(doll);
                    }
                    result.Add(new(InventoryWire.Id(id), value));
                }
                return new PyDict(result);
            }
            case "GetPaperDollPortraitDataFor":
            {
                var id = CharacterId(call);
                if (id > 0 && _portrait is not null &&
                    await _portrait(id, call.Cancellation) is { } portrait)
                    return portrait;
                return PyTuple.Of(new PyList([]), PyNone.Instance);
            }
            case "UpdateExistingCharacterFull":
            case "UpdateExistingCharacterLimited":
                if (_update is null)
                    return PyNone.Instance;
                return await _update(call) ?? PyNone.Instance;
            default:
                return PyNone.Instance;
        }
    }

    private static PyValue Decode(byte[]? bytes) =>
        bytes is { Length: > 0 } ? MarshalCodec.Decode(bytes) : PyNone.Instance;

    private static long CharacterId(ServiceCall call) =>
        Positive(call.Arg(0), call.Session.GetInt("charid") ?? 0);

    private static IEnumerable<long> Ids(PyValue value) => value switch
    {
        PyList list => list.Items.Select(item => Positive(item)),
        PyTuple tuple => tuple.Items.Select(item => Positive(item)),
        _ => [],
    };

    private static long Positive(PyValue value) => value switch
    {
        PyInt number => Math.Max(0, number.Value),
        PyLong number => Math.Max(0, number.Value),
        PyReal number when double.IsFinite(number.Value) => Math.Max(0, (long)number.Value),
        _ => 0,
    };

    private static long Positive(PyValue value, long fallback)
    {
        var id = Positive(value);
        return id > 0 ? id : fallback;
    }
}

public sealed class PlanetMgrRpcService(
    Func<ServiceCall, ValueTask<PyValue?>>? handler = null) : IService
{
    private const string CRowsetName = "carbon.common.script.sys.crowset.CRowset";
    private static readonly (string Name, int Type)[] LaunchColumns =
    [
        ("launchID", 3), ("solarSystemID", 3), ("itemID", 20), ("ownerID", 3), ("planetID", 3),
        ("status", 17), ("launchTime", 64), ("x", 5), ("y", 5), ("z", 5),
    ];
    private static readonly (string Name, int Type)[] PlanetColumns =
    [
        ("solarSystemID", 3), ("planetID", 3), ("typeID", 3), ("numberOfPins", 3), ("celestialIndex", 3),
    ];

    public string Name => "planetMgr";
    public bool HasMethod(string method) => method is "DeleteLaunch" or "GetMyLaunchesDetails" or "GetPlanetsForChar";

    public ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return ValueTask.FromResult<PyValue>(PyNone.Instance);
        return CompatibilityRpc.Invoke(call, handler, () => call.Method switch
        {
            "DeleteLaunch" => PyBool.True,
            "GetMyLaunchesDetails" => EmptyCRowset(LaunchColumns),
            "GetPlanetsForChar" => EmptyCRowset(PlanetColumns),
            _ => PyNone.Instance,
        });
    }

    private static PyObjectEx EmptyCRowset(IReadOnlyList<(string Name, int Type)> columns)
    {
        var packedColumns = Py.Tuple([.. columns.Select(column =>
            (PyValue)Py.Tuple(new PyStr(column.Name), new PyInt(column.Type)))]);
        var descriptor = new PyObjectEx(
            ObjectExKind.Ex1,
            Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(packedColumns)),
            [], []);
        var header = Py.Tuple(
            Py.Tuple(new PyToken(CRowsetName)),
            Py.Dict(("header", descriptor)));
        return new PyObjectEx(ObjectExKind.Ex2, header, [], []);
    }
}

internal static class CompatibilityRpc
{
    public static async ValueTask<PyValue> Invoke(
        ServiceCall call,
        Func<ServiceCall, ValueTask<PyValue?>>? handler,
        Func<PyValue> fallback)
    {
        call.Cancellation.ThrowIfCancellationRequested();
        return handler is null
            ? fallback()
            : await handler(call) ?? fallback();
    }
}
