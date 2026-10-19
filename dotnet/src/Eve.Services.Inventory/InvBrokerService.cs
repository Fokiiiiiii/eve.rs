using System.Collections.Concurrent;
using Eve.Dogma;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services.Ships;
using Eve.Ships;
using Eve.Universe;

namespace Eve.Services.Inventory;

public sealed record ControlledStructureInventory(Item Structure, IReadOnlyList<Item> Contents);

[Service("invbroker")]
public sealed partial class InvBrokerService(
    PgItemStore items, ItemTypeCatalog types, StationCatalog stations, CharacterShips ships, DogmaStatic statics, Func<long> nowUnixMs, ISpaceContainers? space = null, ISpaceDroneBays? droneBays = null, CorporationHangarAccess? corporationHangarAccess = null, IReadOnlyList<Eve.Services.ICharacterEntryHook>? entryHooks = null,
    Func<ClientSession, long, CancellationToken, ValueTask<ControlledStructureInventory?>>? controlledStructureInventory = null,
            Func<IReadOnlyCollection<long>, CancellationToken, Task<IReadOnlyDictionary<long, double>>>? crystalDamage = null) : IBoundObjectReleaseHandler
{
    /// <summary>The container the client asks for to get the hangar of the station it is docked at.</summary>
    public const int ContainerHangar = 10004;

    private const int ShipCategory = 6;

    /// <summary>The category of characters: the one row the reference gives a custom info.</summary>
    private const int PlayerCategory = 1;

    /// <summary>The volume that holds what a station keeps for one character, when the hangar is asked how much it holds of a flag the reference gives no ship to.</summary>
    private const double HangarCapacity = 1_000_000;

    private const double BareCargoCapacity = 5000;

    private enum Kind
    {
        /// <summary>The character's hangar in its station (also what the station's own inventory shows).</summary>
        Hangar,

        /// <summary>A ship in the hangar, or the ship the character flies in space: its fitted modules and its cargo.</summary>
        Ship,

        /// <summary>The solar system the character flies in: what the client binds first to reach the inventory of its ship or of a can beside it. It holds nothing itself.</summary>
        Space,

        /// <summary>A can or a wreck in space beside the ship.</summary>
        Container,

        /// <summary>The current pilot's structure inventory; this binding never grants mutation access.</summary>
        Structure,

        /// <summary>All personal assets for the bound character's global assets window.</summary>
        GlobalAssets,

        /// <summary>The selected character's learned skills, primed by godma as a location.</summary>
        Character,
    }

    /// <summary>What a bound inventory is: which kind, whose item it is, and the session that opened it.</summary>
    private sealed record Context(Kind Kind, long ItemId, long SessionId, long FlagId = 0);

    private readonly ConcurrentDictionary<string, Context> _bound = new(StringComparer.Ordinal);

    private static PyValue None => PyNone.Instance;

    private static long? IdOf(PyValue value) => value switch
    {
        PyInt number => number.Value,
        PyLong number => number.Value,
        _ => null,
    };

    /// <summary>The station the session is docked at.</summary>
    private static long? StationOf(ClientSession session) => session.DockedLocationId;

    public void OnBoundObjectReleased(string oid, ClientSession? session) => _bound.TryRemove(oid, out _);

    /// <summary>The planetary customs offices (set by the host when the planets are running).</summary>
    public ICustomsOffices? Customs { get; set; }

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

    private async ValueTask<ControlledStructureInventory?> ControlledStructureAsync(
        ClientSession session, long structureId, CancellationToken cancellationToken)
    {
        if (controlledStructureInventory is null || structureId <= 0 || session.CharacterId is null
            || session.GetInt("shipid") != structureId || session.GetInt("structureid") != structureId)
            return null;
        var view = await controlledStructureInventory(session, structureId, cancellationToken);
        if (view is null || view.Structure.Id != structureId || view.Structure.OwnerId <= 0
            || statics.CategoryId("Structure") is not { } category
            || statics.Type(view.Structure.TypeId)?.CategoryId != category
            || view.Contents.Any(item => item.LocationId != structureId || item.OwnerId != view.Structure.OwnerId))
            return null;
        return view;
    }

    private void RequireMutableInventory(ServiceCall call)
    {
        if (ContextOf(call)?.Kind == Kind.Character)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The character skill inventory is read-only.")));
        if (ContextOf(call)?.Kind == Kind.Structure)
            throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("The controlled structure inventory is read-only.")));
    }

    private async Task<PyValue> StructureCapacityAsync(
        ControlledStructureInventory view, int? flag, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([view.Structure.TypeId, .. view.Contents.Select(item => item.TypeId)], cancellationToken);
        // EveJS owner bays without an SDE capacity use the existing unbounded inventory wire sentinel.
        var capacityAttribute = statics.AttributeId("capacity");
        var capacity = capacityAttribute is { } attribute && statics.Type(view.Structure.TypeId) is { } hull
            && hull.Attributes.TryGetValue(attribute, out var value) ? value : HangarCapacity;
        if (flag is { } slot && Eve.Fitting.Slots.IsSlotFlag(slot))
        {
            var module = view.Contents.FirstOrDefault(item => item.Flag == slot
                && statics.Type(item.TypeId)?.CategoryId != statics.CategoryId("Charge"));
            capacity = module is not null && capacityAttribute is { } moduleCapacity
                ? statics.Type(module.TypeId)?.Attributes.GetValueOrDefault(moduleCapacity) ?? 0 : 0;
        }
        var used = await ships.VolumeOfAsync(view.Contents.Where(item => flag is null || item.Flag == flag), cancellationToken);
        return KeyVal(capacity, used);
    }

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
    private static long CorporationIdOf(ClientSession session) =>
        session.GetInt("corporationID") is { } corporationId && corporationId != 0
            ? corporationId
            : session.GetInt("corpid") ?? 0;

    private ValueTask<bool> CorporationAccessAsync(
        long characterId, long corporationId, long stationId, int flag,
        CorporationHangarOperation operation, CancellationToken cancellationToken) =>
        corporationHangarAccess is { } authorize
            ? authorize(characterId, corporationId, stationId, flag, operation, cancellationToken)
            : ValueTask.FromResult(false);

    private async Task<(long StationId, int DivisionFlag)?> CorporationDivisionOfItemAsync(
        Item item, long corporationId, CancellationToken cancellationToken)
    {
        if (item.OwnerId != corporationId)
            return null;
        if (ItemFlags.IsCorporationHangar(item.Flag) && stations.Get(item.LocationId) is not null)
            return (item.LocationId, item.Flag);

        var location = item.LocationId;
        var seen = new HashSet<long>();
        for (var depth = 0; depth < 32 && location > 0 && seen.Add(location); depth++)
        {
            if (await items.GetAsync(location, cancellationToken) is not { OwnerId: var owner, LocationId: var parentLocation, Flag: var flag } parent
                || owner != corporationId)
                return null;
            if (ItemFlags.IsCorporationHangar(flag) && stations.Get(parentLocation) is not null)
                return (parentLocation, flag);
            location = parentLocation;
        }
        return null;
    }

    private async ValueTask<bool> CanAccessCorporationItemAsync(
        ClientSession session, Item item, CorporationHangarOperation operation, CancellationToken cancellationToken)
    {
        if (session.CharacterId is not { } character)
            return false;
        var corporationId = CorporationIdOf(session);
        if (corporationId <= 0 || corporationId != item.OwnerId)
            return false;
        var division = await CorporationDivisionOfItemAsync(item, corporationId, cancellationToken);
        return division is { } location
            && await CorporationAccessAsync(character, corporationId, location.StationId, location.DivisionFlag, operation, cancellationToken);
    }

    private async Task<IReadOnlyList<Item>> StationItemsAsync(
        ClientSession session, long characterId, long stationId, CancellationToken cancellationToken)
    {
        var visible = new List<Item>(await items.InLocationAsync(stationId, characterId, cancellationToken));
        var corporationId = CorporationIdOf(session);
        if (corporationId <= 0 || corporationId == characterId || corporationHangarAccess is null)
            return visible;

        var divisions = (await items.InLocationAsync(stationId, corporationId, cancellationToken))
            .Where(item => ItemFlags.IsCorporationHangar(item.Flag))
            .GroupBy(item => item.Flag);
        foreach (var division in divisions)
        {
            if (await CorporationAccessAsync(characterId, corporationId, stationId, division.Key,
                    CorporationHangarOperation.Query, cancellationToken))
                visible.AddRange(division);
        }
        return visible;
    }

    private async Task<IReadOnlyList<ItemRow>> HangarAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (StationOf(session) is not { } station || session.CharacterId is not { } character)
            return [];
        return await RowsAsync(await StationItemsAsync(session, character, station, cancellationToken), cancellationToken);
    }

    /// <summary>What is in the bound inventory: the hangar's ships, a ship's fitted modules and cargo, or what a can or a wreck holds.</summary>
    private async Task<IReadOnlyList<ItemRow>> ContentsAsync(Context context, ClientSession session, CancellationToken cancellationToken)
    {
        if (context.Kind is Kind.Structure or Kind.Hangar)
        {
            if (await ControlledStructureAsync(session, context.ItemId, cancellationToken) is { } controlled)
                return await RowsAsync(controlled.Contents, cancellationToken);
            if (context.Kind == Kind.Structure)
                return [];
        }
        if (context.Kind == Kind.Hangar)
            return await HangarAsync(session, cancellationToken);
        if (context.Kind == Kind.Space || session.CharacterId is not { } character)
            return [];
        if (context.Kind == Kind.GlobalAssets)
        {
            if (context.ItemId != character)
                return [];
            return await RowsAsync(await items.OwnedByAsync(character, cancellationToken), cancellationToken);
        }
        if (context.Kind == Kind.Character)
        {
            if (context.ItemId != character || await ships.LoadAsync(character, cancellationToken) is not { } snapshot)
                return [];
            return await RowsAsync(snapshot.Skills.Select(skill => new Item(
                ShipSnapshot.SkillItemId(character, skill.TypeId), skill.TypeId, character, character,
                ShipSnapshot.SkillFlag, 1, true, null)), cancellationToken);
        }
        if (context.Kind is not (Kind.Container or Kind.Ship))
            return [];

        if (await items.GetAsync(context.ItemId, cancellationToken) is not { } container)
            return [];
        var contents = await items.InContainerAsync(context.ItemId, cancellationToken);
        if (container.OwnerId == character)
            return await RowsAsync(contents.Where(item => item.OwnerId == character), cancellationToken);
        if (await CanAccessCorporationItemAsync(session, container, CorporationHangarOperation.Query, cancellationToken))
            return await RowsAsync(contents.Where(item => item.OwnerId == container.OwnerId || item.OwnerId == character), cancellationToken);
        return [];
    }

    /// <summary>The station as an item of its own: the "container" the hangar is in.</summary>
    private async Task<ItemRow?> StationRowAsync(ClientSession session, CancellationToken cancellationToken)
    {
        if (StationOf(session) is not { } id)
            return null;
        if (stations.Get(id) is { } station &&
            await types.GetAsync(station.TypeId, cancellationToken) is { } type)
            return new ItemRow(station.Id, station.TypeId, station.OwnerId, station.Id, 0, 1, type.GroupId, type.CategoryId);
        if (session.GetInt("structureid") != id || await items.GetAsync(id, cancellationToken) is not { } structure)
            return null;
        return (await RowsAsync([structure], cancellationToken)).FirstOrDefault();
    }

    /// <summary>The item the bound inventory is of: the station, the ship, or the can or wreck.</summary>
    private async Task<ItemRow?> SelfAsync(Context context, ClientSession session, CancellationToken cancellationToken)
    {
        if (context.Kind is Kind.Structure or Kind.Hangar)
        {
            if (await ControlledStructureAsync(session, context.ItemId, cancellationToken) is { } controlled)
                return (await RowsAsync([controlled.Structure], cancellationToken)).FirstOrDefault();
            if (context.Kind == Kind.Structure)
                return null;
        }
        if (context.Kind == Kind.Hangar)
        {
            return await StationRowAsync(session, cancellationToken);
        }
        if (context.Kind == Kind.Character)
        {
            if (session.CharacterId != context.ItemId || await ships.LoadAsync(context.ItemId, cancellationToken) is not { } snapshot)
                return null;
            return (await RowsAsync([new Item(snapshot.Character.Id, snapshot.Character.TypeId, snapshot.Character.Id,
                snapshot.Ship.Id, ShipSnapshot.PilotFlag, 1, true, null)], cancellationToken)).FirstOrDefault();
        }
        if (context.Kind == Kind.Space)
        {
            return null;
        }
        return await items.GetAsync(context.ItemId, cancellationToken) is { } item ? (await RowsAsync([item], cancellationToken)).FirstOrDefault() : null;
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

    /// <summary>
    /// <c>GetInventoryFromId(itemID)</c>: the station's own inventory (what its hangar shows) or the inventory of a ship of the character in the hangar; in space the solar system
    /// (to bind first), the ship the character flies, or a can or wreck the pilot can open (<see cref="ISpaceContainers.OpenAsync"/>).
    /// </summary>
    private async ValueTask<PyValue> CompleteInventoryRpcAsync(ServiceCall call, PyValue response)
    {
        var context = ContextOf(call);
        var stationHangar = context is { Kind: Kind.Hangar };
        var activeSpaceShipInventoryList = call.Method == "List" &&
            context is { Kind: Kind.Ship } ship &&
            call.Session.DockedLocationId is null &&
            call.Session.GetInt("shipid") == ship.ItemId;
        if (entryHooks is not null)
            foreach (var hook in entryHooks)
                await hook.OnInventoryRpcAsync(
                    call.Session,
                    call.Method,
                    stationHangar,
                    activeSpaceShipInventoryList,
                    call.Cancellation);
        return response;
    }

    [Method("GetInventoryFromId")]
    public async ValueTask<PyValue> GetInventoryFromId(ServiceCall call)
    {
        PyValue result;
        if (IdOf(call.Arg(0)) is not { } id)
        {
            result = None;
        }
        else if (call.Session.CharacterId == id)
        {
            result = Bind(new Context(Kind.Character, id, 0), call.Session);
        }
        else if (call.Session.GetInt("structureid") == id && call.Session.GetInt("shipid") == id)
        {
            result = await ControlledStructureAsync(call.Session, id, call.Cancellation) is not null
                ? Bind(new Context(Kind.Structure, id, 0), call.Session)
                : None;
        }
        else if (Customs is not null && await Customs.IsOfficeAsync(id, call.Cancellation))
        {
            result = Bind(new Context(Kind.Container, id, 0, CharacterShips.ContainerFlag), call.Session);
        }
        else if (StationOf(call.Session) is not { } station)
        {
            result = await OpenInSpaceAsync(call, id);
        }
        else if (id == station)
        {
            result = Bind(new Context(Kind.Hangar, station, 0), call.Session);
        }
        else if (call.Session.CharacterId is not { } character
            || await items.GetAsync(id, call.Cancellation) is not { } item)
        {
            result = None;
        }
        else if (item.LocationId == station
            && await types.GetAsync(item.TypeId, call.Cancellation) is { CategoryId: ShipCategory })
        {
            result = item.OwnerId == character
                || await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Query, call.Cancellation)
                ? Bind(new Context(Kind.Ship, item.Id, 0, CharacterShips.CargoFlag), call.Session)
                : None;
        }
        else if (item.OwnerId == character
            && await ships.IsOwnedDockedContainerAsync(id, character, station, call.Cancellation))
        {
            result = Bind(new Context(Kind.Container, id, 0, CharacterShips.ContainerFlag), call.Session);
        }
        else if (await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Query, call.Cancellation)
            && await ships.IsOwnedDockedContainerAsync(id, item.OwnerId, station, call.Cancellation))
        {
            result = Bind(new Context(Kind.Container, id, 0, CharacterShips.ContainerFlag), call.Session);
        }
        else
        {
            result = None;
        }
        if (result is PyNone)
            throw MachoErrors.ThrowUserError("FakeItemNotFound");
        return await CompleteInventoryRpcAsync(call, result);
    }

    /// <summary>
    /// <c>ImportExportWithPlanet(spaceportPinID, importData, exportData, taxRate)</c> on a bound customs office: the import of the pilot's
    /// stacks in the office, the export of the launchpad's contents to the office, charged and recorded in one transaction (planets).
    /// </summary>
    [Method("ImportExportWithPlanet")]
    public async ValueTask<PyValue> ImportExportWithPlanet(ServiceCall call)
    {
        if (ContextOf(call) is not { Kind: Kind.Container } office || Customs is null)
            throw MachoErrors.ThrowUserError("CustomsOfficeAccessDenied");
        var imports = new Dictionary<long, long>();
        if (call.Arg(1) is PyDict importDict)
            foreach (var (key, quantity) in importDict.Entries)
                imports[IdOf(key) ?? 0] = IdOf(quantity) ?? 0;
        var exports = new Dictionary<int, long>();
        if (call.Arg(2) is PyDict exportDict)
            foreach (var (key, quantity) in exportDict.Entries)
                exports[(int)(IdOf(key) ?? 0)] = IdOf(quantity) ?? 0;
        var rate = call.Arg(3) switch { PyReal real => real.Value, PyInt number => number.Value, PyLong number => number.Value, _ => 0.0 };
        await Customs.ImportExportAsync(call.Session, office.ItemId, IdOf(call.Arg(0)) ?? 0, imports, exports, rate, call.Cancellation);
        return None;
    }

    /// <summary>The inventories of a pilot in space: the solar system it binds first, then its ship and the cans and wrecks beside it.</summary>
    private async Task<PyValue> OpenInSpaceAsync(ServiceCall call, long id)
    {
        if (space is null || !space.IsInSpace(call.Session) || space.SolarSystemOf(call.Session) is not { } system)
        {
            return None;
        }
        if (id == system)
        {
            return Bind(new Context(Kind.Space, system, 0), call.Session);
        }
        if (call.Session.GetInt("shipid") == id)
        {
            return Bind(new Context(Kind.Ship, id, 0, CharacterShips.CargoFlag), call.Session);
        }
        return await space.OpenAsync(call.Session, id, call.Cancellation) is { } container ? Bind(new Context(Kind.Container, container.Id, 0, CharacterShips.ContainerFlag), call.Session) : None;
    }

    // ── what the client asks a bound inventory ───────────────────────────────

    private static int? FlagOf(ServiceCall call) => call.Kwarg("flag") is { } named ? Flag(named) : call.Args.Count > 0 ? Flag(call.Args[0]) : null;

    private static int? Flag(PyValue value) => IdOf(value) is { } flag and not 0 ? (int)flag : null;

    /// <summary>
    /// <c>List(flag)</c>: everything in the inventory, or what is in one flag. A flag with nothing in it is answered with an empty
    /// Python <c>set</c>, as the reference does, and so is a ship in a hangar asked for everything (a set of rows that have no custom info; in space it is a list);
    /// everything else is a list of rows.
    /// </summary>
    [Method("List")]
    public async ValueTask<PyValue> List(ServiceCall call)
    {
        PyValue result;
        if (ContextOf(call) is not { } context)
        {
            result = None;
        }
        else
        {
            var flag = FlagOf(call);
            var controlled = context.Kind is Kind.Structure or Kind.Hangar
                ? await ControlledStructureAsync(call.Session, context.ItemId, call.Cancellation) : null;
            var contents = controlled is not null
                ? await RowsAsync(controlled.Contents, call.Cancellation)
                : await ContentsAsync(context, call.Session, call.Cancellation);
            if (context.Kind == Kind.GlobalAssets)
                contents = contents.Where(row => stations.Get(row.LocationId) is not null).ToArray();
            var chosen = flag is { } f ? contents.Where(r => r.Flag == f).ToList() : [.. contents];
            result = context.Kind == Kind.Ship && flag is null && StationOf(call.Session) is not null
                ? InventoryWire.Set(chosen.Select(r => InventoryWire.PackedRow(r, r.CategoryId == PlayerCategory ? new PyStr("") : PyNone.Instance)))
                : chosen.Count == 0 && flag is not null
                    ? InventoryWire.EmptySet()
                    : new PyList([.. chosen.Select(row => controlled is not null ? InventoryWire.PackedRow(row, new PyStr("")) : InventoryWire.PackedRow(row))]);
        }
        return await CompleteInventoryRpcAsync(call, result);
    }


    /// <summary><c>ListByFlags(flags)</c>: the rows in any of the flags, as a list (empty when there are none).</summary>
    [Method("ListByFlags")]
    public async ValueTask<PyValue> ListByFlags(ServiceCall call)
    {
        if (ContextOf(call) is not { } context)
        {
            return None;
        }
        var flags = call.Arg(0);
        if (flags is PyNone) flags = call.Kwarg("flags") ?? PyNone.Instance;
        var wanted = (flags switch { PyTuple t => t.Items, PyList l => l.Items, _ => [] }).Select(IdOf).Where(v => v is not null).Select(v => (int)v!.Value).ToHashSet();
        var contents = await ContentsAsync(context, call.Session, call.Cancellation);
        return new PyList([.. contents.Where(r => wanted.Contains(r.Flag)).Select(r => InventoryWire.PackedRow(r))]);
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
        var contents = await ContentsAsync(context, call.Session, call.Cancellation);
        return new PyList([.. contents.Where(r => wanted.Contains(r.ItemId)).Select(r => InventoryWire.PackedRow(r))]);
    }

    /// <summary>The item the inventory is of (the station, or the ship) as a <c>util.Row</c>.</summary>
    [Method("GetItem")]
    public async ValueTask<PyValue> GetItem(ServiceCall call) =>
        ContextOf(call) is { } context && await SelfAsync(context, call.Session, call.Cancellation) is { } self ? InventoryWire.UtilRow(self) : None;

    /// <summary>The same item as a packed row.</summary>
    [Method("GetSelfInvItem")]
    public async ValueTask<PyValue> GetSelfInvItem(ServiceCall call)
    {
        var result = ContextOf(call) is { } context &&
            await SelfAsync(context, call.Session, call.Cancellation) is { } self
                ? InventoryWire.PackedRow(self)
                : None;
        return await CompleteInventoryRpcAsync(call, result);
    }


    [Method("GetDamageForCrystals")]
    public async ValueTask<PyValue> GetDamageForCrystals(ServiceCall call)
    {
        var requestedIds = call.Arg(0) switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            PyObjectEx { Header: PyTuple { Items.Count: 2 } header }
                when header.Items[0] is PyToken { Value: "__builtin__.set" }
                     && header.Items[1] is PyTuple { Items.Count: 1 } setArguments
                     && setArguments.Items[0] is PyList values => values.Items,
            _ => []
        };
        var itemIds = requestedIds.Select(IdOf).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        if (itemIds.Count == 0 || call.Session.CharacterId is not { } characterId
            || statics.AttributeId("crystalsGetDamaged") is not { } damageAttributeId)
            return new PyDict([]);

        var ownedItems = (await items.OwnedByAsync(characterId, call.Cancellation))
            .Where(item => itemIds.Contains(item.Id))
            .ToArray();
        await statics.EnsureTypesAsync(ownedItems.Select(item => item.TypeId), call.Cancellation);
        var damageableIds = ownedItems
            .Where(item => statics.Type(item.TypeId)?.Attributes.GetValueOrDefault(damageAttributeId) > 0)
            .Select(item => item.Id)
            .ToArray();
        if (damageableIds.Length == 0)
            return new PyDict([]);
        if (crystalDamage is null)
            throw new InvalidOperationException("Crystal damage state is not configured.");

        var damageByItem = await crystalDamage(damageableIds, call.Cancellation);
        return new PyDict(damageableIds.Select(itemId => new KeyValuePair<PyValue, PyValue>(
            new PyLong(itemId), new PyReal(Math.Clamp(damageByItem.GetValueOrDefault(itemId), 0d, 1d)))).ToArray());
    }


    [Method("ValidateItemCanBeOpened")]
    public async ValueTask<PyValue> ValidateItemCanBeOpened(ServiceCall call)
    {
        var itemId = InventoryItemId(call.Arg(0));
        return itemId is null or <= 0 || await CanOpenInventoryItemAsync(call, itemId.Value)
            ? PyBool.True
            : PyBool.False;
    }

    [Method("ValidateItemListCanBeOpened")]
    public async ValueTask<PyValue> ValidateItemListCanBeOpened(ServiceCall call)
    {
        var values = call.Arg(0) switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => []
        };
        var invalid = new List<PyValue>();
        foreach (var value in values)
        {
            if (InventoryItemId(value) is { } itemId && itemId > 0
                && !await CanOpenInventoryItemAsync(call, itemId))
                invalid.Add(new PyLong(itemId));
        }
        return PyList.Of([.. invalid]);
    }

    private async Task<bool> CanOpenInventoryItemAsync(ServiceCall call, long itemId)
    {
        if (space?.IsInSpace(call.Session) == true)
            return await space.OpenAsync(call.Session, itemId, call.Cancellation) is not null;
        if (call.Session.CharacterId is not { } characterId
            || await items.GetAsync(itemId, call.Cancellation) is not { } item)
            return false;

        if (StationOf(call.Session) is { } stationId
            && item.LocationId == stationId
            && item.Flag == ItemFlags.Hangar
            && item.OwnerId != characterId)
            return false;
        if (ItemFlags.IsCorporationHangar(item.Flag)
            && !await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Query, call.Cancellation))
            return false;
        return true;
    }

    private static long? InventoryItemId(PyValue value) => value switch
    {
        PyInt or PyLong => IdOf(value),
        PyPackedRow row => row.Fields()
            .Where(field => string.Equals(field.Key, "itemID", StringComparison.Ordinal))
            .Select(field => IdOf(field.Value))
            .FirstOrDefault(),
        PyDict dict => InventoryItemId(dict),
        _ => null
    };

    private static long? InventoryItemId(PyDict dict)
    {
        var direct = dict.Entries
            .Where(entry => entry.Key is PyStr { Value: "itemID" })
            .Select(entry => IdOf(entry.Value))
            .FirstOrDefault();
        if (direct is not null)
            return direct;

        var header = dict.Entries.Where(entry => entry.Key is PyStr { Value: "header" }).Select(entry => entry.Value).FirstOrDefault();
        var line = dict.Entries.Where(entry => entry.Key is PyStr { Value: "line" }).Select(entry => entry.Value).FirstOrDefault();
        var headers = header switch { PyList list => list.Items, PyTuple tuple => tuple.Items, _ => [] };
        var values = line switch { PyList list => list.Items, PyTuple tuple => tuple.Items, _ => [] };
        var itemIdIndex = headers.ToList().FindIndex(value => value is PyStr { Value: "itemID" });
        return itemIdIndex >= 0 && itemIdIndex < values.Count ? IdOf(values[itemIdIndex]) : null;
    }

    /// <summary>Everything in the inventory as a <c>Rowset</c>.</summary>
    [Method("GetContainerContents")]
    public async ValueTask<PyValue> GetContainerContents(ServiceCall call)
    {
        if (ContextOf(call) is not { } context)
        {
            return None;
        }
        return InventoryWire.Rowset(await ContentsAsync(context, call.Session, call.Cancellation));
    }

    // ── room ─────────────────────────────────────────────────────────────────

    /// <summary>How the client is told the room of a bay: <c>util.KeyVal(capacity, used)</c>.</summary>
    private static PyValue KeyVal(double capacity, double used) => new PyObject(
        new PyStr("util.KeyVal"), new PyDict([new(new PyStr("capacity"), Py.Number(capacity)), new(new PyStr("used"), Py.Number(used))]));

    /// <summary>
    /// <c>GetCapacity(flag)</c>: how big the inventory is and how much is in it. A ship's hold is the ship's own capacity (dogma's); asked of a ship with no flag it is the
    /// cargo hold. A hangar holds a million cubic metres whatever is in it; asked for the cargo hold it answers what the reference does for an inventory that is no
    /// ship (5,000).
    /// </summary>
[Method("ListStations")]
    public async ValueTask<PyValue> ListStations(ServiceCall call)
    {
        var context = ContextOf(call);
        var character = context is { Kind: Kind.GlobalAssets } ? context.ItemId : call.Session.CharacterId;
        if (character is not { } characterId)
            return Py.Rowset(
                ["stationID", "solarSystemID", "typeID", "itemCount", "upkeepState"],
                []);

        var owned = await items.OwnedByAsync(characterId, call.Cancellation);
        var rows = owned
            .Where(item => item.Quantity != 0 && stations.Get(item.LocationId) is not null)
            .GroupBy(item => item.LocationId)
            .Select(group => stations.Get(group.Key) is { } station
                ? (Station: station, ItemCount: group.Count())
                : default)
            .Where(group => group.Station is not null)
            .OrderBy(group => group.Station!.SolarSystemId)
            .ThenBy(group => group.Station!.Id)
            .Select(group => Py.Tuple([
                InventoryWire.Id(group.Station!.Id),
                InventoryWire.Id(group.Station.SolarSystemId),
                new PyInt(group.Station.TypeId),
                new PyInt(group.ItemCount),
                PyNone.Instance
            ]));

        return Py.Rowset(
            ["stationID", "solarSystemID", "typeID", "itemCount", "upkeepState"],
            rows);
    }

    [Method("ListStationItems")]
    public async ValueTask<PyValue> ListStationItems(ServiceCall call)
    {
        var context = ContextOf(call);
        var character = context is { Kind: Kind.GlobalAssets } ? context.ItemId : call.Session.CharacterId;
        if (character is not { } characterId || IdOf(call.Arg(0)) is not { } station || stations.Get(station) is null)
            return new PyList([]);

        var stationItems = context is { Kind: Kind.GlobalAssets }
            ? await items.InLocationAsync(station, characterId, call.Cancellation)
            : await StationItemsAsync(call.Session, characterId, station, call.Cancellation);
        var rows = await RowsAsync(stationItems.Where(item => item.Quantity != 0), call.Cancellation);
        return new PyList([.. rows.Select(row => InventoryWire.PackedRow(row))]);
    }

    [Method("ListIncludingContainers")]
    public async ValueTask<PyValue> ListIncludingContainers(ServiceCall call)
    {
        if (ContextOf(call) is not { } context || call.Session.CharacterId is not { } character) return None;
        var pending = new Queue<ItemRow>(await ContentsAsync(context, call.Session, call.Cancellation));
        var seen = new HashSet<long>();
        var rows = new List<ItemRow>();
        while (pending.TryDequeue(out var row))
        {
            if (!seen.Add(row.ItemId)) continue;
            rows.Add(row);
            var contents = await items.InContainerAsync(row.ItemId, call.Cancellation);
            var visible = new List<Item>();
            foreach (var item in contents)
            {
                if (item.OwnerId == character
                    || await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Query, call.Cancellation))
                    visible.Add(item);
            }
            foreach (var child in await RowsAsync(visible, call.Cancellation))
                pending.Enqueue(child);
        }
        return new PyList([.. rows.Select(row => InventoryWire.PackedRow(row))]);
    }

    [Method("GetAssetWorth")]
    public async ValueTask<PyValue> GetAssetWorth(ServiceCall call)
    {
        const int PlexTypeId = 44992;
        const int BlueprintCategoryId = 9;
        var context = ContextOf(call);
        var character = context is { Kind: Kind.GlobalAssets } ? context.ItemId : call.Session.CharacterId;
        if (character is not { } characterId)
            return Py.Tuple([Py.Number(0d), Py.Number(0d)]);

        var owned = await items.OwnedByAsync(characterId, call.Cancellation);
        var typesById = await types.GetAsync(owned.Select(item => item.TypeId).Append(PlexTypeId), call.Cancellation);
        decimal assetWorth = 0m;
        decimal plexWorth = 0m;
        var plexUnitPrice = typesById.GetValueOrDefault(PlexTypeId)?.BasePrice ?? 0m;

        foreach (var item in owned)
        {
            if (item.Quantity == 0 || !typesById.TryGetValue(item.TypeId, out var type))
                continue;

            var units = item.Singleton ? 1 : Math.Max(0, item.Quantity);
            if (units == 0)
                continue;

            if (item.TypeId == PlexTypeId)
            {
                plexWorth += plexUnitPrice * units;
                continue;
            }

            if (type.CategoryId != BlueprintCategoryId && type.BasePrice > 0)
                assetWorth += type.BasePrice * units;
        }

        return Py.Tuple([
            Py.Number((double)Math.Round(assetWorth, MidpointRounding.AwayFromZero)),
            Py.Number((double)Math.Round(plexWorth, MidpointRounding.AwayFromZero))
        ]);
    }

    [Method("IsMyItemAtDockableLocation")]
    public async ValueTask<PyValue> IsMyItemAtDockableLocation(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character || IdOf(call.Arg(0)) is not { } itemId
            || await items.GetAsync(itemId, call.Cancellation) is not { OwnerId: var owner, LocationId: var location }
            || owner != character || stations.Get(location) is null)
            return PyBool.False;
        var askedLocation = IdOf(call.Arg(1));
        return askedLocation is null || stations.Get(askedLocation.Value) is null || askedLocation == location ? PyBool.True : PyBool.False;
    }

    /// <summary>
    /// The pilot's stack from its ship's hold into a customs office in its system (importExportUI.MoveFromInventory). Only the ship's hold is a
    /// source here: a station hangar is refused (shortcut: a station's hangar into an office in the station's system, when a walk needs it).
    /// The office's room is checked as the client checks it (NotEnoughSpace with the volume and the room left).
    /// </summary>
    private async ValueTask<PyValue> AddToCustomsOfficeAsync(ServiceCall call, long character, long itemId, long source, long officeId, long? quantity)
    {
        if (call.Session.GetInt("shipid") is not { } shipId || source != shipId || space?.SolarSystemOf(call.Session) is not { } system
            || await items.GetAsync(itemId, call.Cancellation) is not { } stack || stack.OwnerId != character || stack.LocationId != shipId
            || await items.GetAsync(officeId, call.Cancellation) is not { } office || office.LocationId != system)
            return None;
        var moving = stack.Singleton ? stack.Quantity : Math.Min(quantity ?? stack.Quantity, stack.Quantity);
        if (moving <= 0)
            return None;
        // the office is not a cargo container (no room of the container model): its room is its type's capacity less what is in it
        var used = await ships.VolumeOfAsync(await items.InContainerAsync(officeId, call.Cancellation), call.Cancellation);
        var required = await ships.VolumeOfAsync([stack with { Quantity = moving }], call.Cancellation);
        var available = Customs!.OfficeCapacity - used;
        if (required - available > 1e-5)
            throw MachoErrors.ThrowUserError("NotEnoughSpace", ("volume", new PyReal(required)), ("available", new PyReal(available)));
        var landed = await Customs!.MoveToOfficeAsync(call.Session, officeId, itemId, moving, call.Cancellation);
        return landed == 0 ? None : InventoryWire.Id(landed);
    }

    [Method("GetCapacity")]
    public async ValueTask<PyValue> GetCapacity(ServiceCall call)
    {
        if (ContextOf(call) is not { } context || call.Session.CharacterId is not { } character)
            return None;
        var flag = FlagOf(call);
        if (context.Kind is Kind.Structure or Kind.Hangar)
        {
            if (await ControlledStructureAsync(call.Session, context.ItemId, call.Cancellation) is { } controlled)
                return await StructureCapacityAsync(controlled, flag, call.Cancellation);
            if (context.Kind == Kind.Structure)
                return None;
        }
        if (context.Kind == Kind.Ship)
        {
            if (space is not null && space.IsInSpace(call.Session) && flag == CharacterShips.DroneBayFlag)
            {
                return droneBays is not null
                    && await droneBays.RoomAsync(call.Session, context.ItemId, flag.Value, call.Cancellation) is { } inSpaceRoom
                    ? KeyVal(inSpaceRoom.Capacity, inSpaceRoom.Used)
                    : None;
            }
            if (await items.GetAsync(context.ItemId, call.Cancellation) is not { } hull)
                return None;
            if (hull.OwnerId != character
                && !await CanAccessCorporationItemAsync(call.Session, hull, CorporationHangarOperation.Query, call.Cancellation))
                return None;
            if (flag == CharacterShips.DroneBayFlag && hull.OwnerId == character
                && !await IsDockedShipContextAsync(call.Session, character, context.ItemId, call.Cancellation))
                return None;
            return await ships.RoomAsync(hull.OwnerId, context.ItemId, flag ?? CharacterShips.CargoFlag, call.Cancellation) is { } room
                ? KeyVal(room.Capacity, room.Used)
                : None;
        }
        if (context.Kind == Kind.Space)
            return None;
        if (context.Kind == Kind.Container)
        {
            // a customs office is an NPC's: its room is its capacity less what is in it, for any pilot (the import window reads it)
            if (Customs is not null && await Customs.IsOfficeAsync(context.ItemId, call.Cancellation))
                return KeyVal(Customs.OfficeCapacity, await ships.VolumeOfAsync(await items.InContainerAsync(context.ItemId, call.Cancellation), call.Cancellation));
            if (await items.GetAsync(context.ItemId, call.Cancellation) is not { } container
                || container.OwnerId != character
                    && !await CanAccessCorporationItemAsync(call.Session, container, CorporationHangarOperation.Query, call.Cancellation))
                return None;
            return await ships.ContainerRoomAsync(context.ItemId, call.Cancellation) is { } inside
                ? KeyVal(inside.Capacity, inside.Used)
                : None;
        }
        var station = StationOf(call.Session);
        var inHangar = station is { } at ? await StationItemsAsync(call.Session, character, at, call.Cancellation) : [];
        var used = await ships.VolumeOfAsync(inHangar.Where(item => flag is null || item.Flag == flag), call.Cancellation);
        return KeyVal(flag == CharacterShips.CargoFlag ? BareCargoCapacity : HangarCapacity, used);
    }


    /// <summary>Remaining turret hardpoints, as queried from the inventory service by the fitting client.</summary>
    [Method("GetAvailableTurretSlots")]
    public async ValueTask<PyValue> GetAvailableTurretSlots(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character
            || await ships.LoadAsync(character, call.Cancellation) is not { } snapshot)
        {
            return new PyInt(0);
        }

        if (call.Session.GetInt("structureid") is { } structureId && structureId > 0 && call.Session.GetInt("shipid") == structureId)
        {
            if (await ControlledStructureAsync(call.Session, structureId, call.Cancellation) is not { } controlled)
                return new PyInt(0);
            // shortcut: base HUD hardpoints only; fitted structure weapon accounting belongs to ST-8.
            var world = new DogmaWorld(statics, character, structureId, structureId);
            world.Add(ItemDogma.Of(controlled.Structure, character, controlled.Structure.LocationId, controlled.Structure.Flag));
            return new PyInt(Math.Max(0L, (long)world.Get(structureId, BuiltInEffects.TurretSlotsLeft)));
        }
        var remaining = snapshot.World.Get(snapshot.Ship.Id, BuiltInEffects.TurretSlotsLeft);
        return new PyInt(Math.Max(0L, (long)remaining));
    }

    /// <summary>
    /// <c>StackAll(flag)</c>: the stacks of one type in the inventory become one. Answers True when something was stacked and None when nothing was; the client
    /// is told each stack that grew and each that went.
    /// </summary>
[Method("SetLabel")]
    public async ValueTask<PyValue> SetLabel(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } character || IdOf(call.Arg(0)) is not { } itemId
            || await items.GetAsync(itemId, call.Cancellation) is not { } item)
            return None;

        var owner = item.OwnerId;
        var type = await types.GetAsync(item.TypeId, call.Cancellation);
        var isShip = type is { CategoryId: ShipCategory };
        var station = StationOf(call.Session);
        var isDockedContainer = station is { } at
            && await ships.IsOwnedDockedContainerAsync(itemId, owner, at, call.Cancellation);
        if (owner != character)
        {
            if (owner != CorporationIdOf(call.Session))
                throw MachoErrors.ThrowUserError("ItemNotYours");
            if (await CorporationDivisionOfItemAsync(item, owner, call.Cancellation) is not { } division
                || division.StationId != station)
                return None;
            if (!await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Take, call.Cancellation))
                throw MachoErrors.ThrowUserError("CrpAccessDenied",
                    ("reason", Py.Tuple(new PyInt(101), new PyStr("UI/Corporations/AccessRestrictions/InsufficientRoles"))));
        }
        if (!isShip && !isDockedContainer)
            return None;

        var rawLabel = call.Arg(1) is PyNone ? null : JsSemantics.StringValue(call.Arg(1));
        var label = isShip
            ? CharacterShips.NormalizeName(rawLabel)
            : System.Text.RegularExpressions.Regex.Replace(rawLabel ?? "", "[\\r\\n]+", " ").Trim();
        if (!isShip && label is { Length: > 100 }) label = label[..100];
        if (string.IsNullOrEmpty(label))
            return None;

        // Refuse a stale authorization snapshot, and publish only the item returned after commit.
        if (await items.SetCustomNameAsync(item, label, call.Cancellation) is not { } updated)
            return None;
        if (call.Session.GetInt("shipid") == itemId)
            call.Session.Set("shipName", new PyStr(updated.CustomName!));
        if (isDockedContainer && type is not null)
            ShipNotifications.SendItemChange(call.Session,
                new ItemChanged(updated, item.LocationId, item.Flag, item.Quantity), value => Row(value, type));
        call.Session.SendNotification("OnCfgDataChanged", "charid", new PyStr("evelocations"),
            new PyList([InventoryWire.Id(updated.Id), new PyStr(updated.CustomName!), PyNone.Instance,
                new PyReal(0), new PyReal(0), new PyReal(0), PyNone.Instance]));
        if (isShip && space is not null && space.IsInSpace(call.Session))
            await space.RefreshShipNameAsync(call.Session, updated.Id, call.Cancellation);
        return None;
    }

[Method("MultiMerge")]
    public async ValueTask<PyValue> MultiMerge(ServiceCall call)
    {
        RequireMutableInventory(call);
        if (ContextOf(call) is not { } context || call.Session.CharacterId is not { } character
            || StationOf(call.Session) is not { } station || IdOf(call.Arg(1)) is not { } sourceContainer)
            return None;
        var destination = context.Kind switch
        {
            Kind.Hangar => station,
            Kind.Ship or Kind.Container => context.ItemId,
            _ => 0,
        };
        if (destination <= 0) return None;
        var corporationId = CorporationIdOf(call.Session);
        var operations = call.Arg(0) switch
        {
            PyList list => list.Items,
            PyTuple tuple => tuple.Items,
            _ => [],
        };
        var mergedAny = false;
        foreach (var operation in operations)
        {
            if (operation is not PyTuple { Items.Count: >= 3 } triple
                || IdOf(triple.Items[0]) is not { } sourceId || IdOf(triple.Items[1]) is not { } destinationId)
                continue;
            var quantity = IdOf(triple.Items[2]) ?? 0;
            if (await items.GetAsync(sourceId, call.Cancellation) is not { } sourceItem
                || await items.GetAsync(destinationId, call.Cancellation) is not { } destinationItem)
                continue;

            var ownerId = character;
            if (sourceItem.OwnerId != character || destinationItem.OwnerId != character)
            {
                if (corporationId <= 0 || sourceItem.OwnerId != corporationId || destinationItem.OwnerId != corporationId
                    || !await CanAccessCorporationItemAsync(call.Session, sourceItem, CorporationHangarOperation.Take, call.Cancellation)
                    || !await CanAccessCorporationItemAsync(call.Session, destinationItem, CorporationHangarOperation.Take, call.Cancellation))
                    continue;
                ownerId = corporationId;
            }
            var merged = await items.MergeAsync(sourceId, destinationId, quantity, ownerId, sourceContainer, destination, call.Cancellation);
            if (merged is null) continue;
            ShipNotifications.SendItemChange(call.Session,
                new ItemChanged(merged.DestinationAfter, merged.DestinationBefore.LocationId, merged.DestinationBefore.Flag, merged.DestinationBefore.Quantity),
                item => DogmaRow(item));
            var sourceAfter = merged.SourceAfter ?? merged.SourceBefore with { LocationId = CharacterShips.JunkLocation };
            ShipNotifications.SendItemChange(call.Session,
                new ItemChanged(sourceAfter, merged.SourceBefore.LocationId, merged.SourceBefore.Flag, merged.SourceBefore.Quantity),
                item => DogmaRow(item));
            mergedAny = true;
        }
        return mergedAny ? PyBool.True : None;
    }

    [Method("StackAll")]
    public async ValueTask<PyValue> StackAll(ServiceCall call)
    {
        RequireMutableInventory(call);
        if (ContextOf(call) is not { } context || context.Kind == Kind.Structure || call.Session.CharacterId is not { } character)
        {
            return None;
        }
        var container = context.Kind switch
        {
            Kind.Hangar => StationOf(call.Session),
            Kind.Space => null,
            _ => context.ItemId,
        };
        if (container is not { } place)
        {
            return None;
        }
        var flag = FlagOf(call) ?? context.Kind switch
        {
            Kind.Hangar => ItemFlags.Hangar,
            Kind.Container => CharacterShips.ContainerFlag,
            _ => CharacterShips.CargoFlag,
        };
        var changes = await ships.StackAllAsync(character, place, flag, call.Cancellation);
        foreach (var change in changes)
        {
            ShipNotifications.SendItemChange(call.Session, change, item => DogmaRow(item));
        }
        return changes.Count > 0 ? PyBool.True : None;
    }

    // ── moving items ─────────────────────────────────────────────────────────

    /// <summary>
    /// <c>Add(itemID, sourceLocationID, qty=None, flag=None)</c>: moves an item into this inventory: a module into a slot of the ship (or the first
    /// free one), a module out of its slot into the hangar, goods into the cargo hold. Answers the id of the item that moved, and the client is
    /// told what changed. What cannot be done is answered with a message or, for an item that is not there, with None.
    /// </summary>
private async ValueTask<bool> IsDockedShipContextAsync(ClientSession session, long characterId, long shipId, CancellationToken cancellationToken)
    {
        if (StationOf(session) is not { } station || await items.GetAsync(shipId, cancellationToken) is not { } ship)
        {
            return false;
        }
        return ship.OwnerId == characterId
            && ship.LocationId == station
            && await types.GetAsync(ship.TypeId, cancellationToken) is { CategoryId: ShipCategory };
    }

    [Method("Add")]
    public async ValueTask<PyValue> Add(ServiceCall call)
    {
        RequireMutableInventory(call);
        if (ContextOf(call) is not { } context
            || call.Session.CharacterId is not { } character
            || IdOf(call.Arg(0)) is not { } itemId
            || context.Kind is Kind.Space or Kind.Structure)
            return None;
        long? quantity = (call.Kwarg("qty") ?? call.Kwarg("quantity")) is { } asked ? IdOf(asked) : null;
        int? flag = call.Kwarg("flag") is { } flagValue ? Flag(flagValue) : null;
        var source = IdOf(call.Arg(1)) ?? 0;
        // a customs office in space takes the pilot's stacks from its ship's hold (the office is an inventory of its own, not a can)
        if (context.Kind == Kind.Container && Customs is not null && space is not null && space.IsInSpace(call.Session)
            && await Customs.IsOfficeAsync(context.ItemId, call.Cancellation))
            return await AddToCustomsOfficeAsync(call, character, itemId, source, context.ItemId, quantity);
        if (space is not null && space.IsInSpace(call.Session))
        {
            if (context.Kind == Kind.Ship && flag == CharacterShips.DroneBayFlag)
            {
                return droneBays is not null
                    && await droneBays.AddAsync(call.Session, itemId, source, context.ItemId, flag.Value, quantity, call.Cancellation) is { } drone
                    ? InventoryWire.Id(drone)
                    : None;
            }
            if (context.Kind == Kind.Ship && source == context.ItemId && flag is { } hold && CharacterShips.IsShipHoldFlag(hold))
            {
                switch (await ships.MoveAsync(character, itemId, source, context.ItemId, hold, quantity, call.Cancellation))
                {
                    case ShipChanged changed:
                        ShipNotifications.Send(call.Session, character, changed, DogmaRow, Py.FileTimeFromUnixMs(nowUnixMs()));
                        return InventoryWire.Id(changed.ItemId);
                    case ShipRefused refused:
                        throw ShipErrors.Of(refused, statics);
                    default:
                        return None;
                }
            }
            return await space.MoveAsync(call.Session, itemId, source, context.ItemId, quantity, flag, call.Cancellation) is { } moved
                ? InventoryWire.Id(moved)
                : None;
        }

        if (StationOf(call.Session) is not { } station
            || await items.GetAsync(itemId, call.Cancellation) is not { } item
            || item.LocationId != source)
            return None;
        var corporationId = CorporationIdOf(call.Session);
        long? sourceOwnerId = null;
        if (item.OwnerId != character)
        {
            if (corporationId <= 0 || item.OwnerId != corporationId
                || !await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Take, call.Cancellation))
                return None;
            sourceOwnerId = corporationId;
        }
        long? destinationOwnerId = null;
        if (context.Kind == Kind.Hangar && flag is { } corpDivision && ItemFlags.IsCorporationHangar(corpDivision))
        {
            if (corporationId <= 0
                || !await CorporationAccessAsync(character, corporationId, station, corpDivision, CorporationHangarOperation.Deposit, call.Cancellation))
                return None;
            destinationOwnerId = corporationId;
        }
        else if (context.Kind is Kind.Container or Kind.Ship
            && await items.GetAsync(context.ItemId, call.Cancellation) is { OwnerId: var targetOwner }
            && targetOwner != character)
        {
            if (corporationId <= 0 || targetOwner != corporationId
                || await items.GetAsync(context.ItemId, call.Cancellation) is not { } targetContainer
                || !await CanAccessCorporationItemAsync(call.Session, targetContainer, CorporationHangarOperation.Deposit, call.Cancellation))
                return None;
            destinationOwnerId = corporationId;
        }
        if (context.Kind == Kind.Ship && flag == CharacterShips.DroneBayFlag
            && !await IsDockedShipContextAsync(call.Session, character, context.ItemId, call.Cancellation))
            return None;
        ShipOutcome outcome;
        if (context.Kind == Kind.Container)
        {
            outcome = await ships.MoveContainerAsync(character, itemId, source, context.ItemId, quantity, flag, call.Cancellation, sourceOwnerId, destinationOwnerId);
        }
        else
        {
            var destination = context.Kind == Kind.Hangar ? station : context.ItemId;
            outcome = await ships.MoveAsync(character, itemId, source, destination, flag, quantity, call.Cancellation, sourceOwnerId, destinationOwnerId);
        }
        switch (outcome)
        {
            case ShipChanged changed:
                ShipNotifications.Send(call.Session, character, changed, DogmaRow, Py.FileTimeFromUnixMs(nowUnixMs()));
                return InventoryWire.Id(changed.ItemId);
            case ShipRefused refused:
                throw ShipErrors.Of(refused, statics);
            default:
                return None;
        }
    }

    /// <summary>
    /// <c>MultiAdd(itemIDs, sourceLocationID, flag=None)</c>: the loot window's "Loot All": what is in a can or a wreck beside the ship goes into its hold at once (room for all of it, or nothing moves).
    /// Answers True when something moved; a batch of which nothing could be moved is the message the client shows. Only the hold in space is served: elsewhere the answer is None.
    /// </summary>
    [Method("MultiAdd")]
    public async ValueTask<PyValue> MultiAdd(ServiceCall call)
    {
        RequireMutableInventory(call);
        if (ContextOf(call) is not { } context
            || context.Kind is not (Kind.Ship or Kind.Container or Kind.Hangar)
            || IdOf(call.Arg(1)) is not { } source)
            return None;
        IReadOnlyList<long> ids = call.Arg(0) switch
        {
            PyList list => [.. list.Items.Select(IdOf).OfType<long>().Distinct()],
            PyTuple tuple => [.. tuple.Items.Select(IdOf).OfType<long>().Distinct()],
            _ => [],
        };
        if (ids.Count == 0) return None;
        var quantityValue = call.Kwarg("qty");
        if (quantityValue is null or PyNone) quantityValue = call.Kwarg("quantity");
        long? quantity = quantityValue is not null && IdOf(quantityValue) is { } amount && amount > 0 ? amount : null;
        var flagValue = call.Kwarg("flag");
        var requestedFlag = flagValue is null ? context.FlagId : flagValue is PyNone ? 0L : IdOf(flagValue);
        if (requestedFlag is not { } flag)
            return None;
        var corporationId = CorporationIdOf(call.Session);
        var corporationDestination = context.Kind == Kind.Hangar && ItemFlags.IsCorporationHangar((int)flag);
        if (flag != 0 && !CharacterShips.IsShipHoldFlag((int)flag) && !corporationDestination)
            return None;

        if (space is not null && space.IsInSpace(call.Session))
        {
            if (context.Kind != Kind.Ship || flag != CharacterShips.CargoFlag) return None;
            return await space.MoveAllAsync(call.Session, ids, source, context.ItemId, (int)flag, quantity, call.Cancellation) switch
            {
                true => PyBool.True,
                false => throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("None of the selected items could be moved."))),
                null => None,
            };
        }
        if (StationOf(call.Session) is not { } station || call.Session.CharacterId is not { } character) return None;

        long? destinationOwnerId = null;
        if (corporationDestination)
        {
            if (corporationId <= 0
                || !await CorporationAccessAsync(character, corporationId, station, (int)flag,
                    CorporationHangarOperation.Deposit, call.Cancellation))
                return None;
            destinationOwnerId = corporationId;
        }
        else if (context.Kind is Kind.Ship or Kind.Container
            && await items.GetAsync(context.ItemId, call.Cancellation) is { } destination
            && destination.OwnerId != character)
        {
            if (!await CanAccessCorporationItemAsync(call.Session, destination, CorporationHangarOperation.Deposit, call.Cancellation))
                return None;
            destinationOwnerId = destination.OwnerId;
        }

        var destinationId = context.Kind == Kind.Hangar ? station : context.ItemId;
        var moved = false;
        foreach (var itemId in ids)
        {
            if (await items.GetAsync(itemId, call.Cancellation) is not { } item || item.LocationId != source)
                continue;
            long? sourceOwnerId = null;
            if (item.OwnerId != character)
            {
                if (corporationId <= 0 || item.OwnerId != corporationId
                    || !await CanAccessCorporationItemAsync(call.Session, item, CorporationHangarOperation.Take, call.Cancellation))
                    return None;
                sourceOwnerId = corporationId;
            }
            var outcome = context.Kind == Kind.Container
                ? await ships.MoveContainerAsync(character, itemId, source, destinationId, quantity, (int)flag,
                    call.Cancellation, sourceOwnerId, destinationOwnerId)
                : await ships.MoveAsync(character, itemId, source, destinationId, (int)flag, quantity,
                    call.Cancellation, sourceOwnerId, destinationOwnerId);
            switch (outcome)
            {
                case ShipChanged changed:
                    ShipNotifications.Send(call.Session, character, changed, DogmaRow, Py.FileTimeFromUnixMs(nowUnixMs()));
                    moved = true;
                    break;
                case ShipRefused refused:
                    throw ShipErrors.Of(refused, statics);
            }
        }
        return moved
            ? PyBool.True
            : throw MachoErrors.ThrowUserError("CustomNotify", ("notify", new PyStr("None of the selected items could be moved.")));
    }

    private ItemRow DogmaRow(Item item) =>
        new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.Flag, item.Singleton ? -1 : item.Quantity, statics.Type(item.TypeId)?.GroupId ?? 0, statics.Type(item.TypeId)?.CategoryId ?? 0);

    // ── binding by the inventory's own name ──────────────────────────────────

    /// <summary>Processes station-hangar repackaging requests.</summary>

    internal async ValueTask<PyValue> RepackageItems(ServiceCall call)
    {
        if (call.Session.CharacterId is not { } characterId
            || StationOf(call.Session) is not { } dockedStation
            || call.Arg(0) is not PyDict byStation)
            return None;

        var requests = new List<RepackageItemRequest>();
        var corporationId = CorporationIdOf(call.Session);
        foreach (var (stationKey, rawItems) in byStation.Entries)
        {
            if (IdOf(stationKey) != dockedStation)
                continue;
            var requestedStation = dockedStation;
            var values = rawItems switch
            {
                PyList list => list.Items,
                PyTuple tuple => tuple.Items,
                _ => [],
            };
            foreach (var raw in values)
            {
                var parts = raw switch
                {
                    PyTuple tuple => tuple.Items,
                    PyList list => list.Items,
                    _ => [],
                };
                if (parts.Count == 0 || IdOf(parts[0]) is not { } itemId || itemId <= 0)
                    continue;
                var itemLocationId = parts.Count > 1
                    ? IdOf(parts[1]) ?? requestedStation
                    : requestedStation;
                if (await items.GetAsync(itemId, call.Cancellation) is not { } item
                    || item.LocationId != itemLocationId)
                    continue;

                var personalHangar = item.OwnerId == characterId
                    && item.LocationId == dockedStation
                    && item.Flag == ItemFlags.Hangar;
                var corporationHangar = false;
                if (!personalHangar && corporationId > 0 && item.OwnerId == corporationId
                    && ItemFlags.IsCorporationHangar(item.Flag)
                    && await CorporationDivisionOfItemAsync(item, corporationId, call.Cancellation) is { } division
                    && division.StationId == dockedStation
                    && division.DivisionFlag == item.Flag)
                {
                    corporationHangar = await CorporationAccessAsync(
                        characterId, corporationId, dockedStation, item.Flag,
                        CorporationHangarOperation.Take, call.Cancellation);
                }
                if (!personalHangar && !corporationHangar)
                    continue;

                requests.Add(new RepackageItemRequest(
                    itemId, requestedStation, item.OwnerId, item.LocationId, item.Flag));
            }
        }

        if (requests.Count == 0)
            return None;
        var destroyable = call.Kwarg("destroyable") switch
        {
            PyBool value => value.Value,
            PyInt value => value.Value == 1,
            PyLong value => value.Value == 1,
            _ => false,
        };
        var (changes, confirmationItemIds) = await ships.RepackageItemsAsync(
            characterId, requests, destroyable, call.Cancellation);
        foreach (var changed in changes)
            ShipNotifications.Send(call.Session, characterId, changed, DogmaRow, Py.FileTimeFromUnixMs(nowUnixMs()));

        if (confirmationItemIds.Count == 1)
            throw MachoErrors.ThrowUserError("ConfirmRepackageSomethingWithUpgrades");
        if (confirmationItemIds.Count > 1)
        {
            throw MachoErrors.ThrowUserError(
                "ConfirmRepackageMultipleItemsWithUpgrades",
                ("itemIDs", new PyList([.. confirmationItemIds.Select(InventoryWire.Id)])));
        }
        return None;
    }



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
        var bindArguments = call.Arg(0) switch
        {
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            _ => []
        };
        var parameters = bindArguments.Count > 0 ? bindArguments[0] : call.Arg(0);
        var sessionCharacterId = call.Session.CharacterId;
        var globalAssets = sessionCharacterId is { } boundCharacterId
            && bindArguments.Count > 1
            && IdOf(bindArguments[0]) == boundCharacterId
            && IdOf(bindArguments[1]) == 10002;
        var opened = globalAssets
            ? Bind(new Context(Kind.GlobalAssets, call.Session.CharacterId!.Value, 0), call.Session)
            : await ((IService)this).CallAsync(new ServiceCall
            {
                Method = IdOf(parameters) == ContainerHangar ? "GetInventory" : "GetInventoryFromId",
                Args = [parameters],
                Session = call.Session,
                CallId = call.CallId,
                Cancellation = call.Cancellation,
            });
        if (opened is not PySubStruct)
            return None;

        PyValue result = None;
        if (call.Arg(1) is PyTuple { Items.Count: > 0 } nested && JsSemantics.StringValue(nested.Items[0]) is { } method)
        {
            var oid = JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)opened).Value).Decoded!).Items[0]);
            result = await ((IService)this).CallAsync(new ServiceCall
            {
                Method = method,
                Args = nested.Items.Count > 1 && nested.Items[1] is PyTuple or PyList
                    ? nested.Items[1] is PyTuple argsTuple ? argsTuple.Items : ((PyList)nested.Items[1]).Items
                    : [],
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
