using Eve.Characters;
using Eve.Data;
using Eve.Dogma;
using Eve.Fitting;
using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Sde;
using Eve.Ships;
using Eve.Skills;
using Eve.Universe;
using Eve.Wallet;
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

    public const int Cockroach = 11019;
    public const int Tritanium = 34;

    public static readonly StationCatalog Stations = new([
        new Universe.Station(Station, 92885, "Manifest V - AIR Laboratories Trade Center", 1000413, "AIR Laboratories", 500028, "AIR", 123, 40491241,
            30100032, "Manifest", 20010001, "Manifest District", 10001004, "Exordium", 1.0, 0.05, 4),
        new Universe.Station(OtherStation, 4023, "Paara I - Deep Core Mining Inc. Mining Outpost", 1000006, "Deep Core Mining Inc.", 500001, "Caldari State", 7, 40003891,
            30002775, "Paara", 20000407, "Peyiri", 10000033, "The Citadel", 0.78, 0.05, 4),
    ]);

    private readonly string _schema = $"inv_{Guid.NewGuid():N}"[..20];

    private Rig(NpgsqlDataSource source, DogmaStatic statics, CorporationHangarAccess? corporationHangarAccess)
    {
        Source = source;
        Statics = statics;
        Characters = new PgCharacterStore(source, _schema + "c");
        Items = new PgItemStore(source, _schema);
        Skills = new PgSkillStore(source, _schema + "s");
        Modules = new PgModuleStateStore(source, _schema + "f");
        Ships = new CharacterShips(source, statics, Characters, Items, Skills, Modules);
        Service = new InvBrokerService(Items, new ItemTypeCatalog(new SdeReader(source)), Stations, Ships, statics,
            () => ClockMs, corporationHangarAccess: corporationHangarAccess);
    }

    /// <summary>Ships that these reads never ask anything of.</summary>
    public static CharacterShips Unused(NpgsqlDataSource source, PgItemStore items, DogmaStatic statics) =>
        new(source, statics, new PgCharacterStore(source, "unused"), items, new PgSkillStore(source, "unused"), new PgModuleStateStore(source, "unused"));

    public NpgsqlDataSource Source { get; }

    public DogmaStatic Statics { get; }

    public PgCharacterStore Characters { get; }

    public PgItemStore Items { get; }

    public PgSkillStore Skills { get; }

    public PgModuleStateStore Modules { get; }

    public CharacterShips Ships { get; }

    public InvBrokerService Service { get; }

    public static async Task<Rig> CreateAsync(bool activeShip = false, CorporationHangarAccess? corporationHangarAccess = null)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Url!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");
        var rig = new Rig(source, await DogmaStatic.LoadAsync(sde), corporationHangarAccess);
        await PgItemStore.MigrateAsync(source, rig._schema);
        await PgCharacterStore.MigrateAsync(source, rig._schema + "c");
        await PgSkillStore.MigrateAsync(source, rig._schema + "s");
        await PgModuleStateStore.MigrateAsync(source, rig._schema + "f");
        await rig.SeedCharacterAsync(activeShip);
        return rig;
    }


    public const long Corporation = 1_000_001;

    private async Task SeedCharacterAsync(bool activeShip)
    {
        var station = Stations.Get(Station)!;
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var characterId = await Characters.InsertAsync(connection, transaction,
            new Eve.Characters.NewCharacter(1, "Inventory Service Test", 1, 1, 1, 1, 1, 1, Corporation, null,
                Station, station.SolarSystemId, station.ConstellationId, station.RegionId, null, null));
        if (activeShip)
        {
            var shipId = await Items.CreateAsync(connection, transaction,
                new NewItem(Cockroach, characterId, Station, ItemFlags.Hangar, 1, true));
            await Characters.SetShipAsync(connection, transaction, characterId, shipId);
        }
        Assert.Equal(Character, characterId);
        await transaction.CommitAsync();
    }

    public async Task<long> AddAsync(int type, long owner, long location, int flag = ItemFlags.Hangar, long quantity = 1, bool singleton = false)
    {
        await using var connection = await Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, quantity, singleton));
        await transaction.CommitAsync();
        return id;
    }

    public static ClientSession Session(long? station = Station, long? character = Character, long id = 1, long? corporation = null)
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
        if (corporation is { } corp)
        {
            session.Set("corporationID", new PyLong(corp));
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
        await using var command = Source.CreateCommand(
            $"DROP SCHEMA IF EXISTS {_schema} CASCADE; DROP SCHEMA IF EXISTS {_schema}c CASCADE; DROP SCHEMA IF EXISTS {_schema}s CASCADE; DROP SCHEMA IF EXISTS {_schema}f CASCADE");
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
        var ship = new ItemRow(1_990_000_001, Rig.Cockroach, 140_000_001, 60015249, 4, -1, 25, 6);
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
        var rowset = (PyObject)InventoryWire.Rowset([new ItemRow(5, Rig.Cockroach, 7, 9, 4, -1, 25, 6)]);
        var parts = ((PyDict)rowset.Args).Entries.ToDictionary(e => JsSemantics.StringValue(e.Key)!, e => e.Value);
        Assert.Equal(["header", "RowClass", "lines"], ((PyDict)rowset.Args).Entries.Select(e => JsSemantics.StringValue(e.Key)!));
        Assert.Equal(new PyToken("util.Row"), parts["RowClass"]);
        Assert.Equal(
            new PyList([new PyTuple([new PyInt(5), new PyInt(Rig.Cockroach), new PyInt(7), new PyInt(9), new PyInt(4), new PyInt(-1), new PyInt(25), new PyInt(6), new PyStr(""), new PyInt(1), new PyInt(1)])]),
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
    // Packed rows preserve DBTYPE20 item IDs as PyLong; compare the numeric ID, not the wrapper type.
    private static long PackedItemId(PyPackedRow row) => row.Values[0] switch
    {
        PyLong wide => wide.Value,
        PyInt narrow => narrow.Value,
        _ => throw new InvalidOperationException("Packed inventory row has a non-integer item ID."),
    };

    /// <summary>The rows in a Python <c>set</c> (what a docked ship answers when it is asked for everything).</summary>
    private static PyPackedRow[] SetRows(PyValue set) =>
        [.. ((PyList)((PyTuple)((PyTuple)((PyObjectEx)set).Header).Items[1]).Items[0]).Items.Cast<PyPackedRow>()];

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
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var tritanium = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 1000);
        await rig.AddAsync(Rig.Cockroach, 140_000_002, Rig.Station, singleton: true);      // another character's
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.OtherStation, singleton: true); // in another station
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);

        var rows = Rows(await rig.CallAsync(session, "List", hangar));
        Assert.Equal([ship, tritanium], rows.Select(r => ((PyLong)r.Values[0]).Value));
        Assert.Equal(
            new PyValue[] { new PyLong(ship), new PyInt(Rig.Cockroach), new PyInt(Rig.Character), new PyLong(Rig.Station), new PyInt(4), new PyInt(-1), new PyInt(25), new PyInt(6), new PyStr("") },
            Values(rows[0]));
        Assert.Equal(new PyInt(1000), rows[1].Values[5]);
        Assert.Equal((new PyInt(18), new PyInt(4)), (rows[1].Values[6], rows[1].Values[7])); // minerals: group 18 of category 4
    }

    [PgFact]
    public async Task A_flag_with_items_lists_a_flag_without_gives_an_empty_set_and_no_flag_means_all()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
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
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
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
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var other = await rig.AddAsync(Rig.Cockroach, 140_000_002, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        var rows = Rows(await rig.CallAsync(session, "GetItems", hangar, new PyList([new PyLong(ship), new PyLong(other), new PyInt(1)])));
        Assert.Equal([ship], rows.Select(r => ((PyLong)r.Values[0]).Value));
    }


    [PgFact]
    public async Task Station_inventory_rpcs_list_owned_items_and_set_a_ship_label()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var cargo = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 10);
        var session = Rig.Session();

        var stationIds = Assert.IsType<PyList>(await rig.CallAsync(session, "ListStations"));
        Assert.Contains(InventoryWire.Id(Rig.Station), stationIds.Items);
        var stationItems = Rows(await rig.CallAsync(session, "ListStationItems", null, new PyLong(Rig.Station)));
        Assert.Contains(stationItems, row => PackedItemId(row) == ship);
        Assert.Equal(PyBool.True, await rig.CallAsync(session, "IsMyItemAtDockableLocation", null, new PyLong(ship), new PyLong(Rig.Station)));

        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));
        var nested = Rows(await rig.CallAsync(session, "ListIncludingContainers", inventory));
        Assert.Contains(nested, row => PackedItemId(row) == cargo);

        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", null, new PyLong(ship), new PyStr("Test fit")));
        Assert.Equal("Test fit", (await rig.Items.GetAsync(ship))!.CustomName);
    }

    [PgFact]
    public async Task MultiAdd_assembles_a_packaged_ship_and_puts_items_in_its_specialized_bay()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = await rig.AddAsync(28606, Rig.Character, Rig.Station, quantity: 1, singleton: false);
        var heavyWater = await rig.AddAsync(16272, Rig.Character, Rig.Station, quantity: 10, singleton: false);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var result = await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "MultiAdd",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyList([new PyLong(heavyWater)]), new PyLong(Rig.Station)],
            Kwargs = new PyDict([new(new PyStr("flag"), new PyInt(133))]),
        });

        Assert.Equal(PyBool.True, result);
        Assert.True((await rig.Items.GetAsync(ship))!.Singleton);
        Assert.Equal(ship, (await rig.Items.GetAsync(heavyWater))!.LocationId);
        Assert.Equal(133, (await rig.Items.GetAsync(heavyWater))!.Flag);
    }

    [PgFact]
    public async Task Repackaging_service_accepts_station_grouped_client_requests()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var itemId = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, singleton: true);
        var service = new RepackagingService(rig.Service);
        var dispatcher = (IService)service;
        Assert.Equal("repackagingSvc", dispatcher.Name);
        Assert.True(dispatcher.HasMethod("RepackageItems"));

        var request = new PyDict([
            new(new PyLong(Rig.Station), new PyList([
                PyTuple.Of(new PyLong(itemId), new PyLong(Rig.Station)),
            ])),
        ]);
        var result = await dispatcher.CallAsync(new ServiceCall
        {
            Method = "RepackageItems",
            Session = Rig.Session(),
            Args = [request],
        });

        Assert.Equal(PyNone.Instance, result);
        Assert.False((await rig.Items.GetAsync(itemId))!.Singleton);
    }


    [PgFact]
    public async Task Corporation_ship_inventory_reads_and_docked_add_and_multiadd_enforce_division_access()
    {
        var allowTake = false;
        await using var rig = await Rig.CreateAsync(activeShip: true,
            corporationHangarAccess: (character, corporation, station, division, operation, _) =>
                ValueTask.FromResult(character == Rig.Character && corporation == Rig.Corporation
                    && station == Rig.Station && division == ItemFlags.CorpSAG1
                    && (operation is CorporationHangarOperation.Query or CorporationHangarOperation.Deposit
                        || operation == CorporationHangarOperation.Take && allowTake)));
        var corporationShip = await rig.AddAsync(Rig.Cockroach, Rig.Corporation, Rig.Station,
            ItemFlags.CorpSAG1, singleton: true);
        var corporationCargo = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, corporationShip,
            flag: CharacterShips.CargoFlag, quantity: 2);
        var session = Rig.Session(corporation: Rig.Corporation);
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(corporationShip)));
        var existing = Rows(await rig.CallAsync(session, "List", inventory, new PyInt(CharacterShips.CargoFlag)));
        Assert.Contains(existing, row => PackedItemId(row) == corporationCargo);

        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", inventory,
            new PyLong(corporationShip), new PyStr("Denied")));
        Assert.Null((await rig.Items.GetAsync(corporationShip))!.CustomName);
        allowTake = true;
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", inventory,
            new PyLong(corporationShip), new PyStr("Corp fit")));
        Assert.Equal("Corp fit", (await rig.Items.GetAsync(corporationShip))!.CustomName);

        var single = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 3);
        var add = await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "Add",
            Args = [new PyLong(single), new PyLong(Rig.Station)],
            Kwargs = new PyDict([new(new PyStr("flag"), new PyInt(CharacterShips.CargoFlag))]),
            Session = session,
            BoundObjectId = inventory,
        });
        Assert.Equal(InventoryWire.Id(single), add);
        Assert.Equal(Rig.Corporation, (await rig.Items.GetAsync(single))!.OwnerId);
        Assert.Equal(corporationShip, (await rig.Items.GetAsync(single))!.LocationId);

        var multiple = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 4);
        var multiAdd = await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "MultiAdd",
            Args = [new PyList([new PyLong(multiple)]), new PyLong(Rig.Station)],
            Kwargs = new PyDict([new(new PyStr("flag"), new PyInt(CharacterShips.CargoFlag))]),
            Session = session,
            BoundObjectId = inventory,
        });
        Assert.Equal(PyBool.True, multiAdd);
        Assert.Equal(Rig.Corporation, (await rig.Items.GetAsync(multiple))!.OwnerId);
        Assert.Equal(corporationShip, (await rig.Items.GetAsync(multiple))!.LocationId);

        var mergeSource = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, corporationShip,
            flag: CharacterShips.CargoFlag, quantity: 5);
        var mergeTarget = await rig.AddAsync(Rig.Tritanium, Rig.Corporation, corporationShip,
            flag: CharacterShips.CargoFlag, quantity: 10);
        var operation = Py.Tuple(new PyLong(mergeSource), new PyLong(mergeTarget), new PyInt(3));
        Assert.Equal(PyBool.True, await rig.CallAsync(session, "MultiMerge", inventory,
            new PyList([operation]), new PyLong(corporationShip)));
        Assert.Equal(2, (await rig.Items.GetAsync(mergeSource))!.Quantity);
        Assert.Equal(13, (await rig.Items.GetAsync(mergeTarget))!.Quantity);
    }

    [PgFact]
    public async Task Station_asset_rpcs_list_owned_items_and_set_a_ship_label()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var cargo = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 10);
        var session = Rig.Session();

        var stations = Assert.IsType<PyList>(await rig.CallAsync(session, "ListStations"));
        Assert.Contains(InventoryWire.Id(Rig.Station), stations.Items);
        var stationItems = Rows(await rig.CallAsync(session, "ListStationItems", null, new PyLong(Rig.Station)));
        Assert.Contains(stationItems, row => PackedItemId(row) == ship);
        Assert.Equal(PyBool.True, await rig.CallAsync(session, "IsMyItemAtDockableLocation", null, new PyLong(ship), new PyLong(Rig.Station)));

        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));
        var includingContainers = Rows(await rig.CallAsync(session, "ListIncludingContainers", inventory));
        Assert.Contains(includingContainers, row => PackedItemId(row) == cargo);

        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", null, new PyLong(ship), new PyStr("Test fit")));
        Assert.Equal("Test fit", (await rig.Items.GetAsync(ship))!.CustomName);
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
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var hangar = await rig.HangarAsync(session);
        var lines = (PyList)((PyDict)((PyObject)await rig.CallAsync(session, "GetContainerContents", hangar)).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == "lines").Value;
        Assert.Equal(
            new PyTuple([new PyInt(ship), new PyInt(Rig.Cockroach), new PyInt(Rig.Character), new PyInt(Rig.Station), new PyInt(4), new PyInt(-1), new PyInt(25), new PyInt(6), new PyStr(""), new PyInt(1), new PyInt(1)]),
            Assert.Single(lines.Items));
    }

    [PgFact]
    public async Task The_station_and_a_ship_of_the_character_can_be_opened_by_their_ids()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var cargo = await rig.AddAsync(Rig.Tritanium, Rig.Character, Rig.Station, quantity: 5);
        var foreign = await rig.AddAsync(Rig.Cockroach, 140_000_002, Rig.Station, singleton: true);
        var away = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.OtherStation, singleton: true);
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
    public async Task Owned_docked_containers_can_be_opened_listed_and_traversed_when_nested()
    {
        await using var rig = await Rig.CreateAsync();
        var capacity = rig.Statics.AttributeId("capacity");
        Assert.NotNull(capacity);
        var containerType = (await rig.Statics.TypesInGroupsAsync([
            "Cargo Container", "Secure Cargo Container", "Freight Container", "Audit Log Secure Container"
        ])).First(type => type.Attributes.GetValueOrDefault(capacity.Value) > 0);
        var parent = await rig.AddAsync(containerType.TypeId, Rig.Character, Rig.Station, singleton: true);
        var nested = await rig.AddAsync(containerType.TypeId, Rig.Character, parent, flag: 0, singleton: true);
        await rig.AddAsync(Rig.Tritanium, Rig.Character, nested, flag: 0, quantity: 4);
        var session = Rig.Session();

        var parentInventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(parent)));
        var nestedInventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(nested)));
        Assert.NotEqual(parentInventory, nestedInventory);
        Assert.Single(Rows(await rig.CallAsync(session, "List", nestedInventory)));
        Assert.Equal(2, Rows(await rig.CallAsync(session, "ListIncludingContainers", parentInventory)).Length);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "SetLabel", null, new PyLong(parent), new PyStr("Tools")));
        Assert.Equal("Tools", (await rig.Items.GetAsync(parent))!.CustomName);

        var foreign = await rig.AddAsync(containerType.TypeId, 140_000_002, Rig.Station, singleton: true);
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(foreign)));
    }

    [PgFact]
    public async Task An_empty_ship_lists_nothing_and_is_its_own_item()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        Assert.Equal(InventoryWire.EmptySet(), await rig.CallAsync(session, "List", inventory)); // a docked ship asked for everything answers a set, empty or not
        Assert.Equal(InventoryWire.EmptySet(), await rig.CallAsync(session, "List", inventory, new PyInt(5))); // a flag with nothing in it, as in the hangar
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "ListByFlags", inventory, new PyTuple([new PyInt(5), new PyInt(27)])));
        Assert.Equal(new PyList([]), await rig.CallAsync(session, "GetItems", inventory));
        var lines = ((PyDict)((PyObject)await rig.CallAsync(session, "GetContainerContents", inventory)).Args).Entries.Single(e => JsSemantics.StringValue(e.Key) == "lines").Value;
        Assert.Equal(new PyList([]), lines);

        var self = (PyTuple)((PyDict)((PyObject)await rig.CallAsync(session, "GetItem", inventory)).Args).Entries[1].Value;
        Assert.Equal((new PyInt(ship), new PyInt(Rig.Cockroach), new PyInt(Rig.Character), new PyInt(Rig.Station), new PyInt(4), new PyInt(-1)), (self.Items[0], self.Items[1], self.Items[2], self.Items[3], self.Items[4], self.Items[5]));
        Assert.Equal(new PyInt(ship), (await rig.CallAsync(session, "GetSelfInvItem", inventory) is PyPackedRow row ? row.Values[0] : null) is PyLong l ? new PyInt(l.Value) : null);
    }

    [PgFact]
    public async Task A_ship_lists_what_is_fitted_to_it_and_what_it_carries_but_not_the_hangar_or_another_ships()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var other = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var afterburner = await rig.AddAsync(21857, Rig.Character, ship, flag: 19, singleton: true);
        var weapon = await rig.AddAsync(3638, Rig.Character, ship, flag: 27, singleton: true);
        var cargo = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 40);
        await rig.AddAsync(21857, Rig.Character, other, flag: 19, singleton: true); // fitted to the other ship
        await rig.AddAsync(21857, 140_000_002, ship, flag: 20, singleton: true);     // someone else's item in this ship
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        long IdOf(PyPackedRow row) => row.Values[0] is PyLong l ? l.Value : ((PyInt)row.Values[0]).Value;
        var everything = SetRows(await rig.CallAsync(session, "List", inventory));
        Assert.Equal(new[] { afterburner, weapon, cargo }, everything.Select(IdOf).Order());
        // the rows have no custom info, as in the reference
        Assert.All(everything, row => Assert.IsType<PyNone>(row.Values[8]));
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
    public async Task StackAll_joins_what_can_be_joined_and_answers_nothing_when_there_is_nothing()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var first = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 100);
        var second = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 15);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        Assert.Equal(PyBool.True, await rig.CallAsync(session, "StackAll", inventory, new PyInt(5)));
        Assert.Equal(PyNone.Instance, await rig.CallAsync(session, "StackAll", inventory, new PyInt(5)));
        var rows = Rows(await rig.CallAsync(session, "List", inventory, new PyInt(5)));
        var only = Assert.Single(rows);
        Assert.Equal(new PyInt(115), only.Values[5]);
        Assert.Equal(first, only.Values[0] is PyLong l ? l.Value : ((PyInt)only.Values[0]).Value);
        Assert.NotEqual(first, second);
    }


    [PgFact]
    public async Task MultiMerge_moves_the_requested_quantity_between_owned_stacks()
    {
        await using var rig = await Rig.CreateAsync();
        var ship = await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var source = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 15);
        var destination = await rig.AddAsync(Rig.Tritanium, Rig.Character, ship, flag: 5, quantity: 20);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));
        var operation = Py.Tuple(new PyLong(source), new PyLong(destination), new PyInt(5));

        Assert.Equal(PyBool.True, await rig.CallAsync(session, "MultiMerge", inventory, new PyList([operation]), new PyLong(ship)));
        Assert.Equal(10, (await rig.Items.GetAsync(source))!.Quantity);
        Assert.Equal(25, (await rig.Items.GetAsync(destination))!.Quantity);
    }


    [PgFact]
    public async Task A_bound_inventory_answers_only_the_session_that_opened_it_and_only_until_it_is_released()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
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
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
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
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
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
        await rig.AddAsync(Rig.Cockroach, Rig.Character, Rig.Station, singleton: true);
        var docked = Rig.Session();
        var hangar = await rig.HangarAsync(docked);
        docked.Set("stationid", PyNone.Instance);
        Assert.Equal(new PyList([]), await rig.CallAsync(docked, "List", hangar));
    }

    /// <summary>The space a pilot flies in as a test plays it: what is asked of a "Loot All" is kept and what it is answered with is the test's.</summary>
[PgFact]
    public async Task A_docked_ship_inventory_moves_drones_into_and_out_of_the_drone_bay()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schemas = new[] { $"dc_{suffix}", $"ds_{suffix}", $"dw_{suffix}", $"df_{suffix}" };
        try
        {
            var characters = new PgCharacterStore(rig.Source, schemas[0]);
            var skills = new PgSkillStore(rig.Source, schemas[1]);
            var wallets = new PgWalletStore(rig.Source, schemas[2]);
            var modules = new PgModuleStateStore(rig.Source, schemas[3]);
            await PgCharacterStore.MigrateAsync(rig.Source, schemas[0]);
            await PgSkillStore.MigrateAsync(rig.Source, schemas[1]);
            await PgWalletStore.MigrateAsync(rig.Source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(rig.Source, schemas[3]);

            var sde = new SdeReader(rig.Source);
            Assert.NotNull(await sde.BuildAsync());
            CharacterNames.EnsureUnicodeSupport();
            var statics = await DogmaStatic.LoadAsync(sde);
            await statics.EnsureTypesAsync([Rig.Cockroach, 21857, 3636, 2454]);
            var catalog = await CreationCatalog.LoadAsync(sde, Rig.Station);
            var cockroachCatalog = new CreationCatalog(
                catalog.Races.Select(r => r.Id == 1 ? r with { ShipTypeId = Rig.Cockroach, ShipName = "Cockroach" } : r),
                catalog.Bloodlines,
                CreationCatalog.Schools.Select(s => catalog.StarterFor(s.CorporationId)!));
            var creation = new CharacterCreation(
                rig.Source, characters, rig.Items, skills, await SkillCatalog.LoadAsync(sde), wallets,
                new StartingWallet(), cockroachCatalog, modules);
            var created = Assert.IsType<Created>(await creation.CreateAsync(
                new CreationRequest(1, "Drone Bay Tester", 1, 1, 1, 10, 35, [1], [2])));
            var ships = new CharacterShips(rig.Source, statics, characters, rig.Items, skills, modules);
            var service = new InvBrokerService(rig.Items, new ItemTypeCatalog(sde), Rig.Stations, ships, statics, () => Rig.ClockMs);
            var drones = await rig.AddAsync(2454, created.CharacterId, Rig.Station, quantity: 100_001);
            var tritanium = await rig.AddAsync(Rig.Tritanium, created.CharacterId, Rig.Station);
            var session = Rig.Session(character: created.CharacterId);

            async Task<PyValue> Call(string? bound, string method, PyValue[] args, params (string Name, PyValue Value)[] kwargs)
            {
                var rpc = new ServiceCall
                {
                    Method = method,
                    Args = args,
                    Session = session,
                    BoundObjectId = bound,
                    Kwargs = kwargs.Length == 0 ? PyNone.Instance
                        : new PyDict([.. kwargs.Select(k => new KeyValuePair<PyValue, PyValue>(new PyStr(k.Name), k.Value))]),
                };
                return method switch
                {
                    "GetInventory" => service.GetInventory(rpc),
                    "GetInventoryFromId" => await service.GetInventoryFromId(rpc),
                    "GetCapacity" => await service.GetCapacity(rpc),
                    "Add" => await service.Add(rpc),
                    _ => throw new InvalidOperationException($"Unexpected inventory RPC {method}"),
                };
            }

            var shipInventory = Rig.OidOf(await Call(null, "GetInventoryFromId", [new PyLong(created.ShipId)]));
            var hangar = Rig.OidOf(await Call(null, "GetInventory", [new PyInt(InvBrokerService.ContainerHangar)]));
            var empty = Assert.IsType<PyObject>(await Call(shipInventory, "GetCapacity", [new PyInt(CharacterShips.DroneBayFlag)]));
            var emptyRoom = Assert.IsType<PyDict>(empty.Args);
            Assert.Equal(Py.Number(500_000), emptyRoom.Entries.Single(e => e.Key == new PyStr("capacity")).Value);
            Assert.Equal(Py.Number(0), emptyRoom.Entries.Single(e => e.Key == new PyStr("used")).Value);

            var wrongLocation = await Call(shipInventory, "Add", [new PyLong(drones), new PyLong(Rig.OtherStation)],
                ("flag", new PyInt(CharacterShips.DroneBayFlag)));
            Assert.Equal(PyNone.Instance, wrongLocation);

            await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await Call(shipInventory, "Add", [new PyLong(tritanium), new PyLong(Rig.Station)],
                    ("flag", new PyInt(CharacterShips.DroneBayFlag))));

            var undockedSession = Rig.Session(station: null, character: created.CharacterId, id: session.SessionId);
            var undocked = await service.Add(new ServiceCall
            {
                Method = "Add",
                Args = [new PyLong(drones), new PyLong(Rig.Station)],
                Kwargs = new PyDict([new(new PyStr("flag"), new PyInt(CharacterShips.DroneBayFlag))]),
                Session = undockedSession,
                BoundObjectId = shipInventory,
            });
            Assert.Equal(PyNone.Instance, undocked);

            await Call(shipInventory, "Add", [new PyLong(drones), new PyLong(Rig.Station)],
                ("flag", new PyInt(CharacterShips.DroneBayFlag)), ("qty", new PyInt(100_000)));
            var bayDrone = (await rig.Items.InLocationAsync(created.ShipId, created.CharacterId))
                .Single(item => item.Flag == CharacterShips.DroneBayFlag);
            var full = Assert.IsType<PyObject>(await Call(shipInventory, "GetCapacity", [new PyInt(CharacterShips.DroneBayFlag)]));
            var fullRoom = Assert.IsType<PyDict>(full.Args);
            Assert.Equal(Py.Number(500_000), fullRoom.Entries.Single(e => e.Key == new PyStr("capacity")).Value);
            Assert.Equal(Py.Number(500_000), fullRoom.Entries.Single(e => e.Key == new PyStr("used")).Value);
            await Assert.ThrowsAsync<MachoWrappedException>(async () =>
                await Call(shipInventory, "Add", [new PyLong(drones), new PyLong(Rig.Station)],
                    ("flag", new PyInt(CharacterShips.DroneBayFlag))));

            await Call(hangar, "Add", [new PyLong(bayDrone.Id), new PyLong(created.ShipId)],
                ("flag", new PyInt(ItemFlags.Hangar)));
            var returned = await rig.Items.GetAsync(bayDrone.Id);
            Assert.NotNull(returned);
            Assert.Equal((Rig.Station, ItemFlags.Hangar), (returned.LocationId, returned.Flag));
            var emptyAgain = Assert.IsType<PyObject>(await Call(shipInventory, "GetCapacity", [new PyInt(CharacterShips.DroneBayFlag)]));
            var emptyAgainRoom = Assert.IsType<PyDict>(emptyAgain.Args);
            Assert.Equal(Py.Number(0), emptyAgainRoom.Entries.Single(e => e.Key == new PyStr("used")).Value);
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class FakeSpace : Eve.Services.Ships.ISpaceContainers
    {
        public bool? Answer { get; set; }

        public List<(IReadOnlyList<long> Ids, long Source, long Destination)> Asked { get; } = [];

        public bool IsInSpace(ClientSession session) => true;

        public int? SolarSystemOf(ClientSession session) => 30100032;

        public Task<Item?> OpenAsync(ClientSession session, long containerId, CancellationToken cancellationToken) => Task.FromResult<Item?>(null);

        public Task<long?> MoveAsync(ClientSession session, long itemId, long sourceId, long destinationId, long? quantity, CancellationToken cancellationToken) => Task.FromResult<long?>(null);

        public Task<bool?> MoveAllAsync(ClientSession session, IReadOnlyList<long> itemIds, long sourceId, long destinationId, CancellationToken cancellationToken)
        {
            Asked.Add((itemIds, sourceId, destinationId));
            return Task.FromResult(Answer);
        }

        public Task<IReadOnlyList<long>> JettisonAsync(ClientSession session, IReadOnlyList<long> itemIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>([]);
    }

    [PgFact]
    public async Task Loot_all_asks_the_space_for_the_ids_the_pilot_names_from_the_container_it_names_into_the_ship_it_flies_and_answers_what_the_space_says()
    {
        await using var rig = await Rig.CreateAsync();
        var space = new FakeSpace();
        var service = new InvBrokerService(rig.Items, new ItemTypeCatalog(new SdeReader(rig.Source)), Rig.Stations, Rig.Unused(rig.Source, rig.Items, new DogmaStatic([], [], [])), new DogmaStatic([], [], []), () => 1, space);
        var ship = 9_988_400_000_001;
        var session = Rig.Session(station: null);
        session.Set("shipid", new PyLong(ship));
        session.Set("solarsystemid", new PyInt(30100032));
        Task<PyValue> Call(string oid, string method, params PyValue[] args) => CallWith(oid, method, null, args);
        Task<PyValue> CallWith(string oid, string method, int? flag, params PyValue[] args) =>
            ((IService)service).CallAsync(new ServiceCall
            {
                Method = method, Args = args, Session = session, BoundObjectId = oid,
                Kwargs = flag is { } f ? new PyDict([new(new PyStr("flag"), new PyInt(f))]) : PyNone.Instance,
            }).AsTask();
        var oid = Rig.OidOf(await Call(null!, "GetInventoryFromId", new PyLong(ship)));

        // An omitted flag inherits the cargo flag from the bound ship inventory.
        // what moved: True
        space.Answer = true;
        Assert.Equal(PyBool.True, await Call(oid, "MultiAdd", new PyList([new PyLong(9_988_400_000_005), new PyInt(77)]), new PyLong(9_988_400_000_009)));
        Assert.Equal([9_988_400_000_005L, 77], space.Asked[0].Ids);
        Assert.Equal((9_988_400_000_009L, ship), (space.Asked[0].Source, space.Asked[0].Destination));
        // the cargo hold named by the flag (the loot window's) and ids as a tuple are the same
        Assert.Equal(PyBool.True, await CallWith(oid, "MultiAdd", 5, new PyTuple([new PyInt(5)]), new PyInt(9)));
        Assert.Equal(2, space.Asked.Count);
        // nothing could be moved: the message the client shows
        space.Answer = false;
        var failure = await Assert.ThrowsAsync<MachoWrappedException>(async () => await Call(oid, "MultiAdd", new PyList([new PyInt(5)]), new PyInt(9)));
        var args = ((PyTuple)((PyTuple)((PyObjectEx)failure.Error.Payload).Header).Items[1]).Items;
        Assert.Equal("CustomNotify", JsSemantics.StringValue(args[0]));
        Assert.Equal("None of the selected items could be moved.", JsSemantics.StringValue(((PyDict)args[1]).Entries.Single(e => JsSemantics.StringValue(e.Key) == "notify").Value));
        // not the sort of move this is: nothing, and the space is not asked
        space.Answer = null;
        Assert.Equal(PyNone.Instance, await Call(oid, "MultiAdd", new PyList([new PyInt(5)]), new PyInt(9)));
        var asked = space.Asked.Count;
        Assert.Equal(PyNone.Instance, await Call(oid, "MultiAdd", new PyList([]), new PyInt(9)));
        Assert.Equal(PyNone.Instance, await Call(oid, "MultiAdd", new PyList([new PyInt(5)]), new PyStr("x")));
        Assert.Equal(PyNone.Instance, await CallWith(oid, "MultiAdd", 87, new PyList([new PyInt(5)]), new PyInt(9))); // the drone bay is not the hold
        Assert.Equal(PyNone.Instance, await CallWith(oid, "MultiAdd", 0, new PyList([new PyInt(5)]), new PyInt(9))); // an explicit empty flag does not inherit cargo
        Assert.Equal(asked, space.Asked.Count);
    }

    [Fact]
    public void The_descriptor_call_needs_no_database()
    {
        var source = NpgsqlDataSource.Create("Host=localhost");
        var items = new PgItemStore(source, "inventory");
        var service = new InvBrokerService(items, new ItemTypeCatalog(new SdeReader(source)), Rig.Stations, Rig.Unused(source, items, new DogmaStatic([], [], [])), new DogmaStatic([], [], []), () => 1);
        Assert.Equal(InventoryWire.DescriptorHeader, service.GetItemDescriptor(new ServiceCall { Method = "GetItemDescriptor", Args = [], Session = new ClientSession() }));
    }


    [PgFact]
    public async Task Available_turret_slots_are_a_python_integer_zero_when_the_bound_session_has_no_character()
    {
        await using var rig = await Rig.CreateAsync();
        var session = Rig.Session(character: null);
        var hangar = await rig.HangarAsync(session);

        Assert.Equal(new PyInt(0), await rig.CallAsync(session, "GetAvailableTurretSlots", hangar));
    }


    [PgFact]
    public async Task Available_turret_slots_without_a_bound_object_uses_the_prepared_active_ship()
    {
        await using var rig = await Rig.CreateAsync();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var schemas = new[] { $"tc_{suffix}", $"ts_{suffix}", $"tw_{suffix}", $"tf_{suffix}" };
        try
        {
            var characters = new PgCharacterStore(rig.Source, schemas[0]);
            var skills = new PgSkillStore(rig.Source, schemas[1]);
            var wallets = new PgWalletStore(rig.Source, schemas[2]);
            var modules = new PgModuleStateStore(rig.Source, schemas[3]);
            await PgCharacterStore.MigrateAsync(rig.Source, schemas[0]);
            await PgSkillStore.MigrateAsync(rig.Source, schemas[1]);
            await PgWalletStore.MigrateAsync(rig.Source, schemas[2]);
            await PgModuleStateStore.MigrateAsync(rig.Source, schemas[3]);

            var sde = new SdeReader(rig.Source);
            Assert.NotNull(await sde.BuildAsync());
            CharacterNames.EnsureUnicodeSupport();
            var statics = await DogmaStatic.LoadAsync(sde);
            await statics.EnsureTypesAsync([Rig.Cockroach, 21857, 3636, 2454]);
            var catalog = await CreationCatalog.LoadAsync(sde, Rig.Station);
            var turretCatalog = new CreationCatalog(
                catalog.Races.Select(r => r.Id == 2 ? r with { ShipTypeId = Rig.Cockroach, ShipName = "Cockroach" } : r),
                catalog.Bloodlines,
                CreationCatalog.Schools.Select(s => catalog.StarterFor(s.CorporationId)!));
            var creation = new CharacterCreation(
                rig.Source, characters, rig.Items, skills, await SkillCatalog.LoadAsync(sde), wallets,
                new StartingWallet(), turretCatalog, modules);
            var created = Assert.IsType<Created>(await creation.CreateAsync(
                new CreationRequest(1, "Turret RPC Tester", 2, 3, 1, 10, 33, [1], [2])));
            await using (var connection = await rig.Source.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                foreach (var (typeId, flag) in new[] { (21857, 19), (3636, 27) })
                {
                    var itemId = await rig.Items.CreateAsync(connection, transaction, new NewItem(typeId, created.CharacterId, created.ShipId, flag, 1, true));
                    await modules.SetAsync(connection, transaction, itemId, Eve.Dogma.ItemState.Online);
                }
                await transaction.CommitAsync();
            }
            var ships = new CharacterShips(rig.Source, statics, characters, rig.Items, skills, modules);
            var service = new InvBrokerService(
                rig.Items, new ItemTypeCatalog(sde), Rig.Stations, ships, statics, () => 1);

            var turrets = (await rig.Items.InLocationAsync(created.ShipId, created.CharacterId))
                .Where(item => item.TypeId is 21857 or 3636).ToArray();
            Assert.NotEmpty(turrets);
            foreach (var turret in turrets)
            {
                Assert.IsType<ShipChanged>(await ships.MoveAsync(
                    created.CharacterId, turret.Id, created.ShipId, Rig.Station, ItemFlags.Hangar, null));
            }

            var session = Rig.Session(character: created.CharacterId);
            var snapshot = await ships.LoadAsync(created.CharacterId);
            Assert.NotNull(snapshot);
            var expected = Math.Max(0L, (long)snapshot.World.Get(snapshot.Ship.Id, BuiltInEffects.TurretSlotsLeft));
            Assert.True(expected > 0, "the prepared active ship must expose at least one free turret slot");

            var answer = await ((IService)service).CallAsync(new ServiceCall
            {
                Method = "GetAvailableTurretSlots",
                Args = [],
                Session = session,
                CallId = new PyInt(1),
            });

            Assert.IsType<PyInt>(answer);
            Assert.Equal(expected, ((PyInt)answer).Value);

            var missingShip = Rig.Session(character: long.MaxValue - 1);
            var unprepared = await ((IService)service).CallAsync(new ServiceCall
            {
                Method = "GetAvailableTurretSlots",
                Args = [],
                Session = missingShip,
                CallId = new PyInt(2),
            });
            Assert.Equal(new PyInt(0), unprepared);
        }
        finally
        {
            foreach (var schema in schemas)
            {
                await using var command = rig.Source.CreateCommand($"DROP SCHEMA IF EXISTS {schema} CASCADE");
                await command.ExecuteNonQueryAsync();
            }
        }
    }
}
