using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Skills;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The drones of a pilot as the host has them: launched from the drone bay (the item leaves it, the simulation is asked for the ball, the client is told), refused when the pilot has none
/// to fly, sent at targets, taken back into the bay when the simulation scoops them, and deleted when they are destroyed.
/// </summary>
public class DroneHostTests
{
    private const int Hobgoblin = 2454, DronesSkill = 3436, DroneDamageAmplifier = 47447;

    private sealed record Flying(Rig Rig, Eve.Services.ClientSession Session, long Drone, long Character, string Ship, string Entity, int System);

    /// <summary>A pilot in space with a Hobgoblin I in the drone bay of its ship, and the Drones skill at level 1 unless <paramref name="skill"/> is 0.</summary>
    private static async Task<Flying> FlyingWithADroneAsync(
        Rig rig, int skill = 1, bool withDroneDamageAmplifier = false, bool withDroneLinkAugmentor = false, int? droneTypeId = null)
    {
        var character = await rig.NewCharacterAsync();
        if (skill > 0)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await rig.Skills.GrantAsync(connection, transaction, character.CharacterId, [new CharacterSkill(DronesSkill, skill, 250)]);
            await transaction.CommitAsync();
        }
        if (withDroneDamageAmplifier || withDroneLinkAugmentor)
        {
            await using var connection = await rig.Source.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (withDroneDamageAmplifier)
            {
                var amplifierId = await rig.Items.CreateAsync(connection, transaction,
                    new NewItem(DroneDamageAmplifier, character.CharacterId, character.ShipId, Eve.Inventory.ItemFlags.LowSlot0, 1, true));
                await rig.Modules.SetAsync(connection, transaction, amplifierId, Eve.Dogma.ItemState.Online);
            }
            if (withDroneLinkAugmentor)
            {
                const int droneLinkAugmentor = 23527;
                var augmentorId = await rig.Items.CreateAsync(connection, transaction,
                    new NewItem(droneLinkAugmentor, character.CharacterId, character.ShipId, Eve.Inventory.ItemFlags.HiSlot0, 1, true));
                await rig.Modules.SetAsync(connection, transaction, augmentorId, Eve.Dogma.ItemState.Online);
            }
            await transaction.CommitAsync();
        }
        var drone = (await rig.Ships.GrantAsync(character.CharacterId, droneTypeId ?? Hobgoblin, 1, singleton: false))!.Single().After;
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(character.CharacterId, drone.Id, Rig.Station, character.ShipId, CharacterShips.DroneBayFlag, null));
        var session = await rig.SessionAsync(character, 1);
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        var ship = await rig.BindShipInSpaceAsync(session);
        var entity = await rig.BindEntityAsync(session);
        Events.Of(session);
        return new Flying(rig, session, drone.Id, character.CharacterId, ship, entity, (int)session.GetInt("solarsystemid")!.Value);
    }

    private static Task<PyValue> LaunchAsync(Flying flying) =>
        flying.Rig.ShipCallAsync(flying.Session, "LaunchDrones", flying.Ship, new PyList([Py.Tuple(InventoryWire.Id(flying.Drone), new PyInt(1))]), PyNone.Instance, PyBool.False);

    private static string[] Refusals(PyValue answer) =>
        [.. ((PyDict)answer).Entries.SelectMany(e => e.Value is PyList list ? list.Items : [e.Value])
            .Select(v => v is PyTuple { Items: [_, PyDict notify] } ? JsSemantics.StringValue(notify.Entries[0].Value)! : "")];

    [PgFact]
    public async Task A_drone_leaves_the_bay_the_simulation_is_asked_for_it_and_the_client_is_told()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig);

        var answer = await LaunchAsync(flying);

        // {item: [the drone that went out]}
        var entry = Assert.Single(Assert.IsType<PyDict>(answer).Entries);
        Assert.Equal(flying.Drone, Events.Number(entry.Key));
        Assert.Equal([flying.Drone], Assert.IsType<PyList>(entry.Value).Items.Select(Events.Number));
        // the item is out of the bay, in the solar system, a drone of its own
        var item = (await rig.Items.GetAsync(flying.Drone))!;
        Assert.Equal((flying.System, CharacterShips.InSpaceFlag, true), (item.LocationId, item.Flag, item.Singleton));
        // the scene is asked for a ball that flies as the type says: 700 m round the ship, a laser that reaches 2,800 m
        var launch = Assert.Single(rig.Sim.Heard.OfType<SimLaunchDrone>());
        Assert.NotNull(launch.Weapon);
        Assert.Equal(((ulong)flying.Session.SessionId, Hobgoblin, 1099, 700.0, 2800.0), (launch.SessionId, launch.Ship.TypeId, launch.GraphicId, launch.OrbitDistance, launch.AttackRange));
        Assert.Equal(10, launch.Weapon.EffectId);
        // the client is told what dogma has of the drone, then that the item moved
        var told = Events.Of(flying.Session).Select(e => e.Name).ToArray();
        Assert.Equal(["OnGodmaPrimeItem", "OnItemChange"], told.Take(2));
    }

    [PgFact]
    public async Task An_online_drone_damage_amplifier_increases_hobgoblin_weapon_damage_by_ten_percent()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig, withDroneDamageAmplifier: true);
        var snapshot = await rig.Ships.LoadAsync(flying.Character);
        Assert.NotNull(snapshot);
        var amplifier = Assert.Single(snapshot.Fitted, item => item.TypeId == DroneDamageAmplifier);
        Assert.Equal(Eve.Dogma.ItemState.Online, snapshot.World.Item(amplifier.Id)?.State);

        var baseWeapon = Eve.Npc.NpcArmament.HullWeapon(rig.Statics.Type(Hobgoblin)!, flying.Drone, rig.Statics)!;
        await LaunchAsync(flying);

        var launch = Assert.Single(rig.Sim.Heard.OfType<SimLaunchDrone>());
        Assert.NotNull(launch.Weapon);
        var expectedDamage = baseWeapon.RawDamage.Select(damage => Eve.Npc.NpcArmament.Round6(damage * 1.1)).ToArray();
        Assert.Equal(expectedDamage, launch.Weapon.RawDamage);
    }

    [PgFact]
    public async Task A_pilot_flies_as_many_drones_as_its_skills_allow_and_is_refused_the_rest()
    {
        await using var rig = await Rig.CreateAsync();
        // a new character has Drones at level 1 from the start: one drone in space (the Drones skill gives one drone a level)
        var flying = await FlyingWithADroneAsync(rig, skill: 0);

        var answer = await rig.ShipCallAsync(flying.Session, "LaunchDrones", flying.Ship, new PyList([Py.Tuple(InventoryWire.Id(flying.Drone), new PyInt(2))]), PyNone.Instance, PyBool.False);

        // the first goes out, the second is refused with the reason (the answer lists, for the item, what became of each try)
        var tries = Assert.IsType<PyList>(Assert.Single(Assert.IsType<PyDict>(answer).Entries).Value).Items;
        Assert.Equal(flying.Drone, Events.Number(tries[0]));
        Assert.Equal("Maximum active drones already in space.", Refusals(answer).Last());
        Assert.Single(rig.Sim.Heard.OfType<SimLaunchDrone>());
    }

    [PgFact]
    public async Task An_order_is_given_to_the_drones_of_the_pilot_and_what_they_refuse_is_told_for_each()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig);
        await LaunchAsync(flying);
        var ids = new PyList([InventoryWire.Id(flying.Drone)]);

        var engage = await rig.EntityCallAsync(flying.Session, "CmdEngage", flying.Entity, ids, PyNone.Instance);
        Assert.Empty(Assert.IsType<PyDict>(engage).Entries);
        var engageOrder = rig.Sim.Heard.OfType<SimDroneCommand>().Last();
        Assert.Equal((SimDroneCommands.Engage, 0L), (engageOrder.Command, engageOrder.TargetId));
        Assert.Equal([flying.Drone], engageOrder.DroneIds);
        Assert.Equal(25_000.0, engageOrder.ControlRange);

        rig.Sim.DroneAnswer = (_, _, _, drones) =>
            new CommandReply(true, 0, null) { Drones = [.. drones.Select(id => new SimDroneResult(id, "NO_OPPONENT"))] };
        var refused = await rig.EntityCallAsync(flying.Session, "CmdEngage", flying.Entity, ids, PyNone.Instance);
        Assert.Equal(["The drones found nothing to attack."], Refusals(refused));
        rig.Sim.DroneAnswer = null;

        var commandStart = rig.Sim.Heard.OfType<SimDroneCommand>().Count();
        var assist = await rig.EntityCallAsync(flying.Session, "CmdAssist", flying.Entity, new PyInt(flying.Character), ids);
        var guard = await rig.EntityCallAsync(flying.Session, "CmdGuard", flying.Entity, new PyInt(flying.Character), ids);
        const long mineTarget = 7_100_001;
        var mine = await rig.EntityCallAsync(flying.Session, "CmdMineRepeatedly", flying.Entity, ids, new PyLong(mineTarget));
        const long wreckTarget = 7_100_002;
        var salvage = await rig.EntityCallAsync(flying.Session, "CmdSalvage", flying.Entity, ids, new PyLong(wreckTarget));

        foreach (var answer in new[] { assist, guard, mine, salvage })
        {
            Assert.Empty(Assert.IsType<PyDict>(answer).Entries);
        }

        var commands = rig.Sim.Heard.OfType<SimDroneCommand>().Skip(commandStart).ToList();
        Assert.Collection(commands,
            order => Assert.Equal(((ulong)flying.Session.SessionId, SimDroneCommands.Assist, flying.Character, flying.Drone),
                (order.SessionId, order.Command, order.TargetId, Assert.Single(order.DroneIds))),
            order => Assert.Equal(((ulong)flying.Session.SessionId, SimDroneCommands.Guard, flying.Character, flying.Drone),
                (order.SessionId, order.Command, order.TargetId, Assert.Single(order.DroneIds))),
            order => Assert.Equal(((ulong)flying.Session.SessionId, SimDroneCommands.MineRepeatedly, mineTarget, flying.Drone),
                (order.SessionId, order.Command, order.TargetId, Assert.Single(order.DroneIds))),
            order => Assert.Equal(((ulong)flying.Session.SessionId, SimDroneCommands.Salvage, wreckTarget, flying.Drone),
                (order.SessionId, order.Command, order.TargetId, Assert.Single(order.DroneIds))));

        rig.Sim.DroneAnswer = (_, command, _, drones) =>
            new CommandReply(true, 0, null)
            {
                Drones = [.. drones.Select(id => new SimDroneResult(id, command == SimDroneCommands.Salvage ? "NO_WRECK" : ""))]
            };
        var autoSalvage = await rig.EntityCallAsync(flying.Session, "CmdSalvage", flying.Entity, ids, PyNone.Instance);
        Assert.Equal(["No salvageable wreck was found."], Refusals(autoSalvage));
        var autoOrder = rig.Sim.Heard.OfType<SimDroneCommand>().Last();
        Assert.Equal((SimDroneCommands.Salvage, 0L, flying.Drone),
            (autoOrder.Command, autoOrder.TargetId, Assert.Single(autoOrder.DroneIds)));
    }


    [PgFact]
    public async Task A_drone_link_augmentor_adds_its_fitted_dogma_range_to_drone_commands()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig, withDroneLinkAugmentor: true);
        await LaunchAsync(flying);

        var ids = new PyList([InventoryWire.Id(flying.Drone)]);
        var answer = await rig.EntityCallAsync(flying.Session, "CmdEngage", flying.Entity, ids, PyNone.Instance);

        Assert.Empty(Assert.IsType<PyDict>(answer).Entries);
        var order = Assert.Single(rig.Sim.Heard.OfType<SimDroneCommand>());
        Assert.Equal(45_000.0, order.ControlRange);
    }

    [PgFact]
    public async Task A_drone_the_simulation_scoops_is_back_in_the_bay_and_one_that_is_destroyed_is_gone()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig);
        await LaunchAsync(flying);
        Events.Of(flying.Session);

        rig.Sim.Say(new SimDroneScooped((ulong)flying.Session.SessionId, flying.Drone));
        await Rig.UntilAsync(() => rig.Items.GetAsync(flying.Drone).GetAwaiter().GetResult() is { Flag: CharacterShips.DroneBayFlag });
        var back = (await rig.Items.GetAsync(flying.Drone))!;
        Assert.Equal((flying.Character == back.OwnerId, back.Singleton), (true, true));
        await Rig.UntilAsync(() => Events.Of(flying.Session, clear: false).Any(e => e.Name == "OnItemChange"));
        Assert.Contains(Events.Of(flying.Session, clear: false), e => e.Name == "OnItemChange");

        // out again, and destroyed: the item is deleted and the client is told it went to the junkyard
        await LaunchAsync(flying);
        Events.Of(flying.Session);
        rig.Sim.Say(new SimDestroyed(1UL << 62, flying.Drone, 0, [0, 0, 0], [1, 0, 0], 15, 0));
        await Rig.UntilAsync(() => rig.Items.GetAsync(flying.Drone).GetAwaiter().GetResult() is null);
        await Rig.UntilAsync(() => Events.Of(flying.Session, clear: false).Any(e => e.Name == "OnItemChange"));
        Assert.Contains(Events.Of(flying.Session, clear: false), e => e.Name == "OnItemChange");
    }


    [PgFact]
    public async Task Inventory_add_and_get_capacity_use_the_drone_scene_and_complete_the_normal_scoop_notifications()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig);
        await LaunchAsync(flying);
        var inventory = await rig.BindInventoryShipInSpaceAsync(flying.Session);

        var capacity = Assert.IsType<PyObject>(await rig.InventoryCallAsync(flying.Session, "GetCapacity", inventory, [new PyInt(CharacterShips.DroneBayFlag)]));
        Assert.Equal(new PyStr("util.KeyVal"), capacity.Name);
        var room = Assert.IsType<PyDict>(capacity.Args);
        Assert.Equal(500_000d, Numeric(room.Entries.Single(e => e.Key == new PyStr("capacity")).Value));
        Assert.Equal(0d, Numeric(room.Entries.Single(e => e.Key == new PyStr("used")).Value));

        Events.Of(flying.Session);
        var added = await rig.InventoryCallAsync(
            flying.Session,
            "Add",
            inventory,
            [InventoryWire.Id(flying.Drone), new PyInt(flying.System)],
            new PyDict([new(new PyStr("flag"), new PyInt(CharacterShips.DroneBayFlag))]));
        Assert.Equal(flying.Drone, Events.Number(added));
        var command = Assert.Single(rig.Sim.Heard.OfType<SimDroneCommand>());
        Assert.Equal(((ulong)flying.Session.SessionId, SimDroneCommands.ReturnBay, (long)flying.Session.GetInt("shipid")!.Value, flying.Drone),
            (command.SessionId, command.Command, command.TargetId, Assert.Single(command.DroneIds)));
        Assert.Equal((flying.System, CharacterShips.InSpaceFlag), ((await rig.Items.GetAsync(flying.Drone))!.LocationId, (await rig.Items.GetAsync(flying.Drone))!.Flag));

        rig.Sim.Say(new SimDroneScooped((ulong)flying.Session.SessionId, flying.Drone));
        await Rig.UntilAsync(() => rig.Items.GetAsync(flying.Drone).GetAwaiter().GetResult() is { LocationId: var location, Flag: CharacterShips.DroneBayFlag }
            && location == flying.Session.GetInt("shipid"));
        Assert.Contains(Events.Of(flying.Session, clear: false), e => e.Name == "OnItemChange");
    }

    [PgFact]
    public async Task Inventory_add_does_not_move_a_database_only_drone_and_rejects_a_scene_refusal()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig);
        await LaunchAsync(flying);
        var inventory = await rig.BindInventoryShipInSpaceAsync(flying.Session);
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Items.CreateAsync(connection, transaction, new NewItem(Hobgoblin, flying.Character, flying.System, CharacterShips.InSpaceFlag, 1, true));
            await transaction.CommitAsync();
        }
        var unsupported = (await rig.Items.InLocationAsync(flying.System, flying.Character)).Single(i => i.TypeId == Hobgoblin && i.Id != flying.Drone);
        var moveCommands = rig.Sim.Heard.OfType<SimDroneCommand>().Count();

        var orphan = await rig.InventoryCallAsync(
            flying.Session,
            "Add",
            inventory,
            [InventoryWire.Id(unsupported.Id), new PyInt(flying.System)],
            new PyDict([new(new PyStr("flag"), new PyInt(CharacterShips.DroneBayFlag))]));
        Assert.Same(PyNone.Instance, orphan);
        Assert.Equal(moveCommands, rig.Sim.Heard.OfType<SimDroneCommand>().Count());
        Assert.Equal((flying.System, CharacterShips.InSpaceFlag), ((await rig.Items.GetAsync(unsupported.Id))!.LocationId, (await rig.Items.GetAsync(unsupported.Id))!.Flag));

        rig.Sim.DroneAnswer = (_, _, _, drones) => new CommandReply(true, 0, null)
        {
            Drones = [.. drones.Select(id => new SimDroneResult(id, "OUT_OF_RANGE"))],
        };
        var refused = await rig.InventoryCallAsync(
            flying.Session,
            "Add",
            inventory,
            [InventoryWire.Id(flying.Drone), new PyInt(flying.System)],
            new PyDict([new(new PyStr("flag"), new PyInt(CharacterShips.DroneBayFlag))]));
        Assert.Same(PyNone.Instance, refused);
        Assert.Equal((flying.System, CharacterShips.InSpaceFlag), ((await rig.Items.GetAsync(flying.Drone))!.LocationId, (await rig.Items.GetAsync(flying.Drone))!.Flag));
    }


    [PgFact]
    public async Task A_mining_drone_cycle_depletes_its_asteroid_and_persists_ore_before_acknowledgement()
    {
        await using var rig = await Rig.CreateAsync();
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var droneTypeId = SdeTypeId(types, "Mining Drone I");
        var oreTypeId = SdeTypeId(types, "Veldspar");
        await rig.Statics.EnsureTypesAsync([droneTypeId, oreTypeId]);
        Assert.Equal("Drone", rig.Statics.CategoryName(rig.Statics.Type(droneTypeId)!.CategoryId));
        Assert.Equal("Asteroid", rig.Statics.CategoryName(rig.Statics.Type(oreTypeId)!.CategoryId));

        var flying = await FlyingWithADroneAsync(rig, droneTypeId: droneTypeId);
        await LaunchAsync(flying);
        var asteroid = await rig.Ships.CreateAsteroidAsync(
            oreTypeId, flying.System, 0, 1, [0d, 0d, 0d], [0d, 0d, 1d], 10d, rig.SpaceItems);

        var order = await rig.EntityCallAsync(
            flying.Session, "CmdMineRepeatedly", flying.Entity, new PyList([InventoryWire.Id(flying.Drone)]), new PyLong(asteroid.Item.Id));
        Assert.Empty(Assert.IsType<PyDict>(order).Entries);
        Events.Of(flying.Session);
        const ulong cycleId = 7;
        rig.Sim.Say(DroneWorkCycle(flying, asteroid.Item.Id, SimDroneCommands.MineRepeatedly, cycleId, 0.5d));

        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDroneWorkResult>().Any(result => result.CycleId == cycleId));
        var result = Assert.Single(rig.Sim.Heard.OfType<SimDroneWorkResult>(), result => result.CycleId == cycleId);
        Assert.True(result.Accepted);
        Assert.Equal(0, result.UnitsRemaining);
        Assert.Equal(1, result.DeliveredUnits);
        Assert.True(result.Complete);
        Assert.Equal(0, (await rig.Items.GetAsync(asteroid.Item.Id))!.Quantity);
        Assert.NotNull(await rig.SpaceItems.GetAsync(asteroid.Item.Id));
        var heardAfterDepletion = rig.Sim.Heard.Count;
        await rig.Containers.RestoreAsync(flying.System);
        Assert.Empty(rig.Sim.Heard.Skip(heardAfterDepletion).OfType<SimPlaceAsteroid>());

        var shipId = flying.Session.GetInt("shipid")!.Value;
        var ore = Assert.Single(await rig.Items.InLocationAsync(shipId, flying.Character),
            item => item.Flag == CharacterShips.CargoFlag && item.TypeId == oreTypeId);
        Assert.Equal(1, ore.Quantity);
        await Rig.UntilAsync(() => Events.Of(flying.Session, clear: false).Any(e => e.Name == "OnItemChange"));
    }

    [PgFact]
    public async Task A_salvage_drone_cycle_consumes_a_wreck_and_persists_an_sde_salvage_product()
    {
        await using var rig = await Rig.CreateAsync();
        var types = await new Eve.Sde.SdeReader(rig.Source).ReadTypeSummariesAsync();
        var droneTypeId = SdeTypeId(types, "Salvage Drone I");
        var wreckTypeId = Assert.Single(types, type => type.Name == "Amarr Frigate Wreck").TypeId;
        await rig.Statics.EnsureTypesAsync([droneTypeId, wreckTypeId]);
        Assert.Equal("Drone", rig.Statics.CategoryName(rig.Statics.Type(droneTypeId)!.CategoryId));
        var wreckType = rig.Statics.Type(wreckTypeId)!;
        Assert.Contains("Wreck", rig.Statics.GroupName(wreckType.GroupId), StringComparison.OrdinalIgnoreCase);

        var flying = await FlyingWithADroneAsync(rig, droneTypeId: droneTypeId);
        await LaunchAsync(flying);
        long wreckId;
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            wreckId = await rig.Items.CreateAsync(connection, transaction,
                new NewItem(wreckTypeId, flying.Character, flying.System, CharacterShips.InSpaceFlag, 1, true));
            await rig.SpaceItems.SaveAsync(connection, transaction,
                new SpaceItem(wreckId, flying.System, [0d, 0d, 0d], [0d, 0d, 1d], 10d, wreckType.GroupId,
                    0, flying.Character, 0, 0, 0, long.MaxValue));
            await transaction.CommitAsync();
        }

        var order = await rig.EntityCallAsync(
            flying.Session, "CmdSalvage", flying.Entity, new PyList([InventoryWire.Id(flying.Drone)]), new PyLong(wreckId));
        Assert.Empty(Assert.IsType<PyDict>(order).Entries);
        Events.Of(flying.Session);
        const ulong cycleId = 8;
        rig.Sim.Say(DroneWorkCycle(flying, wreckId, SimDroneCommands.Salvage, cycleId));

        await Rig.UntilAsync(() => rig.Sim.Heard.OfType<SimDroneWorkResult>().Any(result => result.CycleId == cycleId));
        var result = Assert.Single(rig.Sim.Heard.OfType<SimDroneWorkResult>(), result => result.CycleId == cycleId);
        Assert.True(result.Accepted);
        Assert.Equal(1, result.DeliveredUnits);
        Assert.True(result.Complete);
        Assert.Null(await rig.Items.GetAsync(wreckId));
        Assert.Null(await rig.SpaceItems.GetAsync(wreckId));

        var shipId = flying.Session.GetInt("shipid")!.Value;
        var cargo = (await rig.Items.InLocationAsync(shipId, flying.Character))
            .Where(item => item.Flag == CharacterShips.CargoFlag).ToArray();
        await rig.Statics.EnsureTypesAsync(cargo.Select(item => item.TypeId));
        var product = Assert.Single(cargo, item =>
        {
            var type = rig.Statics.Type(item.TypeId);
            return type is not null && rig.Statics.GroupName(type.GroupId).Equals("Salvaged Materials", StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(1, product.Quantity);
        await Rig.UntilAsync(() => Events.Of(flying.Session, clear: false).Any(e => e.Name == "OnItemChange"));
    }

    private static int SdeTypeId(IReadOnlyList<Eve.Sde.SdeTypeSummary> types, string name) =>
        Assert.Single(types, type => type.Published && type.Name == name).TypeId;

    private static SimDroneWorkCycle DroneWorkCycle(Flying flying, long targetId, byte command, ulong cycleId, double roll = 0)
    {
        var profile = Assert.Single(flying.Rig.Sim.Heard.OfType<SimLaunchDrone>()).Work;
        return new SimDroneWorkCycle(
            (ulong)flying.Session.SessionId, flying.Character, flying.Session.GetInt("shipid")!.Value, flying.System,
            flying.Drone, targetId, cycleId, command, profile.MiningAmountM3, profile.MiningWasteVolumeMultiplier,
            profile.MiningWasteProbability, profile.SalvageAccessDifficultyBonus, roll);
    }

    private static double Numeric(PyValue value) => value switch
    {
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        PyReal real => real.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };


    [PgFact]
    public async Task Inventory_turret_slot_rpc_returns_the_active_ships_nonnegative_remaining_count()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await FlyingWithADroneAsync(rig, skill: 0);
        var inventory = await rig.BindInventoryShipInSpaceAsync(flying.Session);
        var snapshot = await rig.Ships.LoadAsync(flying.Character);
        Assert.NotNull(snapshot);
        var expected = Math.Max(0L, (long)snapshot.World.Get(snapshot.Ship.Id, Eve.Dogma.BuiltInEffects.TurretSlotsLeft));

        var answer = await rig.InventoryCallAsync(flying.Session, "GetAvailableTurretSlots", inventory, []);

        Assert.IsType<PyInt>(answer);
        Assert.Equal(expected, Events.Number(answer));
    }
}
