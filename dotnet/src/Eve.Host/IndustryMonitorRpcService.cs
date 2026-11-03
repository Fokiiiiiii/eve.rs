using System.Globalization;
using System.Text.Json.Nodes;
using Eve.GameStore;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Host;

public sealed record IndustryMonitorLocation(long ItemId, int FlagId);
public sealed record IndustryMonitorRequest(long BlueprintId, int BlueprintTypeId, int ActivityId, long FacilityId, IndustryMonitorLocation? InputLocation);

public sealed class IndustryMonitorRpcService : IService
{
    private const long FirstMonitorId = 1;
    private const int BlueprintCategoryId = 9;
    private sealed class MonitorWatch(IndustryMonitorRequest request, long monitorId, IReadOnlyDictionary<int, long> materials)
    {
        public IndustryMonitorRequest Request { get; } = request;
        public long MonitorId { get; } = monitorId;
        public IReadOnlyDictionary<int, long> Materials { get; set; } = materials;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly GameStoreCache? _store;
    private readonly PgItemStore? _items;
    private readonly ItemTypeCatalog? _itemTypes;
    private readonly Func<IndustryMonitorRequest, CancellationToken, ValueTask<IReadOnlyDictionary<int, long>>>? _materials;
    private readonly Func<ClientSession, IndustryMonitorRequest, Item, CancellationToken, ValueTask<long?>>? _authorizedInputLocation;
    private readonly Func<long> _now;
    private readonly object _gate = new();
    private readonly Dictionary<ClientSession, MonitorWatch> _watches = new();
    private long _nextMonitorId = FirstMonitorId;

    public IndustryMonitorRpcService(GameStoreCache? store = null, PgItemStore? items = null,
        Func<IndustryMonitorRequest, CancellationToken, ValueTask<IReadOnlyDictionary<int, long>>>? availableMaterials = null,
        Func<long>? nowUnixMs = null, ItemTypeCatalog? itemTypes = null,
        Func<ClientSession, IndustryMonitorRequest, Item, CancellationToken, ValueTask<long?>>? authorizedInputLocation = null)
        => (_store, _items, _itemTypes, _materials, _authorizedInputLocation, _now) =
            (store, items, itemTypes, availableMaterials, authorizedInputLocation,
                nowUnixMs ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));

    public string Name => "industryMonitor";
    public bool HasMethod(string method) => method is "ConnectJob" or "DisconnectJob";

    public async ValueTask<PyValue> CallAsync(ServiceCall call)
    {
        if (!HasMethod(call.Method)) return PyNone.Instance;
        call.Cancellation.ThrowIfCancellationRequested();
        if (call.Method == "DisconnectJob")
        {
            var id = Long(call.Arg(0));
            lock (_gate)
            {
                if (!_watches.TryGetValue(call.Session, out var currentWatch) || currentWatch.MonitorId != id)
                    return PyNone.Instance;
                _watches.Remove(call.Session);
                RemovePersistedMonitor(id);
            }
            return PyNone.Instance;
        }

        var request = Parse(call.Arg(0));
        var materials = await ReadAvailableMaterialsAsync(call.Session, request, call.Cancellation);
        var monitorId = AllocateId();
        var row = new JsonObject
        {
            ["monitorID"] = monitorId,
            ["activityID"] = request.ActivityId,
            ["blueprintID"] = request.BlueprintId,
            ["ownerID"] = call.Session.CharacterId ?? 0,
            ["createdAt"] = _now(),
        };
        var write = _store?.Write("industryRuntime", $"/monitors/{monitorId}", row);
        if (write is { Success: false }) throw new InvalidOperationException($"Could not save industry monitor: {write.ErrorMsg}");

        var newWatch = new MonitorWatch(request, monitorId, materials);
        lock (_gate)
        {
            var hadWatch = _watches.ContainsKey(call.Session);
            _watches[call.Session] = newWatch;
            if (!hadWatch)
                call.Session.Closed += () => CloseSession(call.Session);
        }

        return Py.Tuple(new PyInt(monitorId), ToPyMaterials(materials));
    }

    public async ValueTask InventoryChangedAsync(ClientSession session, CancellationToken cancellationToken)
    {
        MonitorWatch? watch;
        lock (_gate) _watches.TryGetValue(session, out watch);
        if (watch is null) return;

        await watch.Gate.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
                if (!_watches.TryGetValue(session, out var current) || !ReferenceEquals(current, watch)) return;

            var materials = await ReadAvailableMaterialsAsync(session, watch.Request, cancellationToken);
            lock (_gate)
            {
                if (!_watches.TryGetValue(session, out var current) || !ReferenceEquals(current, watch)
                    || SameMaterials(watch.Materials, materials))
                    return;
                watch.Materials = materials;
                session.SendNotification("OnIndustryMaterials", "clientID", new PyInt(watch.MonitorId), ToPyMaterials(materials));
            }
        }
        finally
        {
            watch.Gate.Release();
        }
    }

    private void CloseSession(ClientSession session)
    {
        lock (_gate) _watches.Remove(session);
    }

    private void RemovePersistedMonitor(long id)
    {
        if (_store?.Read("industryRuntime", $"/monitors/{id}").Data is not JsonObject) return;
        var result = _store.Remove("industryRuntime", $"/monitors/{id}");
        if (result is { Success: false }) throw new InvalidOperationException($"Could not remove industry monitor: {result.ErrorMsg}");
    }

    private long AllocateId()
    {
        lock (_gate)
        {
            var stored = _store?.Read("industryRuntime", "/_meta/nextMonitorID").Data;
            var id = Math.Max(FirstMonitorId, Math.Max(_nextMonitorId, Long(stored)));
            _nextMonitorId = id + 1;
            var write = _store?.Write("industryRuntime", "/_meta/nextMonitorID", JsonValue.Create(_nextMonitorId));
            if (write is { Success: false }) throw new InvalidOperationException($"Could not save next industry monitor ID: {write.ErrorMsg}");
            return id;
        }
    }

    private async ValueTask<IReadOnlyDictionary<int, long>> ReadAvailableMaterialsAsync(
        ClientSession session, IndustryMonitorRequest request, CancellationToken cancellationToken)
    {
        if (_items is null)
            return _materials is null
                ? new Dictionary<int, long>()
                : await _materials(request, cancellationToken);
        if (_itemTypes is null || _authorizedInputLocation is null || request.BlueprintId <= 0)
            return new Dictionary<int, long>();
        var blueprint = await _items.GetAsync(request.BlueprintId, cancellationToken);
        if (blueprint is null || (request.BlueprintTypeId > 0 && blueprint.TypeId != request.BlueprintTypeId))
            return new Dictionary<int, long>();
        var inventoryLocationId = await _authorizedInputLocation(session, request, blueprint, cancellationToken);
        if (inventoryLocationId is not > 0 || request.InputLocation is not { } inputLocation)
            return new Dictionary<int, long>();
        var contents = (await _items.InLocationAsync(inventoryLocationId.Value, blueprint.OwnerId, cancellationToken))
            .Where(item => item.Flag == inputLocation.FlagId)
            .ToArray();
        var itemTypes = await _itemTypes.GetAsync(contents.Select(item => item.TypeId), cancellationToken);
        return contents.Where(item => !itemTypes.TryGetValue(item.TypeId, out var type) || type.CategoryId != BlueprintCategoryId)
            .GroupBy(item => item.TypeId).ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));
    }

    private static bool SameMaterials(IReadOnlyDictionary<int, long> left, IReadOnlyDictionary<int, long> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var quantity) && quantity == pair.Value);

    private static PyDict ToPyMaterials(IReadOnlyDictionary<int, long> materials) =>
        new(materials.Select(pair => new KeyValuePair<PyValue, PyValue>(new PyInt(pair.Key), new PyInt(pair.Value))).ToArray());

    private static IndustryMonitorRequest Parse(PyValue value) => new(
        Long(Field(value, "blueprintID")), Int(Field(value, "blueprintTypeID")),
        Int(Field(value, "activityID")), Long(Field(value, "facilityID")),
        ParseLocation(Field(value, "inputLocation")));

    private static IndustryMonitorLocation? ParseLocation(PyValue? value)
    {
        if (value is null or PyNone) return null;
        var itemId = Long(Field(value, "itemID"));
        if (itemId <= 0) return null;
        return new IndustryMonitorLocation(itemId, Int(Field(value, "flagID")));
    }

    private static PyValue? Field(PyValue? value, string name)
    {
        IEnumerable<KeyValuePair<PyValue, PyValue>> entries = value switch
        {
            PyDict direct => direct.Entries,
            PyObject { Args: PyDict args } => args.Entries,
            PyObjectEx ex => ex.Dict,
            _ => Array.Empty<KeyValuePair<PyValue, PyValue>>()
        };
        foreach (var (key, entry) in entries)
            if (JsSemantics.StringValue(key) == name) return entry;
        return null;
    }

    private static int Int(PyValue? value) => (int)Math.Clamp(Long(value), int.MinValue, int.MaxValue);
    private static long Long(JsonNode? value) => long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static long Long(PyValue? value) => value switch { PyInt i => i.Value, PyLong l => l.Value, PyReal r when double.IsFinite(r.Value) => (long)r.Value, _ => 0 };
}
