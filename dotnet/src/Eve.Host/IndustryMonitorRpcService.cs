using System.Globalization;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed record IndustryMonitorLocation(long ItemId, long OwnerId, int FlagId, bool CanTake);
public sealed record IndustryMonitorRequest(long BlueprintId, int BlueprintTypeId, int ActivityId, long FacilityId, IndustryMonitorLocation? InputLocation);

public sealed class IndustryMonitorRpcService : IService
{
    private const long FirstMonitorId = 1;
    private const int BlueprintCategoryId = 9;
    private readonly GameStoreCache? _store;
    private readonly PgItemStore? _items;
    private readonly ItemTypeCatalog? _itemTypes;
    private readonly Func<IndustryMonitorRequest, CancellationToken, ValueTask<IReadOnlyDictionary<int, long>>>? _materials;
    private readonly Func<long> _now;
    private readonly object _gate = new();
    private long _nextMonitorId = FirstMonitorId;

    public IndustryMonitorRpcService(GameStoreCache? store = null, PgItemStore? items = null,
        Func<IndustryMonitorRequest, CancellationToken, ValueTask<IReadOnlyDictionary<int, long>>>? availableMaterials = null,
        Func<long>? nowUnixMs = null, ItemTypeCatalog? itemTypes = null)
        => (_store, _items, _itemTypes, _materials, _now) = (store, items, itemTypes, availableMaterials, nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public string Name => "industryMonitor";
    public bool HasMethod(string method) => method is "ConnectJob" or "DisconnectJob";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Method == "DisconnectJob")
        {
            var id = Long(call.Arg(0));
            _store?.Remove("industryRuntime", $"/monitors/{id}");
            return PyNone.Instance;
        }

        var request = Parse(call.Arg(0));
        var monitorId = AllocateId();
        var row = new JsonObject
        {
            ["monitorID"] = monitorId,
            ["activityID"] = request.ActivityId,
            ["blueprintID"] = request.BlueprintId,
            ["createdAt"] = _now(),
        };
        var write = _store?.Write("industryRuntime", $"/monitors/{monitorId}", row);
        if (write is { Success: false }) throw new InvalidOperationException($"Could not save industry monitor: {write.ErrorMsg}");
        var materials = await ReadAvailableMaterialsAsync(request, call.Cancellation);
        return Py.Tuple(new PyInt(monitorId), new PyDict(materials.Select(pair =>
            new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), new PyInt(pair.Value))).ToArray()));
    }

    private long AllocateId()
    {
        lock (_gate)
        {
            var stored = _store?.Read("industryRuntime", "/_meta/nextMonitorID").Data;
            var id = Math.Max(FirstMonitorId, Math.Max(_nextMonitorId, Long(stored)));
            _nextMonitorId = Math.Max(_nextMonitorId, id + 1);
            id = Math.Max(id, _nextMonitorId - 1);
            _nextMonitorId = id + 1;
            _store?.Write("industryRuntime", "/_meta/nextMonitorID", System.Text.Json.Nodes.JsonValue.Create(_nextMonitorId));
            return id;
        }
    }

    private async ValueTask<IReadOnlyDictionary<int, long>> ReadAvailableMaterialsAsync(IndustryMonitorRequest request, CancellationToken cancellationToken)
    {
        if (_materials is not null) return await _materials(request, cancellationToken);
        if (_items is null || _itemTypes is null || request.BlueprintId <= 0 || request.InputLocation is not { CanTake: true, ItemId: > 0 })
            return new Dictionary<int, long>();
        var blueprint = await _items.GetAsync(request.BlueprintId, cancellationToken);
        if (blueprint is null || (request.BlueprintTypeId > 0 && blueprint.TypeId != request.BlueprintTypeId))
            return new Dictionary<int, long>();
        var ownerId = blueprint.OwnerId;
        if (request.InputLocation.OwnerId > 0 && request.InputLocation.OwnerId != ownerId)
            return new Dictionary<int, long>();
        var contents = (await _items.InLocationAsync(request.InputLocation.ItemId, ownerId, cancellationToken))
            .Where(item => item.Flag == request.InputLocation.FlagId)
            .ToArray();
        var itemTypes = await _itemTypes.GetAsync(contents.Select(item => item.TypeId), cancellationToken);
        return contents.Where(item => !itemTypes.TryGetValue(item.TypeId, out var type) || type.CategoryId != BlueprintCategoryId)
            .GroupBy(item => item.TypeId).ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
    }

    private static IndustryMonitorRequest Parse(PyValue value) => new(
        Long(Field(value, "blueprintID")),
        Int(Field(value, "blueprintTypeID")),
        Int(Field(value, "activityID")),
        Long(Field(value, "facilityID")),
        ParseLocation(Field(value, "inputLocation")));

    private static IndustryMonitorLocation? ParseLocation(PyValue? value)
    {
        if (value is null or PyNone) return null;
        var itemId = Long(Field(value, "itemID"));
        if (itemId <= 0) return null;
        var canTake = Field(value, "canTake") is not { } take || take is PyNone || Boolean(take);
        return new IndustryMonitorLocation(itemId, Long(Field(value, "ownerID")), Int(Field(value, "flagID")), canTake);
    }

    private static PyValue? Field(PyValue? value, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = value switch { PyDict direct => direct.Entries, PyObject { Args: PyDict args } => args.Entries, PyObjectEx ex => ex.Dict, _ => Array.Empty<KeyValuePair<PyValue, PyValue>>() };

        foreach (var (key, entry) in entries)
            if (JsSemantics.StringValue(key) == name) return entry;
        return null;
    }

    private static int Int(PyValue? value) => (int)Math.Clamp(Long(value), int.MinValue, int.MaxValue);
    private static long Long(JsonNode? value) => long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static long Long(PyValue? value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r when double.IsFinite(r.Value) => (long)r.Value, _ => 0 };
    private static bool Boolean(PyValue value) => value switch { PyBool b => b == PyBool.True, PyInt i => i.Value != 0, PyLong l => l.Value != 0, _ => false };
}
