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

    private static Dictionary<string, PyValue> UserErrorArguments(MachoWrappedException error)
    {
        var arguments = ((PyTuple)((PyTuple)((PyObjectEx)error.Error.Payload).Header).Items[1]).Items;
        return ((PyDict)arguments[1]).Entries.ToDictionary(entry => ((PyStr)entry.Key).Value, entry => entry.Value);
    }

    private static Task<PyValue> AddToShipAsync(Rig rig, ClientSession session, string inventory, long itemId, int flag) =>
        ((IService)rig.Service).CallAsync(new ServiceCall
        {
            Method = "Add",
            BoundObjectId = inventory,
            Session = session,
            Args = [new PyLong(itemId), new PyLong(Rig.Station)],
            Kwargs = new PyDict([new(new PyStr("flag"), new PyInt(flag))]),
        }).AsTask();

    [PgFact]
    public async Task Fitting_a_module_with_multiple_missing_skills_returns_all_prerequisites_in_the_client_error()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var module = await rig.AddAsync(450, Rig.Character, Rig.Station, singleton: true); // Gatling Pulse Laser I: Amarr Frigate I and Small Energy Turret I
        var session = Rig.Session();
        var shipInventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => AddToShipAsync(rig, session, shipInventory, module, HighSlot));

        Assert.Equal("FittingHasSkillPrerequisites", error.Error.UserErrorMessage);
        var values = UserErrorArguments(error);
        Assert.Equal(new PyStr("Small Energy Turret : Level 1, Gunnery : Level 1"), values["requiredSkills"]);
        Assert.Equal(new PyInt(450), values["item"]);
        Assert.Equal(new PyInt(2), values["skillCount"]);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(module))!.LocationId);
    }

    [PgFact]
    public async Task Fitting_past_the_last_turret_hardpoint_reports_that_the_hardpoints_are_full()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = await rig.AddAsync(17738, Rig.Character, Rig.Station, singleton: true); // Machariel: eight high slots and seven turret hardpoints
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, Rig.Character,
                [new CharacterSkill(3303, 1, 0), new CharacterSkill(3300, 1, 0)]);
            await rig.Characters.SetShipAsync(connection, transaction, Rig.Character, ship);
            await transaction.CommitAsync();
        }
        var session = Rig.Session();
        var shipInventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        for (var slot = 0; slot < 7; slot++)
        {
            var turret = await rig.AddAsync(450, Rig.Character, Rig.Station, singleton: true);
            Assert.Equal(InventoryWire.Id(turret), await AddToShipAsync(rig, session, shipInventory, turret, HighSlot + slot));
        }
        var eighthTurret = await rig.AddAsync(450, Rig.Character, Rig.Station, singleton: true);

        var error = await Assert.ThrowsAsync<MachoWrappedException>(() => AddToShipAsync(rig, session, shipInventory, eighthTurret, HighSlot + 7));

        Assert.Equal("CustomNotify", error.Error.UserErrorMessage);
        var notify = Assert.IsType<PyStr>(UserErrorArguments(error)["notify"]);
        Assert.Equal("All turret hardpoints on this ship are already in use.", notify.Value);
        Assert.Equal(Rig.Station, (await rig.Items.GetAsync(eighthTurret))!.LocationId);
    }

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
    public async Task A_module_stack_used_by_a_saved_fit_splits_one_singleton_and_switches_on_the_new_item()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var stack = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station, quantity: 3);
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 1, UserId = 34, CharacterId = Rig.Character };
        session.Set("stationid", new PyInt(Rig.Station));
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Afterburner), new PyList([new PyLong(stack)]))),
            Map((new PyInt(MediumSlot), new PyInt(Afterburner))), Map(), Map(), true));

        Assert.Empty(reply.Items);
        var remainder = (await rig.Items.GetAsync(stack))!;
        Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar, 2L, false),
            (remainder.LocationId, remainder.Flag, remainder.Quantity, remainder.Singleton));
        var fitted = Assert.Single(await rig.Items.InLocationAsync(ship, Rig.Character),
            item => item.TypeId == Afterburner && item.Flag == MediumSlot);
        Assert.NotEqual(stack, fitted.Id);
        Assert.Equal((1L, true), (fitted.Quantity, fitted.Singleton));
        Assert.True((await rig.Modules.StatesAsync([fitted.Id]))[fitted.Id] >= ItemState.Online);

        var notifications = sink.Packets
            .Select(packet => new[] { "OnMultiEvent", "OnItemsChanged", "OnItemChange" }.FirstOrDefault(name => RecordingSink.Mentions(packet, name)))
            .OfType<string>()
            .ToArray();
        Assert.Equal(["OnMultiEvent", "OnItemsChanged", "OnItemChange"], notifications);
        var moduleEvent = Assert.Single(sink.Packets, packet => RecordingSink.Mentions(packet, "OnMultiEvent"));
        Assert.True(RecordingSink.ContainsId(RecordingSink.NotificationPayload(moduleEvent)!, fitted.Id));
        var itemMove = Assert.Single(sink.Packets, packet => RecordingSink.Mentions(packet, "OnItemsChanged"));
        Assert.True(RecordingSink.ContainsId(RecordingSink.NotificationPayload(itemMove)!, fitted.Id));
        Assert.False(RecordingSink.ContainsId(RecordingSink.NotificationPayload(itemMove)!, stack));
        var stackChange = Assert.Single(sink.Packets, packet => RecordingSink.Mentions(packet, "OnItemChange"));
        Assert.True(RecordingSink.ContainsId(RecordingSink.NotificationPayload(stackChange)!, stack));

        var packetCount = sink.Packets.Count;
        var retry = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Afterburner), new PyList([new PyLong(stack)]))),
            Map((new PyInt(MediumSlot), new PyInt(Afterburner))), Map(), Map(), true));

        Assert.Empty(retry.Items);
        Assert.Equal(packetCount, sink.Packets.Count);
        Assert.Equal((Rig.Station, 2L), ((await rig.Items.GetAsync(stack))!.LocationId, (await rig.Items.GetAsync(stack))!.Quantity));
        Assert.Equal(fitted.Id, Assert.Single(await rig.Items.InLocationAsync(ship, Rig.Character),
            item => item.TypeId == Afterburner && item.Flag == MediumSlot).Id);
    }

    [PgFact]
    public async Task Fitting_a_module_stack_through_inventory_add_takes_one_unit_without_a_quantity_argument()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var stack = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station, quantity: 3);
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 1, UserId = 34, CharacterId = Rig.Character };
        session.Set("stationid", new PyInt(Rig.Station));
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = await AddToShipAsync(rig, session, inventory, stack, MediumSlot);

        var remainder = (await rig.Items.GetAsync(stack))!;
        Assert.Equal((Rig.Station, Eve.Inventory.ItemFlags.Hangar, 2L, false),
            (remainder.LocationId, remainder.Flag, remainder.Quantity, remainder.Singleton));
        var fitted = Assert.Single(await rig.Items.InLocationAsync(ship, Rig.Character),
            item => item.TypeId == Afterburner && item.Flag == MediumSlot);
        Assert.NotEqual(stack, fitted.Id);
        Assert.Equal((1L, true), (fitted.Quantity, fitted.Singleton));
        Assert.Equal(InventoryWire.Id(fitted.Id), reply);
        var itemMove = Assert.Single(sink.Packets, packet => RecordingSink.Mentions(packet, "OnItemsChanged"));
        Assert.True(RecordingSink.ContainsId(RecordingSink.NotificationPayload(itemMove)!, fitted.Id));
        Assert.False(RecordingSink.ContainsId(RecordingSink.NotificationPayload(itemMove)!, stack));
        var stackChange = Assert.Single(sink.Packets, packet => RecordingSink.Mentions(packet, "OnItemChange"));
        Assert.True(RecordingSink.ContainsId(RecordingSink.NotificationPayload(stackChange)!, stack));
    }

    [PgFact]
    public async Task A_single_module_stack_can_supply_every_matching_slot_in_a_saved_fit()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var stack = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station, quantity: 2);
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 1, UserId = 34, CharacterId = Rig.Character };
        session.Set("stationid", new PyInt(Rig.Station));
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(Afterburner), new PyList([new PyLong(stack)]))),
            Map((new PyInt(MediumSlot), new PyInt(Afterburner)), (new PyInt(MediumSlot + 1), new PyInt(Afterburner))),
            Map(), Map(), true));

        Assert.Empty(reply.Items);
        var fitted = (await rig.Items.InLocationAsync(ship, Rig.Character))
            .Where(item => item.TypeId == Afterburner && item.Flag is MediumSlot or MediumSlot + 1)
            .ToList();
        Assert.Equal(2, fitted.Count);
        Assert.All(fitted, item => Assert.Equal((1L, true), (item.Quantity, item.Singleton)));
        var itemMove = Assert.Single(sink.Packets, packet => RecordingSink.Mentions(packet, "OnItemsChanged"));
        var payload = RecordingSink.NotificationPayload(itemMove)!;
        Assert.All(fitted, item => Assert.True(RecordingSink.ContainsId(payload, item.Id)));
        Assert.DoesNotContain(sink.Packets, packet => RecordingSink.Mentions(packet, "OnItemChange"));
    }

    [PgFact]
    public async Task A_module_stack_that_fails_fit_rules_stays_in_the_hangar_and_is_reported_missing()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        var ship = await rig.AddAsync(Ibis, Rig.Character, Rig.Station, singleton: true);
        var stack = await rig.AddAsync(450, Rig.Character, Rig.Station, quantity: 2);
        var sink = new RecordingSink();
        var session = new ClientSession(sink) { SessionId = 1, ClientId = 1, UserId = 34, CharacterId = Rig.Character };
        session.Set("stationid", new PyInt(Rig.Station));
        var inventory = Rig.OidOf(await rig.CallAsync(session, "GetInventoryFromId", null, new PyLong(ship)));

        var reply = Assert.IsType<PyList>(await FitAsync(rig, session, ship, inventory,
            Map((new PyInt(450), new PyList([new PyLong(stack)]))),
            Map((new PyInt(HighSlot), new PyInt(450))), Map(), Map(), true));

        var missing = Assert.IsType<PyTuple>(Assert.Single(reply.Items));
        Assert.Equal([new PyInt(450), new PyInt(1)], missing.Items);
        var remainder = (await rig.Items.GetAsync(stack))!;
        Assert.Equal((Rig.Station, 2L, false), (remainder.LocationId, remainder.Quantity, remainder.Singleton));
        Assert.DoesNotContain(await rig.Items.InLocationAsync(ship, Rig.Character), item => item.TypeId == 450);
        Assert.Empty(sink.Packets);
    }

    [PgFact]
    public async Task Concurrent_moves_split_one_module_stack_into_distinct_slots_without_losing_items()
    {
        await using var rig = await Rig.CreateAsync(activeShip: true);
        await TrainAsync(rig);
        var ship = (await rig.Ships.LoadAsync(Rig.Character))!.Ship.Id;
        var stack = await rig.AddAsync(Afterburner, Rig.Character, Rig.Station, quantity: 2);

        var outcomes = await Task.WhenAll(
            rig.Ships.MoveAsync(Rig.Character, stack, Rig.Station, ship, MediumSlot, 1),
            rig.Ships.MoveAsync(Rig.Character, stack, Rig.Station, ship, MediumSlot + 1, 1));

        var changes = outcomes.Select(outcome => Assert.IsType<ShipChanged>(outcome)).ToArray();
        Assert.Equal(2, changes.Select(change => change.ItemId).Distinct().Count());
        var fitted = (await rig.Items.InLocationAsync(ship, Rig.Character))
            .Where(item => item.TypeId == Afterburner && item.Flag is MediumSlot or MediumSlot + 1)
            .ToList();
        Assert.Equal(2, fitted.Count);
        Assert.Equal(changes.Select(change => change.ItemId).Order(), fitted.Select(item => item.Id).Order());
        Assert.All(fitted, item => Assert.Equal((1L, true), (item.Quantity, item.Singleton)));
        var original = (await rig.Items.GetAsync(stack))!;
        Assert.Equal(ship, original.LocationId);
        Assert.Contains(original.Flag, new[] { MediumSlot, MediumSlot + 1 });
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

        public static bool Mentions(PyValue value, string name) => value switch
        {
            PyStr text => text.Value == name,
            PyObject obj => Mentions(obj.Args, name),
            PyTuple tuple => tuple.Items.Any(item => Mentions(item, name)),
            PyList list => list.Items.Any(item => Mentions(item, name)),
            _ => false,
        };

        public static bool ContainsId(PyValue value, long id) => value switch
        {
            PyInt integer => integer.Value == id,
            PyLong integer => integer.Value == id,
            PyPackedRow row => row.Values.Any(item => ContainsId(item, id)),
            PyObject obj => ContainsId(obj.Name, id) || ContainsId(obj.Args, id),
            PyObjectEx obj => ContainsId(obj.Header, id) || obj.List.Any(item => ContainsId(item, id))
                || obj.Dict.Any(entry => ContainsId(entry.Key, id) || ContainsId(entry.Value, id)),
            PyTuple tuple => tuple.Items.Any(item => ContainsId(item, id)),
            PyList list => list.Items.Any(item => ContainsId(item, id)),
            PyDict dict => dict.Entries.Any(entry => ContainsId(entry.Key, id) || ContainsId(entry.Value, id)),
            PySubStruct sub => ContainsId(sub.Value, id),
            _ => false,
        };

        public static PyValue? NotificationPayload(PyValue value)
        {
            if (value is not PyObject { Args: PyTuple packet } || packet.Items.Count <= 4
                || packet.Items[4] is not PyTuple streams || streams.Items.Count == 0
                || streams.Items[0] is not PyTuple { Items.Count: > 1 } stream
                || stream.Items[1] is not PyBuffer buffer)
            {
                return null;
            }
            return MarshalCodec.Decode(buffer.Value);
        }
    }
}
