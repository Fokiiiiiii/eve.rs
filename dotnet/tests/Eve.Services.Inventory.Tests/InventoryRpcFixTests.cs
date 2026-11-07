using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;

namespace Eve.Services.Inventory.Tests;

/// <summary>RPC contracts over real item state; the parent runs these PostgreSQL regressions.</summary>
public class InventoryRpcFixTests
{
    private static async Task<PyValue> Call(Rig rig, ClientSession session, string method,
        string? bound, PyValue[] args, params (string Name, PyValue Value)[] kwargs) =>
        await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = method, Session = session, BoundObjectId = bound, Args = args,
            Kwargs = new PyDict([.. kwargs.Select(pair =>
                new KeyValuePair<PyValue, PyValue>(new PyStr(pair.Name), pair.Value))]),
        });

    private static long Number(PyValue value) => value switch
    {
        PyInt n => n.Value, PyLong n => n.Value, _ => throw new InvalidOperationException(),
    };

    private static Dictionary<string, PyValue> Fields(PyValue row) =>
        Assert.IsType<PyPackedRow>(row).Fields().ToDictionary(field => field.Key, field => field.Value);

    private static IReadOnlyList<PyValue> Rows(PyValue rows) => Assert.IsType<PyList>(rows).Items;

    private static IReadOnlyList<PyValue> SetRows(PyValue value)
    {
        var set = Assert.IsType<PyObjectEx>(value);
        Assert.Equal(ObjectExKind.Ex1, set.Kind);
        var header = Assert.IsType<PyTuple>(set.Header);
        Assert.Equal(new PyToken("__builtin__.set"), header.Items[0]);
        return Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyTuple>(header.Items[1]).Items)).Items;
    }

    private sealed class Sink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public Action<PyValue>? OnSend { get; set; }
        public void Send(PyValue packet) { OnSend?.Invoke(packet); Packets.Add(packet); }
    }

    private static IEnumerable<PyValue> Descendants(PyValue value)
    {
        yield return value;
        IEnumerable<PyValue> children = value switch
        {
            PyObject obj => [obj.Args],
            PyPackedRow row => row.Fields().Select(field => field.Value),
            PyBuffer buffer => [MarshalCodec.Decode(buffer.Value.ToArray())],
            PyTuple tuple => tuple.Items,
            PyList list => list.Items,
            PyDict dict => dict.Entries.SelectMany(pair => new[] { pair.Key, pair.Value }),
            _ => [],
        };
        foreach (var child in children)
            foreach (var nested in Descendants(child)) yield return nested;
    }

    private static bool Named(PyValue packet, string name) =>
        Descendants(packet).OfType<PyStr>().Any(text => text.Value == name);

    private static ClientSession Session(Sink sink, long? corporation = null)
    {
        var session = new ClientSession(sink) { SessionId = 41, ClientId = 41, UserId = 34, CharacterId = Rig.Character };
        session.Set("stationid", new PyInt(Rig.Station));
        if (corporation is { } corp) session.Set("corporationID", new PyLong(corp));
        return session;
    }

    [PgFact]
    public async Task Flags_kwargs_populate_the_uncached_client_flags_and_positional_flags_take_precedence()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        await rig.AddAsync(Rig.Tritanium, Rig.Character + 1, Rig.Station, quantity: 10);
        var session = Rig.Session();
        var bound = await rig.HangarAsync(session);
        var flags = new PyList([new PyInt(ItemFlags.Hangar), new PyInt(ItemFlags.Hangar)]);
        foreach (var args in new[] { Array.Empty<PyValue>(), new PyValue[] { PyNone.Instance } })
        {
            var rows = SetRows(await Call(rig, session, "ListByFlags", bound, args, ("flags", flags)));
            Assert.Equal(item, Number(Fields(Assert.Single(rows))["itemID"]));
        }
        Assert.Empty(SetRows(await Call(rig, session, "ListByFlags", bound, [new PyList([])], ("flags", flags))));
        Assert.Empty(SetRows(await Call(rig, session, "ListByFlags", bound, [], ("flags", new PyInt(ItemFlags.Hangar)))));
        Assert.Equal(PyNone.Instance, await Call(rig, Rig.Session(id: 99), "ListByFlags", bound, [], ("flags", flags)));
    }

    [PgFact]
    public async Task ListByFlags_returns_a_python_set_on_the_wire()
    {
        await using var rig = await Rig.CreateAsync();
        var item = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var session = Session(new Sink());
        var bound = await rig.HangarAsync(session);

        var response = await Call(rig, session, "ListByFlags", bound, [new PyTuple([new PyInt(ItemFlags.Hangar)])]);
        var set = Assert.IsType<PyObjectEx>(response);
        Assert.Equal(ObjectExKind.Ex1, set.Kind);
        var header = Assert.IsType<PyTuple>(set.Header);
        Assert.Equal(new PyToken("__builtin__.set"), header.Items[0]);
        var constructorArgs = Assert.IsType<PyTuple>(header.Items[1]);
        var rows = Assert.IsType<PyList>(Assert.Single(constructorArgs.Items));
        Assert.Equal(item, Number(Fields(Assert.Single(rows.Items))["itemID"]));
        Assert.Empty(set.List);
        Assert.Empty(set.Dict);
        var decoded = Assert.IsType<PyObjectEx>(MarshalCodec.Decode(MarshalCodec.Encode(response)));
        Assert.Equal(ObjectExKind.Ex1, decoded.Kind);
        Assert.Equal(item, Number(Fields(Assert.Single(SetRows(decoded)))["itemID"]));

        var empty = await Call(rig, session, "ListByFlags", bound, [new PyTuple([new PyInt(ItemFlags.Cargo)])]);
        Assert.Equal(InventoryWire.EmptySet(), empty);
    }

    [PgFact]
    public async Task GetInventory_binds_the_docked_capsuleer_deliveries_container()
    {
        var canManageDeliveries = false;
        await using var rig = await Rig.CreateAsync(activeShip: true,
            capsuleerDeliveriesAccess: (character, corporation, _) =>
                ValueTask.FromResult(character == Rig.Character && corporation == Rig.Corporation && canManageDeliveries));
        var delivered = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, Rig.Station,
            flag: ItemFlags.CapsuleerDeliveries);
        const long structureId = 900_001_888;
        var structureDelivery = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, structureId,
            flag: ItemFlags.CapsuleerDeliveries);
        await rig.AddAsync(Rig.Tritanium, Rig.Corporation, Rig.OtherStation,
            flag: ItemFlags.CapsuleerDeliveries);
        await rig.AddAsync(Rig.Tritanium, Rig.Corporation, Rig.Station, flag: ItemFlags.CorpDeliveries);
        var session = Session(new Sink(), Rig.Corporation);

        var bound = await rig.CallAsync(session, "GetInventory", null, new PyInt(10015));
        var oid = Rig.OidOf(bound);
        Assert.Equal(InventoryWire.EmptySet(), await Call(rig, session, "List", oid, [new PyInt(ItemFlags.CapsuleerDeliveries)]));

        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var shipInventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        var denied = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiAdd", shipInventory,
            [new PyList([InventoryWire.Id(delivered)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag))));
        Assert.Equal("CustomNotify", denied.Error.UserErrorMessage);
        Assert.Equal((Rig.Corporation, Rig.Station, ItemFlags.CapsuleerDeliveries),
            ((await rig.Items.GetAsync(delivered))!.OwnerId, (await rig.Items.GetAsync(delivered))!.LocationId, (await rig.Items.GetAsync(delivered))!.Flag));

        canManageDeliveries = true;
        var visible = Assert.IsType<PyList>(await Call(rig, session, "List", oid,
            [new PyInt(ItemFlags.CapsuleerDeliveries)]));
        Assert.Equal(delivered, Number(Fields(Assert.Single(visible.Items))["itemID"]));

        var structureSession = Rig.Session(station: null, corporation: Rig.Corporation);
        structureSession.Set("structureid", InventoryWire.Id(structureId));
        var structureInventory = Rig.OidOf(await rig.CallAsync(structureSession, "GetInventory", null,
            new PyInt(InvBrokerService.ContainerStructure)));
        var structureRows = SetRows(await Call(rig, structureSession, "ListByFlags", structureInventory,
            [new PyList([new PyInt(ItemFlags.CapsuleerDeliveries)])]));
        Assert.Equal(structureDelivery, Number(Fields(Assert.Single(structureRows))["itemID"]));

        Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", shipInventory,
            [new PyList([InventoryWire.Id(delivered)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag))));
        var moved = (await rig.Items.GetAsync(delivered))!;
        Assert.Equal((Rig.Character, ship, CharacterShips.CargoFlag), (moved.OwnerId, moved.LocationId, moved.Flag));

        Assert.IsType<PySubStruct>(bound);
    }

    [PgFact]
    public async Task MultiAdd_skips_a_denied_corporation_item_and_moves_later_personal_items()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var first = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 4);
        var denied = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, Rig.Station,
            flag: ItemFlags.CorpSAG1, quantity: 4);
        var last = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 6);
        var sink = new Sink();
        var session = Session(sink, Rig.Corporation);
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));

        var response = await Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(first), InventoryWire.Id(denied), InventoryWire.Id(last)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)));

        Assert.Equal(PyBool.True, response);
        Assert.Equal((ship, 4L, Rig.Character),
            ((await rig.Items.GetAsync(first))!.LocationId, (await rig.Items.GetAsync(first))!.Quantity, (await rig.Items.GetAsync(first))!.OwnerId));
        Assert.Equal((Rig.Station, 4L, Rig.Corporation),
            ((await rig.Items.GetAsync(denied))!.LocationId, (await rig.Items.GetAsync(denied))!.Quantity, (await rig.Items.GetAsync(denied))!.OwnerId));
        Assert.Equal((ship, 6L, Rig.Character),
            ((await rig.Items.GetAsync(last))!.LocationId, (await rig.Items.GetAsync(last))!.Quantity, (await rig.Items.GetAsync(last))!.OwnerId));
        var itemChanges = sink.Packets.Where(packet => Named(packet, "OnItemChange")).ToArray();
        Assert.Equal(2, itemChanges.Length);
        Assert.Equal([first, last], itemChanges.SelectMany(Descendants).OfType<PyPackedRow>()
            .Select(row => Number(Fields(row)["itemID"])).Distinct().ToArray());
    }

    [PgFact]
    public async Task GetAssetWorth_counts_dockable_asset_roots_only_and_keeps_fractional_isk()
    {
        await using var rig = await Rig.CreateAsync();
        const int fractionalPriceTypeId = 353774; // V24.01 SDE types: unpublished 20GJ Neutron Blaster, basePrice 16873.5.
        const long systemId = 30_100_032;
        var catalog = new ItemTypeCatalog(new SdeReader(rig.Source));
        var prices = await catalog.GetAsync([fractionalPriceTypeId, Rig.Cockroach]);
        var fractionalPrice = Assert.IsType<ItemType>(prices.GetValueOrDefault(fractionalPriceTypeId)).BasePrice;
        var shipPrice = Assert.IsType<ItemType>(prices.GetValueOrDefault(Rig.Cockroach)).BasePrice;
        Assert.NotEqual(decimal.Truncate(fractionalPrice), fractionalPrice);

        var dockedItem = await rig.AddAsync(fractionalPriceTypeId, Rig.Character, Rig.Station);
        var dockedShip = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var dockedCargo = await rig.AddAsync(fractionalPriceTypeId, Rig.Character, dockedShip,
            flag: CharacterShips.CargoFlag, quantity: 2);
        var spaceShip = await rig.AddAsync(Rig.Cockroach, Rig.Character, systemId, singleton: true);
        var spaceCargo = await rig.AddAsync(fractionalPriceTypeId, Rig.Character, spaceShip,
            flag: CharacterShips.CargoFlag, quantity: 3);
        Assert.All([dockedItem, dockedShip, dockedCargo, spaceShip, spaceCargo], id => Assert.True(id > 0));

        var session = Session(new Sink());
        var binding = Assert.IsType<PyTuple>(await rig.CallAsync(session, "MachoBindObject", null,
            new PyTuple([InventoryWire.Id(Rig.Character), new PyInt(10002)]),
            new PyTuple([new PyStr("ListStations"), new PyTuple([]), PyNone.Instance])));
        var bound = Rig.OidOf(binding.Items[0]);
        var response = Assert.IsType<PyTuple>(await Call(rig, session, "GetAssetWorth", bound, []));

        Assert.Equal(new PyReal((double)Math.Round(fractionalPrice * 3m + shipPrice, 2, MidpointRounding.AwayFromZero)), response.Items[0]);
    }

    [PgFact]
    public async Task GetItems_refuses_more_than_500_requested_ids()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var session = Session(new Sink());
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "GetItems", bound,
            [new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyLong(id)).ToArray())]));

        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", refusal.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task ListByFlags_refuses_more_than_500_requested_flags()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Session(new Sink());
        var bound = await rig.HangarAsync(session);
        var flags = new PyList(Enumerable.Range(1, 501).Select(value => (PyValue)new PyInt(value)).ToArray());

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "ListByFlags", bound, [flags]));

        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", refusal.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task GetDamageForCrystals_refuses_malformed_shapes_and_nonpositive_item_ids()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Session(new Sink());

        var shape = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "GetDamageForCrystals", null, [PyNone.Instance]));
        var identifier = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "GetDamageForCrystals", null, [new PyList([new PyInt(-1)])]));

        Assert.Equal("INVENTORY_BULK_ARGUMENT_INVALID", shape.Error.UserErrorMessage);
        Assert.Equal("INVENTORY_ITEM_ID_INVALID", identifier.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task Inventory_bulk_rpcs_refuse_more_than_500_ids_before_work()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var session = Session(new Sink());
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        var values = new PyList(Enumerable.Range(1, 501).Select(id => (PyValue)new PyLong(id)).ToArray());

        var mergeRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiMerge", bound,
            [values, InventoryWire.Id(Rig.Station)]));
        var multiAddRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiAdd", bound,
            [values, InventoryWire.Id(Rig.Station)]));
        var crystalRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "GetDamageForCrystals", null, [values]));
        var openRefusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "ValidateItemListCanBeOpened", null, [values]));

        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", mergeRefusal.Error.UserErrorMessage);
        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", multiAddRefusal.Error.UserErrorMessage);
        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", crystalRefusal.Error.UserErrorMessage);
        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", openRefusal.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task MultiAdd_honors_both_quantity_names_and_publishes_only_committed_real_split_rows()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var sink = new Sink();
        var session = Session(sink);
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        foreach (var key in new[] { "qty", "quantity" })
        {
            var id = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
            sink.Packets.Clear();
            sink.OnSend = packet =>
            {
                foreach (var row in Descendants(packet).OfType<PyPackedRow>())
                {
                    var fields = Fields(row);
                    var saved = Task.Run(() => rig.Items.GetAsync(Number(fields["itemID"]))).GetAwaiter().GetResult();
                    Assert.NotNull(saved);
                    Assert.Equal(saved.Quantity, Number(fields["quantity"]));
                    Assert.Equal(saved.LocationId, Number(fields["locationID"]));
                    Assert.Equal(saved.Flag, Number(fields["flagID"]));
                    Assert.Equal(saved.OwnerId, Number(fields["ownerID"]));
                }
            };
            Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", bound,
                [new PyList([InventoryWire.Id(id), InventoryWire.Id(id)]), InventoryWire.Id(Rig.Station)],
                ("flag", new PyInt(CharacterShips.CargoFlag)), (key, key == "qty" ? new PyInt(3) : new PyLong(3))));
            Assert.Equal(7, (await rig.Items.GetAsync(id))!.Quantity);
            Assert.Equal(Rig.Station, (await rig.Items.GetAsync(id))!.LocationId);
            var cargo = (await rig.Items.InContainerAsync(ship)).Where(i => i.TypeId == Rig.Tritanium).ToArray();
            Assert.Contains(cargo, i => i.Id != id && i.Quantity == 3 && i.OwnerId == Rig.Character);
            var next = Rows(await rig.CallAsync(session, "List", bound, new PyInt(CharacterShips.CargoFlag)));
            Assert.Equal(cargo.Select(i => i.Id).Order(), next.Select(row => Number(Fields(row)["itemID"])).Order());
            Assert.Equal(2, sink.Packets.Count(packet => Named(packet, "OnItemChange")));
            sink.OnSend = null;
        }
    }

    [PgFact]
    public async Task MultiAdd_to_personal_hangar_accepts_the_client_hangar_flag()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var first = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, CharacterShips.CargoFlag, quantity: 10);
        var second = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, CharacterShips.CargoFlag, quantity: 20);
        var sink = new Sink();
        var session = Session(sink);
        session.Set("shipid", InventoryWire.Id(ship));
        var bound = await rig.HangarAsync(session);
        sink.Packets.Clear();

        Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(first), InventoryWire.Id(second)]), InventoryWire.Id(ship)],
            ("flag", new PyInt(ItemFlags.Hangar))));

        Assert.Equal((Rig.Station, ItemFlags.Hangar, 10L),
            ((await rig.Items.GetAsync(first))!.LocationId, (await rig.Items.GetAsync(first))!.Flag, (await rig.Items.GetAsync(first))!.Quantity));
        Assert.Equal((Rig.Station, ItemFlags.Hangar, 20L),
            ((await rig.Items.GetAsync(second))!.LocationId, (await rig.Items.GetAsync(second))!.Flag, (await rig.Items.GetAsync(second))!.Quantity));
        var notifications = sink.Packets.Where(packet => Named(packet, "OnItemChange")).ToArray();
        Assert.Equal(2, notifications.Length);
        Assert.Equal([first, second], notifications.Select(packet =>
            Number(Fields(Assert.Single(Descendants(packet).OfType<PyPackedRow>()))["itemID"])));
    }

    [PgFact]
    public async Task MultiAdd_rejects_nested_cargo_containers_before_moving_any_items()
    {
        await using var rig = await Rig.CreateAsync();
        var containerType = await ContainerType(rig);
        var container = await rig.AddAsync(containerType, Rig.Character, Rig.Station, singleton: true);
        var item = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        var nested = await rig.AddAsync(containerType, Rig.Character, Rig.Station, singleton: true);
        var sink = new Sink();
        var session = Session(sink);
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(container)));
        sink.Packets.Clear();

        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(item), InventoryWire.Id(nested)]), InventoryWire.Id(Rig.Station)]));

        Assert.Equal("CustomNotify", refusal.Error.UserErrorMessage);
        var payload = ((PyTuple)((PyTuple)((PyObjectEx)refusal.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("A cargo container cannot be placed inside another cargo container.",
            ((PyStr)((PyDict)payload[1]).Entries.Single().Value).Value);
        Assert.Empty(sink.Packets);
        Assert.Empty(await rig.Items.InContainerAsync(container));
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(item))!.LocationId);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(nested))!.LocationId);
    }

    [PgFact]
    public async Task MultiAdd_qty_precedence_null_fallback_and_invalid_amount_keep_existing_authority_rules()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var session = Rig.Session();
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        foreach (var (qty, amount) in new (PyValue, long)[] { (new PyInt(2), 2), (PyNone.Instance, 4), (new PyInt(0), 10) })
        {
            var id = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
            Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", bound,
                [new PyList([InventoryWire.Id(id)]), InventoryWire.Id(Rig.Station)],
                ("flag", new PyInt(CharacterShips.CargoFlag)), ("qty", qty), ("quantity", new PyInt(4))));
            var source = (await rig.Items.GetAsync(id))!;
            Assert.Equal(amount == 10 ? ship : Rig.Station, source.LocationId);
            Assert.Equal(amount == 10 ? 10 : 10 - amount, source.Quantity);
        }
        var tooSmall = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 2);
        var refusal = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(tooSmall)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)), ("qty", new PyInt(3))));
        Assert.Equal("CustomNotify", refusal.Error.UserErrorMessage);
        Assert.Equal(2, (await rig.Items.GetAsync(tooSmall))!.Quantity);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(tooSmall))!.LocationId);
    }

    [PgFact]
    public async Task MultiAdd_preserves_prior_commits_and_notifications_when_a_later_owner_is_refused()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var own = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        var foreign = await rig.AddAsync(Rig.Tritanium, Rig.Character + 1, Rig.Station, quantity: 10);
        var elsewhere = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.OtherStation, quantity: 10);
        var sink = new Sink();
        var session = Session(sink);
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        Assert.Equal(PyNone.Instance, await Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(elsewhere), InventoryWire.Id(own), InventoryWire.Id(foreign)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)), ("qty", new PyInt(3))));
        Assert.Equal(7, (await rig.Items.GetAsync(own))!.Quantity);
        Assert.Equal(10, (await rig.Items.GetAsync(foreign))!.Quantity);
        Assert.Equal(Rig.OtherStation, (await rig.Items.GetAsync(elsewhere))!.LocationId);
        Assert.Equal(2, sink.Packets.Count(packet => Named(packet, "OnItemChange")));
        Assert.Single(Rows(await rig.CallAsync(session, "List", bound, new PyInt(CharacterShips.CargoFlag))));
    }

    private static async Task<int> ContainerType(Rig rig) =>
        (await rig.Statics.TypesInGroupsAsync(["Cargo Container", "Secure Cargo Container", "Freight Container", "Audit Log Secure Container"]))
        .First(type => type.Attributes.GetValueOrDefault(rig.Statics.AttributeId("capacity")!.Value) > 0).TypeId;

    [PgFact]
    public async Task MultiAdd_into_a_docked_container_uses_the_same_real_stack_authority()
    {
        await using var rig = await Rig.CreateAsync();
        var container = await rig.AddAsync(await ContainerType(rig), Rig.Character, Rig.Station, singleton: true);
        var id = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        var session = Rig.Session();
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(container)));
        Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(id)]), InventoryWire.Id(Rig.Station)], ("qty", new PyInt(3))));
        Assert.Equal(7, (await rig.Items.GetAsync(id))!.Quantity);
        var moved = Assert.Single(await rig.Items.InContainerAsync(container));
        Assert.NotEqual(id, moved.Id);
        Assert.Equal((Rig.Character, 3L, 0), (moved.OwnerId, moved.Quantity, moved.Flag));
        Assert.Equal(moved.Id, Number(Fields(Assert.Single(Rows(await rig.CallAsync(session, "List", bound))))["itemID"]));
    }

    [PgFact]
    public async Task SetLabel_normalizes_ship_names_then_publishes_the_committed_location_name()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var sink = new Sink();
        var session = Session(sink);
        session.Set("shipid", InventoryWire.Id(ship));
        sink.OnSend = packet =>
        {
            if (!Named(packet, "OnCfgDataChanged")) return;
            var saved = Task.Run(() => rig.Items.GetAsync(ship)).GetAwaiter().GetResult()!;
            Assert.Equal("A B", saved.CustomName);
            var row = Assert.Single(Descendants(packet).OfType<PyList>(), value => value.Items.Count == 7);
            // Non-table Python str decodes to PyBytes; the config row itself remains a list.
            Assert.Equal(new PyList([InventoryWire.Id(ship), new PyBytes(System.Text.Encoding.UTF8.GetBytes(saved.CustomName!)),
                PyNone.Instance, new PyReal(0), new PyReal(0), new PyReal(0), PyNone.Instance]), row);
        };
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(ship), new PyStr("  A\r\nB  ")));
        Assert.Single(sink.Packets, packet => Named(packet, "OnCfgDataChanged"));
        Assert.Equal(new PyStr("A B"), session.Get("shipName"));
        sink.OnSend = null;
        sink.Packets.Clear();
        foreach (var empty in new PyValue[] { PyNone.Instance, new PyStr(" \r\n ") })
            Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(ship), empty));
        Assert.Empty(sink.Packets);
        Assert.Equal("A B", (await rig.Items.GetAsync(ship))!.CustomName);
        await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(ship), new PyStr(new string('x', 40)));
        Assert.Equal(new string('x', 32), (await rig.Items.GetAsync(ship))!.CustomName);
        var hangar = await rig.HangarAsync(session);
        Assert.Contains(Rows(await rig.CallAsync(session, "List", hangar)), row => Number(Fields(row)["itemID"]) == ship);
    }

    [PgFact]
    public async Task SetLabel_container_sends_item_change_before_cfg_and_refuses_foreign_ownership()
    {
        await using var rig = await Rig.CreateAsync();
        var type = await ContainerType(rig);
        var container = await rig.AddAsync(type, Rig.Character, Rig.Station, singleton: true);
        var foreign = await rig.AddAsync(type, Rig.Character + 1, Rig.Station, singleton: true);
        var remote = await rig.AddAsync(type, Rig.Character, Rig.OtherStation, singleton: true);
        var sink = new Sink();
        var session = Session(sink);
        sink.OnSend = packet =>
        {
            if (Named(packet, "OnItemChange") || Named(packet, "OnCfgDataChanged"))
                Assert.Equal(new string('c', 100), Task.Run(() => rig.Items.GetAsync(container)).GetAwaiter().GetResult()!.CustomName);
        };
        await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(container), new PyStr(" " + new string('c', 105) + " "));
        Assert.Equal(2, sink.Packets.Count);
        Assert.True(Named(sink.Packets[0], "OnItemChange"));
        Assert.True(Named(sink.Packets[1], "OnCfgDataChanged"));
        sink.OnSend = null;
        sink.Packets.Clear();
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(foreign), new PyStr("Denied")));
        Assert.Equal("ItemNotYours", refused.Error.UserErrorMessage);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(remote), new PyStr("Remote")));
        Assert.Null((await rig.Items.GetAsync(foreign))!.CustomName);
        Assert.Null((await rig.Items.GetAsync(remote))!.CustomName);
        Assert.Empty(sink.Packets);
    }

    [PgFact]
    public async Task SetLabel_corporation_container_requires_take_role_and_keeps_the_authoritative_owner()
    {
        var allowTake = false;
        await using var rig = await Rig.CreateAsync(corporationHangarAccess: (character, corp, station, division, operation, _) =>
            ValueTask.FromResult(character == Rig.Character && corp == Rig.Corporation && station == Rig.Station
                && division == ItemFlags.CorpSAG1 && operation == CorporationHangarOperation.Take && allowTake));
        var container = await rig.AddAsync(await ContainerType(rig), Rig.Corporation, Rig.Station, ItemFlags.CorpSAG1, singleton: true);
        var sink = new Sink();
        var session = Session(sink, Rig.Corporation);
        var denied = await Assert.ThrowsAsync<MachoWrappedException>(async () =>
            await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(container), new PyStr("Corp")));
        Assert.Equal("CrpAccessDenied", denied.Error.UserErrorMessage);
        Assert.Empty(sink.Packets);
        Assert.Null((await rig.Items.GetAsync(container))!.CustomName);
        allowTake = true;
        await rig.CallAsync(session, "SetLabel", null, InventoryWire.Id(container), new PyStr("Corp"));
        var saved = (await rig.Items.GetAsync(container))!;
        Assert.Equal((Rig.Corporation, Rig.Station, ItemFlags.CorpSAG1, "Corp"),
            (saved.OwnerId, saved.LocationId, saved.Flag, saved.CustomName));
        Assert.Equal(2, sink.Packets.Count);
    }

    [PgFact]
    public async Task Guarded_name_update_refuses_a_stale_item_instead_of_announcing_a_fabricated_rename()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var before = (await rig.Items.GetAsync(id))!;
        Assert.True(await rig.Items.SetCustomNameAsync(id, "Concurrent"));
        Assert.Null(await rig.Items.SetCustomNameAsync(before, "Stale"));
        Assert.Equal("Concurrent", (await rig.Items.GetAsync(id))!.CustomName);
        var current = (await rig.Items.GetAsync(id))!;
        Assert.Equal(current with { CustomName = "Committed" }, await rig.Items.SetCustomNameAsync(current, "Committed"));
    }

    [PgFact]
    public async Task Guarded_name_update_refuses_changed_custody_and_only_one_concurrent_writer_can_rename_a_snapshot()
    {
        await using var rig = await Rig.CreateAsync();
        var id = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var before = (await rig.Items.GetAsync(id))!;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveToOwnerAsync(connection, transaction, id, Rig.Character + 1, Rig.OtherStation, ItemFlags.Hangar);
            await transaction.CommitAsync();
        }
        Assert.Null(await rig.Items.SetCustomNameAsync(before, "Unauthorized"));
        var current = (await rig.Items.GetAsync(id))!;
        Assert.Null(current.CustomName);
        var results = await Task.WhenAll(rig.Items.SetCustomNameAsync(current, "First"), rig.Items.SetCustomNameAsync(current, "Second"));
        var written = Assert.Single(results.OfType<Item>());
        Assert.Equal(written, await rig.Items.GetAsync(id));
        Assert.Equal((Rig.Character + 1, Rig.OtherStation), (written.OwnerId, written.LocationId));
    }

    private static async Task<(long Ship, long Can, long First, long Second)> SpaceBatch(Rig rig)
    {
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var system = Rig.Stations.Get(Rig.Station)!.SolarSystemId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.MoveAsync(connection, transaction, ship, system, CharacterShips.InSpaceFlag);
            await transaction.CommitAsync();
        }
        var can = await rig.AddAsync(CharacterShips.CargoContainerTypeId, Rig.Character + 1, system,
            CharacterShips.ContainerFlag, singleton: true);
        var first = await rig.AddAsync(Rig.Tritanium, Rig.Character + 1, can, CharacterShips.ContainerFlag, 10);
        var second = await rig.AddAsync(Rig.Tritanium, Rig.Character + 1, can, CharacterShips.ContainerFlag, 10);
        return (ship, can, first, second);
    }

    [PgFact]
    public async Task Space_batch_quantity_splits_real_rows_and_transfers_only_the_selected_units_to_the_pilot()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var batch = await SpaceBatch(rig);
        var changed = Assert.IsType<ShipChanged>(await rig.Ships.MoveAllInSpaceAsync(Rig.Character,
            [batch.First, batch.Second, batch.First, 424242], batch.Can, CharacterShips.CargoFlag, quantity: 3));
        Assert.Equal(4, changed.Stacks!.Count);
        Assert.Contains(changed.Stacks, change => change.After.Id == changed.ItemId && change.After.LocationId == batch.Ship);
        foreach (var id in new[] { batch.First, batch.Second })
        {
            var remaining = (await rig.Items.GetAsync(id))!;
            Assert.Equal((Rig.Character + 1, batch.Can, 7L), (remaining.OwnerId, remaining.LocationId, remaining.Quantity));
        }
        var cargo = await rig.Items.InContainerAsync(batch.Ship);
        Assert.Equal(2, cargo.Count);
        Assert.All(cargo, item => Assert.Equal((Rig.Character, 3L, CharacterShips.CargoFlag), (item.OwnerId, item.Quantity, item.Flag)));
        foreach (var change in changed.Stacks)
            Assert.Equal(change.After, await rig.Items.GetAsync(change.After.Id));
    }

    [PgFact]
    public async Task Space_batch_capacity_is_checked_for_all_selected_units_before_any_item_is_changed()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var batch = await SpaceBatch(rig);
        var snapshot = (await rig.Ships.LoadAsync(Rig.Character))!;
        var capacity = snapshot.World.Get(batch.Ship, rig.Statics.AttributeId("capacity")!.Value);
        // Leave 0.05 m3: one selection of three units fits, two selections together do not.
        await rig.AddAsync(Rig.Tritanium, Rig.Character, batch.Ship, CharacterShips.CargoFlag,
            checked((long)Math.Round((capacity - 0.05) / 0.01)));
        var refused = Assert.IsType<ShipRefused>(await rig.Ships.MoveAllInSpaceAsync(Rig.Character,
            [batch.First, batch.Second], batch.Can, CharacterShips.CargoFlag, quantity: 3));
        Assert.Equal(ShipRefusal.NotEnoughCargoSpace, refused.Reason);
        foreach (var id in new[] { batch.First, batch.Second })
        {
            var untouched = (await rig.Items.GetAsync(id))!;
            Assert.Equal((batch.Can, 10L), (untouched.LocationId, untouched.Quantity));
        }
        Assert.Single(await rig.Items.InContainerAsync(batch.Ship));
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAllInSpaceAsync(Rig.Character,
            [batch.First, batch.Second], batch.Can, CharacterShips.CargoFlag, quantity: 2));
        Assert.All(await rig.Items.InContainerAsync(batch.Can), item => Assert.Equal(8, item.Quantity));
    }

    [PgFact]
    public async Task MultiAdd_keeps_the_first_split_committed_when_the_next_stack_exceeds_remaining_capacity()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var snapshot = (await rig.Ships.LoadAsync(Rig.Character))!;
        var ship = snapshot.Ship.Id;
        var capacity = snapshot.World.Get(ship, rig.Statics.AttributeId("capacity")!.Value);
        await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, CharacterShips.CargoFlag,
            checked((long)Math.Round((capacity - 0.05) / 0.01)));
        var first = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        var second = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        var sink = new Sink();
        var session = Session(sink);
        var bound = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiAdd", bound,
            [new PyList([InventoryWire.Id(first), InventoryWire.Id(second)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)), ("qty", new PyInt(4))));
        Assert.Equal("NotEnoughCargoSpace", refused.Error.UserErrorMessage);
        Assert.Equal(6, (await rig.Items.GetAsync(first))!.Quantity);
        Assert.Equal(10, (await rig.Items.GetAsync(second))!.Quantity);
        Assert.Equal(2, sink.Packets.Count(packet => Named(packet, "OnItemChange")));
        Assert.Equal(2, Rows(await rig.CallAsync(session, "List", bound, new PyInt(CharacterShips.CargoFlag))).Count);
    }

    [PgFact]
    public async Task MultiAdd_corporation_split_keeps_source_custody_and_requires_take_role_for_the_reverse_move()
    {
        var allowTake = false;
        await using var rig = await Rig.CreateAsync(activeShip: true, corporationHangarAccess: (character, corp, station, division, operation, _) =>
            ValueTask.FromResult(character == Rig.Character && corp == Rig.Corporation && station == Rig.Station
                && division == ItemFlags.CorpSAG1 && (operation != CorporationHangarOperation.Take || allowTake)));
        var id = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 10);
        var session = Rig.Session(corporation: Rig.Corporation);
        var hangar = await rig.HangarAsync(session);
        Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", hangar,
            [new PyList([InventoryWire.Id(id)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(ItemFlags.CorpSAG1)), ("qty", new PyInt(3))));
        var original = (await rig.Items.GetAsync(id))!;
        Assert.Equal((Rig.Character, 7L), (original.OwnerId, original.Quantity));
        var corporate = Assert.Single(await rig.Items.InContainerAsync(Rig.Station), item => item.OwnerId == Rig.Corporation);
        Assert.Equal((3L, ItemFlags.CorpSAG1), (corporate.Quantity, corporate.Flag));
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var cargo = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, InventoryWire.Id(ship)));
        var takeDenied = await Assert.ThrowsAsync<MachoWrappedException>(() => Call(rig, session, "MultiAdd", cargo,
            [new PyList([InventoryWire.Id(corporate.Id)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)), ("quantity", new PyInt(1))));
        Assert.Equal("CustomNotify", takeDenied.Error.UserErrorMessage);
        Assert.Equal(corporate, await rig.Items.GetAsync(corporate.Id));
        allowTake = true;
        Assert.Equal(PyBool.True, await Call(rig, session, "MultiAdd", cargo,
            [new PyList([InventoryWire.Id(corporate.Id)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)), ("quantity", new PyInt(1))));
        Assert.Equal((Rig.Corporation, 2L), ((await rig.Items.GetAsync(corporate.Id))!.OwnerId, (await rig.Items.GetAsync(corporate.Id))!.Quantity));
        var received = Assert.Single(await rig.Items.InContainerAsync(ship));
        Assert.Equal((Rig.Character, 1L), (received.OwnerId, received.Quantity));
    }

    private sealed class RouteSpace : Eve.Services.Ships.ISpaceContainers
    {
        public long? Quantity { get; private set; }
        public Func<ClientSession, long, Task>? Renamed { get; set; }
        public bool IsInSpace(ClientSession session) => true;
        public int? SolarSystemOf(ClientSession session) => Rig.Stations.Get(Rig.Station)!.SolarSystemId;
        public Task<Item?> OpenAsync(ClientSession session, long id, CancellationToken cancellationToken) => Task.FromResult<Item?>(null);
        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("MultiAdd must keep the batch path.");
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> ids, long sourceId, long destinationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Quantity must not be discarded.");
        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> ids, long sourceId, long destinationId,
            int? flag, long? quantity, CancellationToken cancellationToken)
        {
            Quantity = quantity;
            return Task.FromResult<bool?>(false);
        }
        public Task RefreshShipNameAsync(ClientSession session, long id, CancellationToken cancellationToken) =>
            Renamed?.Invoke(session, id) ?? Task.CompletedTask;
        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static IService Service(Rig rig, RouteSpace space) =>
        new InvBrokerService(rig.Items, new ItemTypeCatalog(new Eve.Sde.SdeReader(rig.Source)), Rig.Stations,
            rig.Ships, rig.Statics, () => Rig.ClockMs, space);

    [PgFact]
    public async Task MultiAdd_forwards_quantity_to_the_space_batch_callback_and_preserves_its_refusal()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var batch = await SpaceBatch(rig);
        var session = Rig.Session(station: null);
        session.Set("shipid", InventoryWire.Id(batch.Ship));
        var space = new RouteSpace();
        var service = Service(rig, space);
        var bound = Rig.OidOf(await service.CallAsync(new ServiceCall
        {
            Session = session, Method = "GetInventoryFromId", Args = [InventoryWire.Id(batch.Ship)],
        }));
        var refused = await Assert.ThrowsAsync<MachoWrappedException>(async () => await service.CallAsync(new ServiceCall
        {
            Session = session, Method = "MultiAdd", BoundObjectId = bound,
            Args = [new PyList([InventoryWire.Id(batch.First)]), InventoryWire.Id(batch.Can)],
            Kwargs = Py.Dict(("qty", new PyInt(3)), ("flag", new PyInt(CharacterShips.CargoFlag))),
        }));
        Assert.Equal("CustomNotify", refused.Error.UserErrorMessage);
        Assert.Equal(3L, space.Quantity);
        Assert.Equal((batch.Can, 10L), ((await rig.Items.GetAsync(batch.First))!.LocationId, (await rig.Items.GetAsync(batch.First))!.Quantity));
    }

    [PgFact]
    public async Task SetLabel_space_presentation_callback_reads_committed_name_after_the_cfg_notification()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var batch = await SpaceBatch(rig);
        var sink = new Sink();
        var session = new ClientSession(sink) { SessionId = 42, ClientId = 42, UserId = 34, CharacterId = Rig.Character };
        session.Set("shipid", InventoryWire.Id(batch.Ship));
        var called = false;
        var space = new RouteSpace
        {
            Renamed = async (_, id) =>
            {
                Assert.Equal(batch.Ship, id);
                Assert.Equal("Flying", (await rig.Items.GetAsync(id))!.CustomName);
                Assert.Single(sink.Packets, packet => Named(packet, "OnCfgDataChanged"));
                Assert.Equal(new PyStr("Flying"), session.Get("shipName"));
                called = true;
            },
        };
        await Service(rig, space).CallAsync(new ServiceCall
        {
            Session = session, Method = "SetLabel", Args = [InventoryWire.Id(batch.Ship), new PyStr("Flying")],
        });
        Assert.True(called);
    }
}
