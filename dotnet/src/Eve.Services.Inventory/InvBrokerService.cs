using System.Collections.Concurrent;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Universe;

namespace Eve.Services.Inventory;

/// <summary>
/// The inventory broker: it hands out the inventories the client opens (the hangar of its station, the station, the ship in
/// the hangar) as bound objects, and each of them lists what is in it. Only reading is here: moving, stacking and fitting
/// items come later.
/// </summary>
[Service("invbroker")]
public sealed partial class InvBrokerService(PgItemStore items, ItemTypeCatalog types, StationCatalog stations, Func<long> nowUnixMs) : IBoundObjectReleaseHandler
{
    /// <summary>The container the client asks for to get the hangar of the station it is docked at.</summary>
    public const int ContainerHangar = 10004;

    private const int ShipCategory = 6;

    private enum Kind
    {
        /// <summary>The character's hangar in its station (also what the station's own inventory shows).</summary>
        Hangar,

        /// <summary>A ship in the hangar: its cargo and fitting, which nothing fills yet.</summary>
        Ship,
    }

    /// <summary>What a bound inventory is: which kind, whose item it is, and the session that opened it.</summary>
    private sealed record Context(Kind Kind, long ItemId, long SessionId);

    private readonly ConcurrentDictionary<string, Context> _bound = new(StringComparer.Ordinal);

    private static PyValue None => PyNone.Instance;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>The station the session is docked at.</summary>
    private static long? StationOf(ClientSession session) => session.GetInt("stationid");

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    private PyValue Bind(Context context, ClientSession session)
    {
        var oid = $"N={MachoMessage.ProxyNodeId}:{BoundObjectIds.Next()}";
        _bound[oid] = context with { SessionId = session.SessionId };
        session.Closed += () => _bound.TryRemove(oid, out _); // an inventory does not outlive the connection that opened it
        return new PySubStruct(new PySubStream(Py.Tuple(new PyStr(oid), new PyLong(Py.FileTimeFromUnixMs(nowUnixMs())))));
    }

    /// <summary>The bound inventory the call is addressed to, when this session opened it.</summary>
    private Context? ContextOf(ServiceCall call) =>
        call.BoundObjectId is { } oid && _bound.TryGetValue(oid, out var context) && context.SessionId == call.Session.SessionId ? context : null;

    // ── the rows ─────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<ItemRow>> RowsAsync(IEnumerable<Item> list, CancellationToken cancellationToken)
    {
        var all = list.ToList();
        var known = await types.GetAsync(all.Select(i => i.TypeId), cancellationToken);
        return [.. all.Where(i => known.ContainsKey(i.TypeId)).Select(i => Row(i, known[i.TypeId]))];
    }

    private static ItemRow Row(Item item, ItemType type) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, type.GroupId, type.CategoryId);

    /// <summary>What is in the character's hangar at its station.</summary>
    private async Task<IReadOnlyList<ItemRow>> HangarAsync(ClientSession session, CancellationToken cancellationToken) =>
        StationOf(session) is { } station && session.CharacterId is { } character
            ? await RowsAsync(await items.InLocationAsync(station, character, cancellationToken), cancellationToken)
            : [];

    /// <summary>The station as an item of its own: the "container" the hangar is in.</summary>
    private async Task<ItemRow?> StationRowAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (StationOf(session) is not { } id || stations.Get(id) is not { } station || await types.GetAsync(station.TypeId, cancellationToken) is not { } type)
        {
            return null;
        }
        return new ItemRow(station.Id, station.TypeId, station.OwnerId, station.Id, 0, 1, type.GroupId, type.CategoryId);
    }

    /// <summary>The item the bound inventory is of: the station, or the ship.</summary>
    private async Task<ItemRow?> SelfAsync(Context context, ClientSession session, CancellationToken cancellationToken)
    {
        if (context.Kind == Kind.Hangar)
        {
            return await StationRowAsync(session, cancellationToken);
        }
        return await items.GetAsync(context.ItemId, cancellationToken) is { } ship ? (await RowsAsync([ship], cancellationToken)).FirstOrDefault() : null;
    }

    // ── handing out inventories ─────────────────────────────────────────────

    /// <summary>The descriptor the client reads item rows through.</summary>
    [Method("GetItemDescriptor")]
    public PyValue GetItemDescriptor(ServiceCall call) => InventoryWire.DescriptorHeader;

    /// <summary><c>GetInventory(containerID)</c>: the hangar of the station the character is docked at, asked for by its container id, the station's id or nothing.</summary>
    [Method("GetInventory")]
    public PyValue GetInventory(ServiceCall call)
    {
        if (StationOf(call.Session) is not { } station)
        {
            return None;
        }
        var container = call.Arg(0) is PyNone ? station : IdOf(call.Arg(0));
        return container == ContainerHangar || container == station ? Bind(new Context(Kind.Hangar, station, 0), call.Session) : None;
    }

    /// <summary><c>GetInventoryFromId(itemID)</c>: the station's own inventory (what its hangar shows) or the inventory of a ship of the character in the hangar.</summary>
    [Method("GetInventoryFromId")]
    public async ValueTask<PyValue> GetInventoryFromId(ServiceCall call)
    {
        if (IdOf(call.Arg(0)) is not { } id || StationOf(call.Session) is not { } station)
        {
            return None;
        }
        if (id == station)
        {
            return Bind(new Context(Kind.Hangar, station, 0), call.Session);
        }
        if (call.Session.CharacterId is { } character && await items.GetAsync(id, call.Cancellation) is { } item
            && item.OwnerId == character && item.LocationId == station
            && await types.GetAsync(item.TypeId, call.Cancellation) is { CategoryId: ShipCategory })
        {
            return Bind(new Context(Kind.Ship, item.Id, 0), call.Session);
        }
        return None;
    }

    // ── what the client asks a bound inventory ───────────────────────────────

    private static int? FlagOf(ServiceCall call) => call.Kwarg("flag") is { } named ? Flag(named) : call.Args.Count > 0 ? Flag(call.Args[0]) : null;

    private static int? Flag(PyValue value) => IdOf(value) is { } flag and not 0 ? (int)flag : null;

    /// <summary>
    /// <c>List(flag)</c>: everything in the inventory, or what is in one flag. A flag with nothing in it is answered with an empty
    /// Python <c>set</c>, as the reference does; everything else is a list of rows.
    /// </summary>
    [Method("List")]
    public async ValueTask<PyValue> List(ServiceCall call)
    {
        if (ContextOf(call) is not { } context)
        {
            return None;
        }
        var flag = FlagOf(call);
        var contents = context.Kind == Kind.Hangar ? await HangarAsync(call.Session, call.Cancellation) : [];
        var chosen = flag is { } f ? contents.Where(r => r.Flag == f).ToList() : [.. contents];
        return chosen.Count == 0 && flag is not null && context.Kind == Kind.Hangar
            ? InventoryWire.EmptySet()
            : new PyList([.. chosen.Select(InventoryWire.PackedRow)]);
    }

    /// <summary><c>ListByFlags(flags)</c>: the rows in any of the flags, as a list (empty when there are none).</summary>
    [Method("ListByFlags")]
    public async ValueTask<PyValue> ListByFlags(ServiceCall call)
    {
        if (ContextOf(call) is not { } context)
        {
            return None;
        }
        var wanted = (call.Arg(0) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] }).Select(IdOf).Where(v => v is not null).Select(v => (int)v!.Value).ToHashSet();
        var contents = context.Kind == Kind.Hangar ? await HangarAsync(call.Session, call.Cancellation) : [];
        return new PyList([.. contents.Where(r => wanted.Contains(r.Flag)).Select(InventoryWire.PackedRow)]);
    }

    /// <summary><c>GetItems(itemIDs)</c>: the rows of these items where they are in the inventory (none, when no ids are given).</summary>
    [Method("GetItems")]
    public async ValueTask<PyValue> GetItems(ServiceCall call)
    {
        if (ContextOf(call) is not { } context)
        {
            return None;
        }
        var wanted = (call.Arg(0) switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] }).Select(IdOf).Where(v => v is not null).Select(v => v!.Value).ToHashSet();
        var contents = context.Kind == Kind.Hangar ? await HangarAsync(call.Session, call.Cancellation) : [];
        return new PyList([.. contents.Where(r => wanted.Contains(r.ItemId)).Select(InventoryWire.PackedRow)]);
    }

    /// <summary>The item the inventory is of (the station, or the ship) as a <c>util.Row</c>.</summary>
    [Method("GetItem")]
    public async ValueTask<PyValue> GetItem(ServiceCall call) =>
        ContextOf(call) is { } context && await SelfAsync(context, call.Session, call.Cancellation) is { } self ? InventoryWire.UtilRow(self) : None;

    /// <summary>The same item as a packed row.</summary>
    [Method("GetSelfInvItem")]
    public async ValueTask<PyValue> GetSelfInvItem(ServiceCall call) =>
        ContextOf(call) is { } context && await SelfAsync(context, call.Session, call.Cancellation) is { } self ? InventoryWire.PackedRow(self) : None;

    /// <summary>Everything in the inventory as a <c>Rowset</c>.</summary>
    [Method("GetContainerContents")]
    public async ValueTask<PyValue> GetContainerContents(ServiceCall call)
    {
        if (ContextOf(call) is not { } context)
        {
            return None;
        }
        return InventoryWire.Rowset(context.Kind == Kind.Hangar ? await HangarAsync(call.Session, call.Cancellation) : []);
    }

    // ── binding by the inventory's own name ──────────────────────────────────

    /// <summary>The proxy node serves every inventory.</summary>
    [Method("MachoResolveObject")]
    public PyValue MachoResolveObject(ServiceCall call) => new PyInt(MachoMessage.ProxyNodeId);

    /// <summary>
    /// <c>MachoBindObject((inventoryID[, flag]), (method, args, kwargs))</c>: opens the inventory named in the bind parameters, then
    /// makes the nested call to the service, whose answer goes back with the bound object.
    /// </summary>
    [Method("MachoBindObject")]
    public async ValueTask<PyValue> MachoBindObject(ServiceCall call)
    {
        var parameters = call.Arg(0) switch { PyTuple t when t.Items.Count > 0 => t.Items[0], PyList l when l.Items.Count > 0 => l.Items[0], var other => other };
        var opened = await ((IService)this).CallAsync(new ServiceCall
        {
            Method = IdOf(parameters) == ContainerHangar ? "GetInventory" : "GetInventoryFromId",
            Args = [parameters],
            Session = call.Session,
            CallId = call.CallId,
            Cancellation = call.Cancellation,
        });
        if (opened is not PySubStruct)
        {
            return None;
        }
        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            var oid = (JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)opened).Value).Decoded!).Items[0]));
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList ? (nested.Items[1] is PyTuple a ? a.Items : ((PyList)nested.Items[1]).Items) : [],
                Kwargs = nested.Items.Count > 2 ? nested.Items[2] : None,
                Session = call.Session,
                CallId = call.CallId,
                BoundObjectId = oid,
                Cancellation = call.Cancellation,
            });
        }
        return Py.Tuple(opened, result);
    }
}
