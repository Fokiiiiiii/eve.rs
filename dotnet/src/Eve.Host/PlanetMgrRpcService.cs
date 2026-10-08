using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Services;

namespace Eve.Host;

public sealed class PlanetMgrRpcService : IService
{
    private static readonly string[] PlanetColumns = ["solarSystemID", "planetID", "typeID", "numberOfPins", "celestialIndex"];
    private static readonly long[] PlanetTypes = [3, 3, 3, 3, 3];
    private static readonly string[] LaunchColumns = ["launchID", "solarSystemID", "itemID", "ownerID", "planetID", "status", "launchTime", "x", "y", "z"];
    private static readonly long[] LaunchTypes = [3, 3, 20, 3, 3, 17, 64, 5, 5, 5];
    private static readonly Descriptor PlanetDescriptor = MakeDescriptor(PlanetColumns, PlanetTypes);
    private static readonly Descriptor LaunchDescriptor = MakeDescriptor(LaunchColumns, LaunchTypes);
    private readonly GameStoreCache? _store;
    private readonly SdeReader? _sde;
    private readonly Func<long> _now;

    public PlanetMgrRpcService(GameStoreCache? store = null, SdeReader? sde = null, Func<long>? nowUnixMs = null)
        => (_store, _sde, _now) = (store, sde, nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public string Name => "planetMgr";
    public bool HasMethod(string method) => method is "DeleteLaunch" or "GetMyLaunchesDetails" or "GetPlanetsForChar";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        return call.Method switch
        {
            "DeleteLaunch" => DeleteLaunch(call),
            "GetMyLaunchesDetails" => GetMyLaunchesDetails(call),
            "GetPlanetsForChar" => await GetPlanetsForCharAsync(call),
            _ => PyNone.Instance,
        };
    }

    private PyValue DeleteLaunch(ServiceCall call)
    {
        var launchId = Positive(call.Arg(0));
        if (launchId <= 0) return PyBool.False;
        var path = $"/launchesByID/{launchId}";
        if (_store?.Read("planetRuntimeState", path).Data is not JsonObject launch) return PyBool.True;
        var ownerId = Long(launch["ownerID"]);
        if (call.Session.CharacterId is { } characterId && ownerId > 0 && ownerId != characterId) return PyBool.False;
        launch["deleted"] = true;
        launch["deletedAt"] = NowIso();
        _store.Write("planetRuntimeState", path, launch);
        return PyBool.True;
    }

    private PyValue GetMyLaunchesDetails(ServiceCall call)
    {
        var ownerId = call.Session.CharacterId ?? 0;
        var launches = _store?.Read("planetRuntimeState", "/launchesByID").Data as JsonObject;
        var rows = new List<PyValue>();
        if (launches is not null && ownerId > 0)
            foreach (var launch in launches.Select(pair => pair.Value).OfType<JsonObject>())
                if (!Boolean(launch["deleted"]) && Long(launch["ownerID"]) == ownerId)
                    rows.Add(new PyPackedRow(LaunchDescriptor, LaunchValues(launch)));
        return CRowset(LaunchDescriptor, rows);
    }

    private async ValueTask<PyValue> GetPlanetsForCharAsync(ServiceCall call)
    {
        var ownerId = call.Session.CharacterId ?? 0;
        var colonies = _store?.Read("planetRuntimeState", "/coloniesByKey").Data as JsonObject;
        var selected = colonies is null || ownerId <= 0 ? [] : colonies.Select(pair => pair.Value).OfType<JsonObject>()
            .Where(colony => Long(colony["ownerID"]) == ownerId).ToArray();
        var planetIds = selected.Select(colony => Long(colony["planetID"])).Where(id => id > 0).Distinct().ToArray();
        var metadata = _sde is null || planetIds.Length == 0
            ? new Dictionary<long, JsonElement>()
            : (await _sde.ReadAsync("mapPlanets", planetIds, call.Cancellation)).ToDictionary(row => row.Key, row => row.Doc);
        var rows = new List<PyValue>();
        foreach (var colony in selected)
        {
            var planetId = Long(colony["planetID"]);
            metadata.TryGetValue(planetId, out var meta);
            var systemId = Long(colony["solarSystemID"] ?? colony["systemID"]);
            if (systemId <= 0) systemId = JsonLong(meta, "solarSystemID");
            var typeId = Long(colony["typeID"] ?? colony["planetTypeID"]);
            if (typeId <= 0) typeId = JsonLong(meta, "typeID");
            var pinCount = Long(colony["numberOfPins"] ?? colony["pinCount"]);
            if (pinCount <= 0) pinCount = (colony["pins"] as JsonArray)?.Count ?? 0;
            var celestialIndex = Long(colony["celestialIndex"]);
            if (celestialIndex <= 0) celestialIndex = JsonLong(meta, "celestialIndex");
            rows.Add(new PyPackedRow(PlanetDescriptor,
                [new PyInt(systemId), new PyInt(planetId), new PyInt(typeId), new PyInt(pinCount), new PyInt(celestialIndex)]));
        }
        return CRowset(PlanetDescriptor, rows);
    }

    private PyValue[] LaunchValues(JsonObject launch)
    {
        var launchId = Long(launch["launchID"] ?? launch["itemID"]);
        var itemId = Long(launch["itemID"] ?? launch["launchID"]);
        var launchTime = Long(launch["launchTime"]);
        if (launchTime <= 0) launchTime = Py.FileTimeFromUnixMs(_now());
        return [new PyInt(launchId), new PyInt(Long(launch["solarSystemID"])), new PyLong(itemId),
            new PyInt(Long(launch["ownerID"])), new PyInt(Long(launch["planetID"])), PyValue.Of(Boolean(launch["status"])),
            new PyLong(launchTime), Py.Number(Number(launch["x"])), Py.Number(Number(launch["y"])), Py.Number(Number(launch["z"]))];
    }

    private static PyValue CRowset(Descriptor descriptor, IReadOnlyList<PyValue> rows) => new PyObjectEx(ObjectExKind.Ex2,
        Py.Tuple(Py.Tuple(new PyToken("carbon.common.script.sys.crowset.CRowset")), Py.Dict(("header", descriptor.Header))),
        rows, []);

    private static Descriptor MakeDescriptor(string[] columns, long[] types) => Descriptor.FromHeader(new PyObjectEx(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("blue.DBRowDescriptor"), Py.Tuple(Py.Tuple([.. columns.Select((name, index) =>
            (PyValue)Py.Tuple(new PyStr(name), new PyInt(types[index])))]))), [], []));

    private static long JsonLong(JsonElement doc, string name) => doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
    private string NowIso() => DateTimeOffset.FromUnixTimeMilliseconds(_now()).ToString("O", CultureInfo.InvariantCulture);
    private static long Long(JsonNode? node) => long.TryParse(node?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    private static double Number(JsonNode? node) => double.TryParse(node?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : 0;
    private static bool Boolean(JsonNode? node) => node?.GetValueKind() == JsonValueKind.True || (node?.GetValueKind() == JsonValueKind.Number && Long(node) != 0);
    private static long Positive(PyValue value) => Math.Max(0, value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r => (long)r.Value, _ => 0 });
}
