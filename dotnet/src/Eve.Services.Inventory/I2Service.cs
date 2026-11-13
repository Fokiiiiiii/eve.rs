using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;

namespace Eve.Services.Inventory;

/// <summary>
/// The V24 devtools item inspector's location-bound i2 moniker. It resolves persisted items below the
/// character's current station or solar system and emits the same util.Row shape used by inventory reads.
/// </summary>
[Service("i2")]
public sealed partial class I2Service(PgItemStore items, ItemTypeCatalog itemTypes, Func<long> nowUnixMs)
{
    // V24.01 inventorycommon/const.py:910, 926.
    private const int SolarSystemGroup = 5;
    private const int StationGroup = 15;
    private const int MaxLocationDepth = 32;

    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>Opens a location-bound object and, on a first Moniker call, runs that nested method.</summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        if (!TryLocation(call.Arg(0), out var locationId, out var locationGroup) ||
            !IsCurrentLocation(call.Session, locationId, locationGroup))
            return PyNone.Instance;

        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        var opened = new PySubStruct(new PySubStream(
            Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
        PyValue result = PyNone.Instance;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            var args = nested.Items.Count > 1 ? nested.Items[1] switch
            {
                PyTuple tuple => tuple.Items,
                PyList list => list.Items,
                _ => [],
            } : [];
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = args,
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : PyNone.Instance,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                BoundObjectParameters = call.Arg(0),
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(opened, result);
    }

    /// <summary><c>IsPrimed(itemID)</c> is true only for a persisted item in the bound location's inventory tree.</summary>
    [Method("IsPrimed")]
    public async ValueTask<PyValue> IsPrimed(ServiceCall call)
    {
        if (!TryBoundLocation(call, out var locationId, out var locationGroup) ||
            !IsCurrentLocation(call.Session, locationId, locationGroup) || !TryId(call.Arg(0), out var itemId))
            return PyBool.False;

        var item = await items.GetAsync(itemId, call.Cancellation);
        return item is not null && await BelongsToLocationAsync(item, locationId, call.Cancellation)
            ? PyBool.True
            : PyBool.False;
    }

    /// <summary><c>GetItem(itemID)</c> returns the inventory util.Row consumed by the V24 inspector's <c>.typeID</c> read.</summary>
    [Method("GetItem")]
    public async ValueTask<PyValue> GetItem(ServiceCall call)
    {
        if (!TryBoundLocation(call, out var locationId, out var locationGroup) ||
            !IsCurrentLocation(call.Session, locationId, locationGroup) || !TryId(call.Arg(0), out var itemId) ||
            await items.GetAsync(itemId, call.Cancellation) is not { } item ||
            !await BelongsToLocationAsync(item, locationId, call.Cancellation))
            return PyNone.Instance;

        if (await itemTypes.GetAsync(item.TypeId, call.Cancellation) is not { } type)
            return PyNone.Instance;

        return InventoryWire.UtilRow(new ItemRow(
            item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Quantity,
            type.GroupId, type.CategoryId));
    }

    private static bool TryBoundLocation(ServiceCall call, out long locationId, out int locationGroup) =>
        TryLocation(call.BoundObjectParameters, out locationId, out locationGroup);

    private static bool TryLocation(PyValue? value, out long locationId, out int locationGroup)
    {
        locationId = 0;
        locationGroup = 0;
        var values = value switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => [],
        };
        return values.Count == 2 && TryId(values[0], out locationId) && TryInt(values[1], out locationGroup) &&
            locationGroup is SolarSystemGroup or StationGroup;
    }

    private static bool IsCurrentLocation(ClientSession session, long locationId, int locationGroup)
    {
        if (session.CharacterId is null)
            return false;
        var activeLocation = locationGroup switch
        {
            SolarSystemGroup => session.GetInt("solarsystemid"),
            StationGroup => session.GetInt("stationid") ?? session.GetInt("structureid"),
            _ => null,
        };
        return activeLocation == locationId;
    }

    private async Task<bool> BelongsToLocationAsync(Item item, long locationId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<long> { item.Id };
        var current = item;
        for (var depth = 0; depth < MaxLocationDepth; depth++)
        {
            if (current.Id == locationId || current.LocationId == locationId)
                return true;
            if (!visited.Add(current.LocationId))
                return false;
            var parent = await items.GetAsync(current.LocationId, cancellationToken);
            if (parent is null)
                return false;
            current = parent;
        }
        return false;
    }

    private static bool TryId(PyValue? value, out long id)
    {
        id = value switch
        {
            PyInt integer => integer.Value,
            PyLong integer => integer.Value,
            _ => 0,
        };
        return id > 0;
    }

    private static bool TryInt(PyValue? value, out int number)
    {
        if (value is PyInt { Value: >= int.MinValue and <= int.MaxValue } integer)
        {
            number = (int)integer.Value;
            return true;
        }
        number = 0;
        return false;
    }
}
