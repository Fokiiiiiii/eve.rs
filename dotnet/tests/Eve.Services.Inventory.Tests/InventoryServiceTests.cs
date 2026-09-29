using Eve.Data;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Universe;
using Npgsql;

namespace Eve.Services.Inventory.Tests;

/// <summary>Skips a test, loudly, when there is no PostgreSQL to test against (see tools/dev/pg18.sh).</summary>
public sealed class PgFactAttribute : FactAttribute
{
    public PgFactAttribute()
    {
        if (Rig.Url is null)
        {
            Skip = "EVE_PG_URL is not set (see tools/dev/pg18.sh)";
        }
    }
}

/// <summary>The inventory broker over a scratch item schema and the loaded static data.</summary>
internal sealed class Rig : IAsyncDisposable
{
    public static readonly string? Url = Environment.GetEnvironmentVariable("EVE_PG_URL");

    public const long ClockMs = 1_700_000_000_123;
    public const long Character = 140_000_001;
    public const long Station = 60015249;
    public const long OtherStation = 60000610;

    public const int Ibis = 601;
    public const int Tritanium = 34;

    public static readonly StationCatalog Stations = new([
        new Universe.Station(Station, 92885, "Manifest V - AIR Laboratories Trade Center", 1000413, "AIR Laboratories", 500028, "AIR", 123, 40491241,
            30100032, "Manifest", 20010001, "Manifest District", 10001004, "Exordium", 1.0, 0.05, 4),
        new Universe.Station(OtherStation, 4023, "Paara I - Deep Core Mining Inc. Mining Outpost", 1000006, "Deep Core Mining Inc.", 500001, "Caldari State", 7, 40003891,
            30002775, "Paara", 20000407, "Peyiri", 10000033, "The Citadel", 0.78, 0.05, 4),
    ]);

    private readonly string _schema = $"inv_{Guid.NewGuid():N}"[..20];

    private Rig(NpgsqlDataSource source)
    {
        Source = source;
        Items = new PgItemStore(source, _schema);
        Service = new InvBrokerService(Items, new ItemTypeCatalog(new SdeReader(source)), Stations, () => ClockMs);
    }

    public NpgsqlDataSource Source { get; }

    public PgItemStore Items { get; }

    public InvBrokerService Service { get; }

    public static async Task<Rig> CreateAsync()
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        Assert.True(await new SdeReader(source).BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var rig = new Rig(source);
        await PgItemStore.MigrateAsync(source, rig._schema);
        return rig;
    }

    public async Task<long> AddAsync(int type, long owner, long location, int flag = ItemFlags.Hangar, long quantity = 1, bool singleton = false)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, quantity, singleton));
        await transaction.CommitAsync();
        return id;
    }

    public static ClientSession Session(long? station = Station, long? character = Character, long id = 1)
    {
        var session = new ClientSession(new Sink()) { SessionId = id, ClientId = id, UserId = 34 };
        if (station is { } s)
        {
            session.Set("stationid", new PyInt(s));
        }
        if (character is { } c)
        {
            session.CharacterId = c;
        }
        return session;
    }

    public async Task<PyValue> CallAsync(ClientSession session, string method, string? bound = null, params PyValue[] args) =>
        await ((IService)Service).CallAsync(new ServiceCall { Method = method, Args = args, Session = session, BoundObjectId = bound });

    /// <summary>Opens the hangar as the client does and returns the bound object's name.</summary>
    public async Task<string> HangarAsync(ClientSession session) => OidOf(await CallAsync(session, "GetInventory", null, new PyInt(InvBrokerService.ContainerHangar)));

    public static string OidOf(PyValue bound) =>
        JsSemantics.StringValue(((PyTuple)((PySubStream)((PySubStruct)bound).Value).Decoded!).Items[0])!;

    public async ValueTask DisposeAsync()
    {
        await using var command = Source.CreateCommand($"DROP SCHEMA IF EXISTS {_schema} CASCADE");
        await command.ExecuteNonQueryAsync();
        await Source.DisposeAsync();
    }

    private sealed class Sink : IPacketSink
    {
        public void Send(PyValue packet)
        {
        }
    }
}

public class InventoryWireTests
{
    [Fact]
    public void The_descriptor_lists_the_stored_columns_and_the_two_the_client_works_out()
    {
        var header = (PyTuple)((PyObjectEx)InventoryWire.DescriptorHeader).Header;
        Assert.Equal(new PyToken("blue.DBRowDescriptor"), header.Items[0]);
        var columns = (PyTuple)((PyTuple)header.Items[1]).Items[0];
        Assert.Equal(
            [("itemID", 20L), ("typeID", 3), ("ownerID", 3), ("locationID", 20), ("flagID", 2), ("quantity", 3), ("groupID", 3), ("categoryID", 3), ("customInfo", 129)],
            columns.Items.Select(c => (JsSemantics.StringValue(((PyTuple)c).Items[0])!, ((PyInt)((PyTuple)c).Items[1]).Value)));
        Assert.Equal(["stacksize", "singleton"], ((PyList)header.Items[2]).Items.Select(v => JsSemantics.StringValue(((PyTuple)v).Items[0])!));
    }

    [Fact]
    public void A_packed_row_survives_the_wire_and_the_client_can_work_out_the_stack_and_the_singleton()
    {
        var ship = new ItemRow(1_990_000_001, 601, 140_000_001, 60015249, 4, -1, 237, 6);
        var stack = new ItemRow(1_990_000_002, 34, 140_000_001, 60015249, 4, 1000, 18, 4);
        foreach (var (item, stackSize, singleton) in new[] { (ship, 1L, 1L), (stack, 1000L, 0L) })
        {
            var row = (PyPackedRow)MarshalCodec.Decode(MarshalCodec.Encode(InventoryWire.PackedRow(item)));
            var fields = row.Fields().ToDictionary(f => f.Key, f => f.Value);
            Assert.Equal(item.ItemId, fields["itemID"] is PyLong wide ? wide.Value : ((PyInt)fields["itemID"]).Value); // an id that fits comes back as an int
            Assert.Equal(new PyInt(item.Quantity), fields["quantity"]);
            Assert.Equal(new PyInt(stackSize), fields["stacksize"]);
            Assert.Equal(new PyInt(singleton), fields["singleton"]);
            Assert.Equal((item.TypeId, item.GroupId, item.CategoryId), (((PyInt)fields["typeID"]).Value, ((PyInt)fields["groupID"]).Value, ((PyInt)fields["categoryID"]).Value));
        }
    }

    [Fact]
    public void An_id_is_an_int_when_it_fits_and_a_long_when_it_does_not_as_the_reference_writes_it()
    {
        Assert.Equal(new PyInt(60015249), InventoryWire.Id(60015249));
        Assert.Equal(new PyLong(9_988_400_000_662), InventoryWire.Id(9_988_400_000_662));
        Assert.Equal(new PyInt(-1), InventoryWire.Id(-1));
    }

    [Fact]
    public void A_util_row_has_its_header_and_a_line_and_says_singleton_even_for_a_stack()
    {
        var row = (PyObject)InventoryWire.UtilRow(new ItemRow(5, 34, 7, 9, 4, 1000, 18, 4));
        Assert.Equal("util.Row", JsSemantics.StringValue(row.Name));
        var parts = ((PyDict)row.Args).Entries;
        Assert.Equal(11, ((PyTuple)parts[0].Value).Items.Count);
        Assert.Equal(
            new PyTuple([new PyInt(5), new PyInt(34), new PyInt(7), new PyInt(9), new PyInt(4), new PyInt(1000), new PyInt(18), new PyInt(4), new PyStr(""), new PyInt(1000), new PyInt(1)]),
            parts[1].Value);
    }

    [Fact]
    public void A_rowset_has_a_header_the_row_class_and_a_tuple_for_each_line()
    {
        var rowset = (PyObject)InventoryWire.Rowset([new ItemRow(5, 601, 7, 9, 4, -1, 237, 6)]);
        var parts = ((PyDict)rowset.Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(["header", "RowClass", "lines"], ((PyDict)rowset.Args).Entries.Select(e => JsSemantics.StringValue(e.Key)!));
        Assert.Equal(new PyToken("util.Row"), parts["RowClass"]);
        Assert.Equal(
            new PyList([new PyTuple([new PyInt(5), new PyInt(601), new PyInt(7), new PyInt(9), new PyInt(4), new PyInt(-1), new PyInt(237), new PyInt(6), new PyStr(""), new PyInt(1), new PyInt(1)])]),
            parts["lines"]);
    }

    [Fact]
    public void An_empty_answer_for_a_flag_is_a_python_set()
    {
        var set = (PyObjectEx)InventoryWire.EmptySet();
        Assert.Equal(new PyToken("__builtin__.set"), ((PyTuple)set.Header).Items[0]);
        Assert.Equal(new PyTuple([new PyList([])]), ((PyTuple)set.Header).Items[1]);
    }
}

public class InvBrokerTests
{
    private static PyValue[] Values(PyValue row) => [.. ((PyPackedRow)row).Values];

    private static PyPackedRow[] Rows(PyValue list) => [.. ((PyList)list).Items.Cast<PyPackedRow>()];

    [PgFact]
    public async Task The_hangar_is_handed_out_as_a_bound_object_for_the_container_the_station_or_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Rig.Session();
        foreach (var argument in new PyValue[] { new PyInt(InvBrokerService.ContainerHangar), new PyInt(Rig.Station), PyNone.Instance })
        {
            var bound = await rig.CallAsync(session, "GetInventory", null, argument);
            var oid = (PyTuple)((PySubStream)((PySubStruct)bound).Value).Decoded!;
            Assert.StartsWith($"N={MachoMessage.ProxyNodeId}:", JsSemantics.StringValue(oid.Items[0]));
            Assert.Equal(new PyLong(Py.FileTimeFromUnixMs(Rig.ClockMs)), oid.Items[1]);
        }
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "GetInventory", null, new PyInt(10002)));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(Rig.Session(station: null), "GetInventory", null, new PyInt(10004)));
    }

    [PgFact]
    public async Task Each_bound_inventory_has_its_own_name()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Rig.Session();
        Assert.NotEqual(await rig.HangarAsync(session), await rig.HangarAsync(session));
    }

    [PgFact]
    public async Task The_hangar_lists_the_characters_items_at_the_station_as_rows()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var tritanium = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 1000);
        await rig.AddAsync(Rig.Ibis, 140_000_002, Rig.Station, singleton: true);      // another character's
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.OtherStation, singleton: true); // in another station
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);

        var rows = Rows(await rig.CallAsync(session, "List", hangar));
        Assert.Equal([ship, tritanium], rows.Select(r => ((PyLong)r.Values[0]).Value));
        Assert.Equal(
            new PyValue[] { new PyLong(ship), new PyInt(601), new PyInt(Rig.Character), new PyLong(Rig.Station), new PyInt(4), new PyInt(-1), new PyInt(237), new PyInt(6), new PyStr("") },
            Values(rows[0]));
        Assert.Equal(new PyInt(1000), rows[1].Values[5]);
        Assert.Equal((new PyInt(18), new PyInt(4)), (rows[1].Values[6], rows[1].Values[7])); // minerals: group 18 of category 4
    }

    [PgFact]
    public async Task A_flag_with_items_lists_a_flag_without_gives_an_empty_set_and_no_flag_means_all()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);

        Assert.Single(Rows(await rig.CallAsync(session, "List", hangar)));
        Assert.Single(Rows(await rig.CallAsync(session, "List", hangar, PyNone.Instance)));
        Assert.Single(Rows(await rig.CallAsync(session, "List", hangar, new PyInt(0))));
        Assert.Single(Rows(await rig.CallAsync(session, "List", hangar, new PyInt(4))));
        Assert.Equal(InventoryWire.EmptySet(), await rig.CallAsync(session, "List", hangar, new PyInt(5)));
        Assert.Equal(InventoryWire.EmptySet(), await rig.CallAsync(session, "List", hangar, new PyInt(27)));

        // the flag can be a keyword
        var named = new ServiceCall { Method = "List", Args = [], Kwargs = new PyDict([new(new PyStr("flag"), new PyInt(5))]), Session = session, BoundObjectId = hangar };
        Assert.Equal(InventoryWire.EmptySet(), await ((IService)rig.Service).CallAsync(named));
    }

    [PgFact]
    public async Task An_empty_hangar_lists_an_empty_list()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "List", hangar));
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "GetItems", hangar));
    }

    [PgFact]
    public async Task Listing_by_flags_answers_a_list_even_when_nothing_is_there()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        Assert.Single(Rows(await rig.CallAsync(session, "ListByFlags", hangar, new PyTuple([new PyInt(4), new PyInt(5)]))));
        Assert.Single(Rows(await rig.CallAsync(session, "ListByFlags", hangar, new PyList([new PyInt(4)]))));
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "ListByFlags", hangar, new PyTuple([new PyInt(5)])));
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "ListByFlags", hangar, new PyTuple([])));
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "ListByFlags", hangar));
    }

    [PgFact]
    public async Task Items_can_be_fetched_by_id_from_the_inventory_they_are_in()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var other = await rig.AddAsync(Rig.Ibis, 140_000_002, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        var rows = Rows(await rig.CallAsync(session, "GetItems", hangar, new PyList([new PyLong(ship), new PyLong(other), new PyInt(1)])));
        Assert.Equal([ship], rows.Select(r => ((PyLong)r.Values[0]).Value));
    }

    [PgFact]
    public async Task The_station_is_the_item_the_hangar_is_in_as_a_util_row_and_as_a_packed_row()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);

        var row = (PyObject)await rig.CallAsync(session, "GetItem", hangar);
        Assert.Equal("util.Row", JsSemantics.StringValue(row.Name));
        var line = (PyTuple)((PyDict)row.Args).Entries[1].Value;
        // the station: its type, its owner, itself as the place, no flag, a stack of one, group 15 of category 3
        Assert.Equal(
            new PyTuple([new PyInt(Rig.Station), new PyInt(92885), new PyInt(1000413), new PyInt(Rig.Station), new PyInt(0), new PyInt(1), new PyInt(15), new PyInt(3), new PyStr(""), new PyInt(1), new PyInt(1)]),
            line);

        var packed = (PyPackedRow)await rig.CallAsync(session, "GetSelfInvItem", hangar);
        var fields = packed.Fields().ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal((new PyInt(1), new PyInt(0)), (fields["stacksize"], fields["singleton"])); // a quantity of one is not a singleton here
    }

    [PgFact]
    public async Task The_contents_come_as_a_rowset_of_the_same_rows()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        var lines = (PyList)((PyDict)((PyObject)await rig.CallAsync(session, "GetContainerContents", hangar)).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == "lines").Value;
        Assert.Equal(
            new PyTuple([new PyInt(ship), new PyInt(601), new PyInt(Rig.Character), new PyInt(Rig.Station), new PyInt(4), new PyInt(-1), new PyInt(237), new PyInt(6), new PyStr(""), new PyInt(1), new PyInt(1)]),
            Assert.Single(lines.Items));
    }

    [PgFact]
    public async Task The_station_and_a_ship_of_the_character_can_be_opened_by_their_ids()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var cargo = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 5);
        var foreign = await rig.AddAsync(Rig.Ibis, 140_000_002, Rig.Station, singleton: true);
        var away = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.OtherStation, singleton: true);
        var session = Rig.Session();

        var station = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyInt(Rig.Station)));
        Assert.Equal(2, Rows(await rig.CallAsync(session, "List", station)).Length); // the same rows as the hangar

        var shipInventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));
        Assert.NotEqual(station, shipInventory);
        foreach (var id in new long[] { foreign, away, cargo, 1, 999_999_999 })
        {
            Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(id)));
        }
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "GetInventoryFromId", null, new PyStr("x")));
    }

    [PgFact]
    public async Task An_empty_ship_lists_nothing_and_is_its_own_item()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        Assert.Equal(new PyList([]), await rig.CallAsync(session, "List", inventory));
        Assert.Equal(InventoryWire.EmptySet(), await rig.CallAsync(session, "List", inventory, new PyInt(5))); // a flag with nothing in it, as in the hangar
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "ListByFlags", inventory, new PyTuple([new PyInt(5), new PyInt(27)])));
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "GetItems", inventory));
        var lines = ((PyDict)((PyObject)await rig.CallAsync(session, "GetContainerContents", inventory)).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == "lines").Value;
        Assert.Equal(new PyList([]), lines);

        var self = (PyTuple)((PyDict)((PyObject)await rig.CallAsync(session, "GetItem", inventory)).Args).Entries[1].Value;
        Assert.Equal((new PyInt(ship), new PyInt(601), new PyInt(Rig.Character), new PyInt(Rig.Station), new PyInt(4), new PyInt(-1)), (self.Items[0], self.Items[1], self.Items[2], self.Items[3], self.Items[4], self.Items[5]));
        Assert.Equal(new PyInt(ship), (await rig.CallAsync(session, "GetSelfInvItem", inventory) is PyPackedRow row ? row.Values[0] : null) is PyLong l ? new PyInt(l.Value) : null);
    }

    [PgFact]
    public async Task A_ship_lists_what_is_fitted_to_it_and_what_it_carries_but_not_the_hangar_or_another_ships()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var other = await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var afterburner = await rig.AddAsync(21857, Rig.Character, ship, flag: 19, singleton: true);
        var weapon = await rig.AddAsync(3638, Rig.Character, ship, flag: 27, singleton: true);
        var cargo = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 40);
        await rig.AddAsync(21857, Rig.Character, other, flag: 19, singleton: true); // fitted to the other ship
        await rig.AddAsync(21857, 140_000_002, ship, flag: 20, singleton: true);     // someone else's item in this ship
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        long IdOf(PyPackedRow row) => row.Values[0] is PyLong l ? l.Value : ((PyInt)row.Values[0]).Value;
        Assert.Equal(new[] { afterburner, weapon, cargo }, Rows(await rig.CallAsync(session, "List", inventory)).Select(IdOf).Order());
        Assert.Equal(new[] { weapon }, Rows(await rig.CallAsync(session, "List", inventory, new PyInt(27))).Select(IdOf));
        Assert.Equal(new[] { afterburner, weapon }, Rows(await rig.CallAsync(session, "ListByFlags", inventory, new PyTuple([new PyInt(19), new PyInt(27)]))).Select(IdOf).Order());
        Assert.Equal(new[] { cargo }, Rows(await rig.CallAsync(session, "GetItems", inventory, new PyTuple([new PyLong(cargo), new PyLong(other)]))).Select(IdOf));
        var lines = (PyList)((PyDict)((PyObject)await rig.CallAsync(session, "GetContainerContents", inventory)).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == "lines").Value;
        Assert.Equal(3, lines.Items.Count);
        // the hangar still lists the two ships and nothing fitted or carried
        var hangar = await rig.HangarAsync(session);
        Assert.Equal(new[] { ship, other }.Order(), Rows(await rig.CallAsync(session, "List", hangar)).Select(IdOf).Order());
    }

    [PgFact]
    public async Task A_bound_inventory_answers_only_the_session_that_opened_it_and_only_until_it_is_released()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var mine = Rig.Session(id: 1);
        var theirs = Rig.Session(id: 2);
        var hangar = await rig.HangarAsync(mine);

        Assert.Equal(PyNone.Instance, await rig.CallAsync(theirs, "List", hangar));
        Assert.NotEqual(PyNone.Instance, await rig.CallAsync(mine, "List", hangar));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "List", "N=65450:99999")); // never handed out
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "List")); // not bound at all

        rig.Service.OnBoundObjectReleased(hangar, mine);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(mine, "List", hangar));
    }

    [PgFact]
    public async Task A_bound_inventory_is_forgotten_when_the_connection_that_opened_it_ends()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        Assert.NotEqual(PyNone.Instance, await rig.CallAsync(session, "List", hangar));
        session.Close();
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "List", hangar));
    }

    [PgFact]
    public async Task Binding_by_the_inventorys_own_name_opens_it_and_makes_the_nested_call()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        Assert.Equal(new PyInt(MachoMessage.ProxyNodeId), await rig.CallAsync(session, "MachoResolveObject", null, new PyInt(Rig.Station), new PyInt(0)));

        foreach (var name in new PyValue[] { new PyInt(Rig.Station), new PyTuple([new PyInt(Rig.Station), new PyInt(4)]), new PyInt(InvBrokerService.ContainerHangar) })
        {
            var answer = (PyTuple)await rig.CallAsync(session, "MachoBindObject", null, name, new PyTuple([new PyStr("List"), new PyTuple([new PyInt(4)]), PyNone.Instance]));
            Assert.IsType<PySubStruct>(answer.Items[0]);
            Assert.Single(Rows(answer.Items[1])); // the nested call ran against the inventory that was just opened
        }
        var unknown = (PyValue)await rig.CallAsync(session, "MachoBindObject", null, new PyInt(1), new PyTuple([new PyStr("List"), new PyTuple([]), PyNone.Instance]));
        Assert.Equal(PyNone.Instance, unknown);
    }

    [PgFact]
    public async Task Undocked_or_without_a_character_nothing_is_in_the_hangar()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Ibis, Rig.Character, Rig.Station, singleton: true);
        var docked = Rig.Session();
        var hangar = await rig.HangarAsync(docked);
        docked.Set("stationid", PyNone.Instance);
        Assert.Equal(new PyList([]), await rig.CallAsync(docked, "List", hangar));
    }

    [Fact]
    public void The_descriptor_call_needs_no_database()
    {
        var service = new InvBrokerService(new PgItemStore(NpgsqlDataSource.Create("Host=localhost"), "inventory"), new ItemTypeCatalog(new SdeReader(NpgsqlDataSource.Create("Host=localhost"))), Rig.Stations, () => 1);
        Assert.Equal(InventoryWire.DescriptorHeader, service.GetItemDescriptor(new ServiceCall { Method = "GetItemDescriptor", Args = [], Session = new ClientSession() }));
    }
}
