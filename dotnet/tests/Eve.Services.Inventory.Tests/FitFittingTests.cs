using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Skills;

namespace Eve.Services.Inventory.Tests;

/// <summary>
/// The fitting of a saved fitting's modules onto a docked ship (EveJS <c>Handle_FitFitting</c>, module leg): the modules go into the slots they name, the
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
            [new CharacterSkill(3386, 1, 0), new CharacterSkill(24241, 1, 0), new CharacterSkill(3436, 1, 0)]);
        await transaction.CommitAsync();
    }

    private static PyDict Map(params (PyValue Key, PyValue Value)[] entries) => new([.. entries.Select(e => new KeyValuePair<PyValue, PyValue>(e.Key, e.Value))]);

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
                Map((new PyInt(Afterburner), new PyList([new PyLong(afterburner)])), (new PyInt(CivilianMiner), new PyList([new PyLong(miner)]))),
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
        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
    }

    private const int Antimatter = 230, SmallRig = 26929;

    private static async Task<PyValue> FitAsync(Rig rig, ClientSession session, long ship, string inventory, PyDict itemsByType, PyDict modules, PyDict bays, PyDict cargo, bool fitRigs) =>
        await ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "FitFitting",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyLong(ship), new PyInt(Ibis), itemsByType, new PyLong(Rig.Station), Py.KeyVal(
                ("modulesByFlag", modules), ("chargesByType", bays)), cargo, fitRigs ? PyBool.True : PyBool.False],
        });

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
}
