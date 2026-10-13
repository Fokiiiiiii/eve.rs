using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
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
            var rows = Rows(await Call(rig, session, "ListByFlags", bound, args, ("flags", flags)));
            Assert.Equal(item, Number(Fields(Assert.Single(rows))["itemID"]));
        }
        Assert.Empty(Rows(await Call(rig, session, "ListByFlags", bound, [new PyList([])], ("flags", flags))));
        Assert.Empty(Rows(await Call(rig, session, "ListByFlags", bound, [], ("flags", new PyInt(ItemFlags.Hangar)))));
        Assert.Equal(PyNone.Instance, await Call(rig, Rig.Session(id: 99), "ListByFlags", bound, [], ("flags", flags)));
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
        Assert.Equal(PyNone.Instance, await Call(rig, session, "MultiAdd", cargo,
            [new PyList([InventoryWire.Id(corporate.Id)]), InventoryWire.Id(Rig.Station)],
            ("flag", new PyInt(CharacterShips.CargoFlag)), ("quantity", new PyInt(1))));
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
