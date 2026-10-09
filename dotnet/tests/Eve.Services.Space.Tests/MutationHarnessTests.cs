using Eve.Host;
using Eve.Inventory;
using Eve.Inventory.Mutation;
using Eve.Macho;
using Eve.Marshal;
using Eve.Services;
using Eve.Ships;
using Eve.Skills;
using Eve.Sde;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The abyssal mutation end to end through the host's registration (ADR 0016, W9): the <c>dynamicItemService</c> route is registered in the registry
/// the way the Host registers it, a module (47297 on 50MN Microwarpdrive I, giving 47408) and a drone (60460 on Hobgoblin I, giving 60478) are made
/// by <c>CreateDynamicItem</c>, the module is fitted, the drone is launched from the bay, and what dogma and the client are given is asserted.
/// Skipped without EVE_PG_URL; skipped is not passed.
/// </summary>
public class MutationHarnessTests
{
    private const long ClockMs = Rig.ClockMs;
    private const int ModuleMutaplasmid = 47297, MicrowarpdriveI = 12052, AbyssalMicrowarpdrive = 47408, RequiredSkill = 3454;
    private const int DroneMutaplasmid = 60460, Hobgoblin = 2454, LightMutatedDrone = 60478, DronesSkill = 3436;

    /// <summary>The rolls of both mutations: the module's worked example (ADR 0016) and the drone's roll at the ends of its bands.</summary>
    private static readonly Dictionary<int, double> Rolls = new()
    {
        [6] = 0.5, [20] = 0.25, [30] = 0.75, [50] = 0.0, [554] = 1.0,
        [9] = 0, [37] = 0.5, [64] = 1, [263] = 0.5, [265] = 1,
    };

    private static readonly string[] Announced = ["OnItemChange", "OnItemChange", "OnItemChange"];

    private static async Task<long> GiveAsync(Rig rig, long owner, int type, long location, int flag, bool singleton)
    {
        await using var connection = await rig.Source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var id = await rig.Items.CreateAsync(connection, transaction, new NewItem(type, owner, location, flag, 1, singleton));
        await transaction.CommitAsync();
        return id;
    }

    /// <summary>The route as the Host builds it (Program.cs): the mutation authority over the rig's stores and bus, and the service over both.</summary>
    private static async Task<DynamicItemRpcService> RouteAsync(Rig rig)
    {
        var sde = new SdeReader(rig.Source);
        var catalog = await MutatorCatalog.LoadAsync(sde);
        var mutations = new ItemMutationService(rig.Source, rig.Items, rig.Items.Mutations, catalog, rig.Statics, new BusItemMutationEvents(rig.Events), () => ClockMs,
            attribute => Rolls.GetValueOrDefault(attribute, 0.5));
        return new DynamicItemRpcService(rig.Items, mutations, rig.Statics, new ItemTypeCatalog(sde));
    }

    private static async Task<long> MutateAsync(Rig rig, ServiceRegistry registry, ClientSession session, long mutator, long source)
    {
        var service = registry.Lookup("dynamicItemService") ?? throw new InvalidOperationException("the dynamic item route is not registered");
        var answer = await service.CallAsync(new ServiceCall
        {
            Method = "CreateDynamicItem", Args = [new PyLong(mutator), new PyLong(source)], Session = session, CallId = new PyInt(1),
        });
        return Events.Number(answer);
    }

    private static async Task<PyValue> InfoAsync(ServiceRegistry registry, ClientSession session, long item)
    {
        var service = registry.Lookup("dynamicItemService") ?? throw new InvalidOperationException("the dynamic item route is not registered");
        return await service.CallAsync(new ServiceCall
        {
            Method = "GetDynamicItemInfo", Args = [new PyLong(item)], Session = session, CallId = new PyInt(1),
        });
    }

    private static double NumberOf(PyValue value) => value switch
    {
        PyReal real => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    private static double AttributeOf(PyValue info, int attribute) =>
        NumberOf(MutatedDroneHostTests.AttributesOf([info]).Entries.Single(e => e.Key is PyInt key && key.Value == attribute).Value);

    [PgFact]
    public async Task A_mutated_module_and_drone_made_through_the_host_route_are_fitted_launched_and_their_numbers_reach_dogma_and_the_client()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        await using (var connection = await rig.Source.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await rig.Skills.GrantAsync(connection, transaction, c.CharacterId, [new CharacterSkill(RequiredSkill, 4, 250), new CharacterSkill(DronesSkill, 1, 250)]);
            await transaction.CommitAsync();
        }
        rig.Registry.Register(await RouteAsync(rig));
        var session = await rig.SessionAsync(c, 1);
        Events.Of(session);

        // the module: the starter afterburner goes to the hangar so that the made module takes the mid slot
        var afterburner = (await rig.Items.InLocationAsync(c.ShipId, c.CharacterId)).Single(i => i.Flag == ItemFlags.MedSlot0);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, afterburner.Id, c.ShipId, Rig.Station, ItemFlags.Hangar, null));
        var moduleMutator = await GiveAsync(rig, c.CharacterId, ModuleMutaplasmid, Rig.Station, ItemFlags.Hangar, singleton: true);
        var moduleSource = await GiveAsync(rig, c.CharacterId, MicrowarpdriveI, Rig.Station, ItemFlags.Hangar, singleton: true);
        var module = await MutateAsync(rig, rig.Registry, session, moduleMutator, moduleSource);
        Assert.Equal(["OnItemChange", "OnItemChange", "OnItemChange"], Events.Of(session).Select(e => e.Name));
        Assert.Equal(AbyssalMicrowarpdrive, (await rig.Items.GetAsync(module))!.TypeId);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, module, Rig.Station, c.ShipId, ItemFlags.MedSlot0, null));

        // the drone: a Hobgoblin I made into a Light Mutated Drone, moved into the bay
        var droneMutator = await GiveAsync(rig, c.CharacterId, DroneMutaplasmid, Rig.Station, ItemFlags.Hangar, singleton: true);
        var droneSource = await GiveAsync(rig, c.CharacterId, Hobgoblin, Rig.Station, ItemFlags.Hangar, singleton: true);
        var drone = await MutateAsync(rig, rig.Registry, session, droneMutator, droneSource);
        Assert.Equal(["OnItemChange", "OnItemChange", "OnItemChange"], Events.Of(session).Select(e => e.Name));
        Assert.Equal(LightMutatedDrone, (await rig.Items.GetAsync(drone))!.TypeId);
        Assert.IsType<ShipChanged>(await rig.Ships.MoveAsync(c.CharacterId, drone, Rig.Station, c.ShipId, CharacterShips.DroneBayFlag, null));

        // undock: the ship is in space with the fitted module and the drone in its bay
        var oid = await rig.BindShipAsync(session);
        await rig.ShipCallAsync(session, "Undock", oid, new PyLong(session.GetInt("shipid")!.Value), PyBool.False);
        await rig.BindBeyonceAsync(session);
        var ship = await rig.BindShipInSpaceAsync(session);
        await rig.BindEntityAsync(session);
        Events.Of(session);

        // dogma: the module carries the mutated power and CPU of its roll, the result's skill, and the input's unmutated values
        var world = (await rig.Ships.LoadAsync(c.CharacterId))!.World;
        Assert.Equal(198.75, world.Get(module, 30), 9);
        Assert.Equal(40.0, world.Get(module, 50), 9);
        Assert.Equal(3454.0, world.Get(module, 182), 9);
        Assert.Equal(40.0, world.Get(module, 9), 9);

        // the drone launched from the bay: the simulation is given its mutated speed, structure, armor, shield and damage
        var answer = await rig.ShipCallAsync(session, "LaunchDrones", ship, new PyList([Py.Tuple(InventoryWire.Id(drone), new PyInt(1))]), PyNone.Instance, PyBool.False);
        Assert.Equal([drone], Assert.IsType<PyDict>(answer).Entries.Select(e => Events.Number(e.Key)));
        var launch = Assert.Single(rig.Sim.Heard.OfType<SimLaunchDrone>());
        Assert.Equal(LightMutatedDrone, launch.Ship.TypeId);
        Assert.Equal(3220, launch.Ship.MaxVelocity, 6);
        Assert.Equal(140, launch.Ship.Vitals!.StructureHp, 6);
        Assert.Equal(103.5, launch.Ship.Vitals.ArmorHp, 6);
        Assert.Equal(46.25, launch.Ship.Vitals.ShieldCapacity, 6);
        Assert.Equal(35.2, launch.Weapon!.RawDamage[1], 6);

        // the client: the drone is primed with its mutated attributes, and the item moves out of the bay as an OnItemChange
        var told = Events.Of(session);
        var prime = Assert.Single(told, e => e.Name == "OnGodmaPrimeItem");
        Assert.Equal(3220, MutatedDroneHostTests.PrimedAttribute(prime, 37), 6);
        Assert.Contains(told, e => e.Name == "OnItemChange");

        // GetDynamicItemInfo answers the same numbers for both items, with their provenance
        var droneInfo = await InfoAsync(rig.Registry, session, drone);
        Assert.Equal(DroneMutaplasmid, Events.Number(MutatedDroneHostTests.DictWith(droneInfo, "mutatorTypeID")!.Entries.Single(e => JsSemantics.StringValue(e.Key) == "mutatorTypeID").Value));
        Assert.Equal(3220, AttributeOf(droneInfo, 37), 6);
        var moduleInfo = await InfoAsync(rig.Registry, session, module);
        Assert.Equal(198.75, AttributeOf(moduleInfo, 30), 6);
    }
}
