using Eve.Dogma;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Skills;

namespace Eve.Services.Inventory.Tests;

/// <summary>
/// ones that cannot go are the reply, and a ship that is not the one the fitting names is refused. Real moves, against the real static data.
/// </summary>
public class FitFittingTests
{
    private const int Ibis = 601, Afterburner = 21857, CivilianMiner = 3651;
    private const int MediumSlot = 19, HighSlot = 27;

    /// <summary>The character trains what the test items ask: Civilian Miner (3386), Hobgoblin I (24241 and 3436), each at level 1 (the SDE's 182 and 277).</summary>
    private static async Task TrainAsync(Rig rig)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await rig.Skills.GrantAsync(connection, transaction, Rig.Character,
            [new CharacterSkill(3386, 1, 0), new CharacterSkill(24241, 1, 0), new CharacterSkill(3436, 1, 0), new CharacterSkill(3425, 1, 0)]);
        await transaction.CommitAsync();
    }

    private static PyDict Map(params (PyValue Key, PyValue Value)[] entries) => new([.. entries.Select(e => new KeyValuePair<PyValue, PyValue>(e.Key, e.Value))]);
    private static PyObjectEx Set(params PyValue[] items) => new(ObjectExKind.Ex1,
        Py.Tuple(new PyToken("__builtin__.set"), Py.Tuple(new PyList(items))), [], []);

    [PgFact]
    public async Task Each_module_goes_into_its_slot_and_the_one_without_a_hangar_item_is_reported()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var afterburner = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station);
        var miner = await rig.AddAsync(CivilianMiner, Rig.Character, Rig.Station);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args =
            [
                new PyLong(ship),
                new PyInt(Ibis),
                Map((new PyInt(Afterburner), Set(new PyLong(afterburner))), (new PyInt(CivilianMiner), Set(new PyLong(miner)))),
                new PyLong(Rig.Station),
                Py.KeyVal(("modulesByFlag", Map(
                    (new PyInt(MediumSlot), new PyInt(Afterburner)),
                    (new PyInt(MediumSlot + 1), new PyInt(Afterburner)),
                    (new PyInt(HighSlot), new PyInt(CivilianMiner))))),
                Map(),
                PyBool.True,
            ],
        }));

        var missing = Assert.IsType<PyTuple>(Assert.Single(reply.Items));
        Assert.Equal([new PyInt(Afterburner), new PyInt(1)], missing.Items);
        var fittedAfterburner = (await rig.Items.GetAsync(afterburner))!;
        Assert.Equal((ship, MediumSlot), (fittedAfterburner.LocationId, fittedAfterburner.Flag));
        var fittedMiner = (await rig.Items.GetAsync(miner))!;
        Assert.Equal((ship, HighSlot), (fittedMiner.LocationId, fittedMiner.Flag));
    }

    [PgFact]
    public async Task Oversized_fit_inputs_are_refused_before_any_item_moves()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var itemIds = Set([.. Enumerable.Range(1, 501).Select(value => (PyValue)new PyLong(value))]);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => FitAsync(
            rig, session, ship, inventory, Map((new PyInt(Afterburner), itemIds)), Map(), Map(), Map(), true));

        Assert.Equal("INVENTORY_BULK_LIMIT_EXCEEDED", error.Error.UserErrorMessage);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(ship))!.LocationId);

        var malformed = await Assert.ThrowsAsync<MachoWrappedException>(() => ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyLong(ship), new PyInt(Ibis), new PyStr("not-a-map"), new PyLong(Rig.Station),
                Py.KeyVal(("modulesByFlag", Map())), Map(), PyBool.True],
        }).AsTask());
        Assert.Equal("INVENTORY_OPERATION_INVALID", malformed.Error.UserErrorMessage);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(ship))!.LocationId);
    }

    [PgFact]
    public async Task A_fitting_for_another_hull_is_refused_by_name()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyLong(ship), new PyInt(Ibis + 1), Map(), new PyLong(Rig.Station), Py.KeyVal(("modulesByFlag", Map())), Map(), PyBool.True],
        }).AsTask());
        Assert.Equal(MachoErrors.UserErrorClass, error.Error.ClassName);
        Assert.Equal("ShipTypeInFittingNotSameAsShip", error.Error.UserErrorMessage);
    }

    [PgFact]
    public async Task A_ship_in_space_is_not_fitted_here_and_says_so()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var docked = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(docked, "GetInventoryFromId", null, new PyLong(ship)));
        var inSpace = Rig.Session(station: null);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = inSpace,
            Args = [new PyLong(ship), new PyInt(Ibis), Map(), new PyLong(Rig.Station), Py.KeyVal(("modulesByFlag", Map())), Map(), PyBool.True],
        }).AsTask());
        var payload = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        Assert.Equal(("CustomNotify", "You must be within 3000 meters of an active Mobile Depot you own, or within 5000 meters of an accessible maintenance-bay ship or array, to refit in space."),
            (error.Error.UserErrorMessage, ((PyStr)((PyDict)payload[1]).Entries[0].Value).Value));
    }

    private const int Antimatter = 230, SmallRig = 26929;

    private static async Task<PyValue> FitAsync(Rig rig, ClientSession session, long ship, string inventory, PyDict itemsByType, PyDict modules, PyDict bays, PyDict cargo, bool fitRigs, PyDict? fighters = null)
    {
        var hull = (await rig.Items.GetAsync(ship))!.TypeId;
        return await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyLong(ship), new PyInt(hull), itemsByType, new PyLong(Rig.Station), Py.KeyVal(
                ("modulesByFlag", modules), ("chargesByType", bays), ("fightersByTypeID", fighters ?? Map())), cargo, fitRigs ? PyBool.True : PyBool.False],
        });
    }

    [PgFact]
    public async Task Charges_are_split_into_the_cargo_hold_and_the_shortfall_is_reported_by_quantity()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var stack = await rig.AddAsync(Antimatter, Rig.Character, Rig.Station, quantity: 8);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Antimatter), new PyList([new PyLong(stack)]))), Map(), Map((new PyInt(Antimatter), new PyInt(5))), Map(), true));

        Assert.Empty(reply.Items);
        var inHold = (await rig.Items.InLocationAsync(ship, Rig.Character)).Where(i => i.TypeId == Antimatter).ToList();
        Assert.Equal(5, inHold.Sum(i => i.Quantity));
        Assert.All(inHold, i => Assert.Equal(CharacterShips.CargoFlag, i.Flag));
        Assert.Equal(3, (await rig.Items.GetAsync(stack))!.Quantity);

        // 8 asked, 5 already in the hold: the 3 the hold lacks are the reply (the reference subtracts the destination's quantity)
        var short_ = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map(), Map(), Map((new PyInt(Antimatter), new PyInt(8))), Map(), true));
        var tuple = Assert.IsType<PyTuple>(Assert.Single(short_.Items));
        Assert.Equal([new PyInt(Antimatter), new PyInt(3)], tuple.Items);
    }

    [PgFact]
    public async Task A_rig_that_is_not_fitted_goes_into_the_cargo_hold()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var placed = await rig.AddAsync(SmallRig, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(SmallRig), new PyList([new PyLong(placed)]))), Map((new PyInt(92), new PyInt(SmallRig))), Map(), Map((new PyInt(SmallRig), new PyInt(1))), false));

        Assert.Empty(reply.Items);
        var item = (await rig.Items.GetAsync(placed))!;
        Assert.Equal((ship, CharacterShips.CargoFlag), (item.LocationId, item.Flag));
    }

    private const int Hobgoblin = 2454;

    [PgFact]
    public async Task Drones_are_split_into_the_drone_bay()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var stack = await rig.AddAsync(Hobgoblin, Rig.Character, Rig.Station, quantity: 3);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyLong(ship), new PyInt(Ibis), Map((new PyInt(Hobgoblin), new PyList([new PyLong(stack)]))), new PyLong(Rig.Station),
                Py.KeyVal(("modulesByFlag", Map()), ("dronesByType", Map((new PyInt(Hobgoblin), new PyInt(1))))), Map(), PyBool.True],
        }));

        Assert.Empty(reply.Items);
        var inBay = (await rig.Items.InLocationAsync(ship, Rig.Character)).Where(i => i.TypeId == Hobgoblin && i.Flag == CharacterShips.DroneBayFlag).ToList();
        // the Ibis drone bay holds 5 m3 (SDE 283) and a Hobgoblin I is 5 m3: one drone fits, two would exceed the bay
        Assert.Equal(1, inBay.Sum(i => i.Quantity));
        Assert.Equal(2, (await rig.Items.GetAsync(stack))!.Quantity);
    }

    private const int Cockroach = 11019, LightFighter = 23055, Afterburner2 = 21857;
    private const int FighterBay = 158, RigFlag = 92;

    [PgFact]
    public async Task The_modules_of_a_fit_are_switched_on_where_the_grid_allows()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var afterburner = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station, singleton: true);
        var miner = await rig.AddAsync(CivilianMiner, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Afterburner), new PyList([new PyLong(afterburner)])), (new PyInt(CivilianMiner), new PyList([new PyLong(miner)]))),
            Map((new PyInt(MediumSlot), new PyInt(Afterburner)), (new PyInt(HighSlot), new PyInt(CivilianMiner))), Map(), Map(), true));

        Assert.Empty(reply.Items);
        var states = await rig.Modules.StatesAsync([afterburner, miner]);
        Assert.True(states[afterburner] >= ItemState.Online, "the afterburner is switched on by the fit");
        Assert.True(states[miner] >= ItemState.Online, "the miner is switched on by the fit");
    }

    [PgFact]
    public async Task A_rig_is_fitted_into_its_slot_when_rigs_are_asked_for()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Cockroach, Rig.Character, Rig.Station, singleton: true);
        var placed = await rig.AddAsync(SmallRig, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(SmallRig), new PyList([new PyLong(placed)]))), Map((new PyInt(RigFlag), new PyInt(SmallRig))), Map(), Map(), true));

        Assert.Empty(reply.Items);
        var item = (await rig.Items.GetAsync(placed))!;
        Assert.Equal((ship, RigFlag), (item.LocationId, item.Flag));
    }

    [PgFact]
    public async Task Fighters_go_into_the_fighter_bay_and_a_module_is_refused_there_and_reported()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Cockroach, Rig.Character, Rig.Station, singleton: true);
        var fighters = await rig.AddAsync(LightFighter, Rig.Character, Rig.Station, quantity: 3);
        var module = await rig.AddAsync(Afterburner2, Rig.Character, Rig.Station, singleton: true);
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(LightFighter), new PyList([new PyLong(fighters)]))), Map(), Map(), Map(), true,
            fighters: Map((new PyInt(LightFighter), new PyInt(2)))));
        Assert.Empty(reply.Items);
        var inBay = (await rig.Items.InLocationAsync(ship, Rig.Character)).Where(i => i.TypeId == LightFighter && i.Flag == FighterBay).ToList();
        Assert.Equal(2, inBay.Sum(i => i.Quantity));
        Assert.Equal(1, (await rig.Items.GetAsync(fighters))!.Quantity);

        var refused = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Afterburner2), new PyList([new PyLong(module)]))), Map(), Map(), Map(), true,
            fighters: Map((new PyInt(Afterburner2), new PyInt(1)))));
        var missing = Assert.IsType<PyTuple>(Assert.Single(refused.Items));
        Assert.Equal([new PyInt(Afterburner2), new PyInt(1)], missing.Items);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(module))!.LocationId);
    }

    [PgFact]
    public async Task A_fit_tells_the_pilot_its_changes_in_one_batch_at_the_end()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var afterburner = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station, singleton: true);
        var miner = await rig.AddAsync(CivilianMiner, Rig.Character, Rig.Station, singleton: true);
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 1, UserId = 34, CharacterId = Rig.Character };
        session.Set("stationid", new PyInt(Rig.Station));
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Afterburner), new PyList([new PyLong(afterburner)])), (new PyInt(CivilianMiner), new PyList([new PyLong(miner)]))),
            Map((new PyInt(MediumSlot), new PyInt(Afterburner)), (new PyInt(HighSlot), new PyInt(CivilianMiner))),
            Map(), Map(), true));

        Assert.Empty(reply.Items);
        Assert.Equal((1, 1), (sink.Named("OnMultiEvent"), sink.Named("OnItemsChanged")));
    }

    [PgFact]
    public async Task A_saved_fit_replaces_an_occupied_module_slot_and_keeps_the_old_module_in_the_hangar()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        const int shieldExtender = 3829;
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var oldModule = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station);
        var replacement = await rig.AddAsync(shieldExtender, Rig.Character, Rig.Station);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(
            Rig.Character, oldModule, Rig.Station, ship, MediumSlot, null));
        var session = Rig.Session();
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args =
            [
                new PyLong(ship),
                new PyInt(Ibis),
                Map((new PyInt(shieldExtender), new PyList([new PyLong(replacement)]))),
                new PyLong(Rig.Station),
                Py.KeyVal(("modulesByFlag", Map((new PyInt(MediumSlot), new PyInt(shieldExtender))))),
                Map(),
                PyBool.True,
            ],
        }));

        Assert.Empty(reply.Items);
        var storedOldModule = (await rig.Items.GetAsync(oldModule))!;
        var storedReplacement = (await rig.Items.GetAsync(replacement))!;
        Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar), (storedOldModule.LocationId, storedOldModule.Flag));
        Assert.Equal((ship, MediumSlot), (storedReplacement.LocationId, storedReplacement.Flag));
    }

    /// <summary>Records the packets a session sends, and counts the notifications of a name (the name is the broadcast address's).</summary>
    private sealed class RecordingSink : IPacketSink
    {
        public List<PyValue> Packets { get; } = [];
        public void Send(PyValue packet) => Packets.Add(packet);
        public int Named(string name) => Packets.Count(packet => Mentions(packet, name));

        private static bool Mentions(PyValue value, string name) => value switch
        {
            PyStr text => text.Value == name,
            PyObject obj => Mentions(obj.Args, name),
            PyTuple tuple => tuple.Items.Any(item => Mentions(item, name)),
            PyList list => list.Items.Any(item => Mentions(item, name)),
            _ => false,
        };
    }
}
