using Eve.Inventory;
using Eve.Macho;
using Eve.Marshal;
using Eve.Ships;
using Eve.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A drone made by the abyssal mutation (ADR 0016) flies with the numbers of its mutation: a Hobgoblin I made with mutaplasmid 60460 (a Light Mutated
/// Drone) keeps its base in the input, takes its rolls from its own record, and that is what is launched, primed, recalled, reconnected and restarted with.
/// Base values from the SDE: structure 9 = 200, shield 263 = 50, armor 265 = 90, max velocity 37 = 2800, damage multiplier 64 = 1.6, thermal 118 = 20.
/// </summary>
public class MutatedDroneHostTests
{
    private const int MutatorForDrone = 60460, LightMutatedDrone = 60478;
    private const double Structure = 140, Shield = 46.25, Armor = 103.5, MaxVelocity = 3220, DamageMultiplier = 1.76, ThermalShot = 35.2;

    private static Task<PyValue> LaunchAsync(DroneHostTests.Flying flying) => DroneHostTests.LaunchAsync(flying);

    private static double NumberOf(PyValue value) => value switch
    {
        PyReal real => real.Value,
        PyInt integer => integer.Value,
        PyLong integer => integer.Value,
        _ => throw new InvalidOperationException(value.ToString()),
    };

    /// <summary>The dictionary that holds <paramref name="key"/> inside a value: a <c>util.KeyVal</c> object (as the server sends it), a dictionary or a tuple.</summary>
    internal static PyDict? DictWith(PyValue value, string key) => value switch
    {
        PyDict dict when dict.Entries.Any(e => JsSemantics.StringValue(e.Key) == key) => dict,
        PyObject obj => DictWith(obj.Args, key),
        PyTuple tuple => tuple.Items.Select(item => DictWith(item, key)).FirstOrDefault(found => found is not null),
        _ => null,
    };

    /// <summary>The attributes a drone's <c>OnGodmaPrimeItem</c> carries, as dogma gives them.</summary>
    internal static PyDict AttributesOf(IEnumerable<PyValue> payload) =>
        (PyDict)(payload.Select(arg => DictWith(arg, "attributes")).FirstOrDefault(found => found is not null)
            ?.Entries.Single(e => JsSemantics.StringValue(e.Key) == "attributes").Value ?? throw new InvalidOperationException("no attributes in the payload"));

    internal static double PrimedAttribute(Events.Event prime, int attribute) =>
        NumberOf(AttributesOf(prime.Args).Entries.Single(e => e.Key is PyInt key && key.Value == attribute).Value);

    private static void AssertFlownWithTheMutation(SimLaunchDrone launch)
    {
        Assert.Equal(LightMutatedDrone, launch.Ship.TypeId);
        Assert.Equal(MaxVelocity, launch.Ship.MaxVelocity, 6);
        Assert.Equal(Structure, launch.Ship.Vitals!.StructureHp, 6);
        Assert.Equal(Armor, launch.Ship.Vitals.ArmorHp, 6);
        Assert.Equal(Shield, launch.Ship.Vitals.ShieldCapacity, 6);
        Assert.NotNull(launch.Weapon);
        Assert.Equal(ThermalShot, launch.Weapon.RawDamage[1], 6);
    }

    [PgFact]
    public async Task A_mutated_drone_is_launched_with_its_mutated_numbers_and_primed_with_them()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, mutatorTypeId: MutatorForDrone);

        await LaunchAsync(flying);

        var launch = Assert.Single(rig.Sim.Heard.OfType<SimLaunchDrone>());
        AssertFlownWithTheMutation(launch);
        var prime = Assert.Single(Events.Of(flying.Session, clear: false), e => e.Name == "OnGodmaPrimeItem");
        Assert.Equal(MaxVelocity, PrimedAttribute(prime, 37), 6);
        Assert.Equal(Structure, PrimedAttribute(prime, 9), 6);
        Assert.Equal(DamageMultiplier, PrimedAttribute(prime, 64), 6);
    }

    [PgFact]
    public async Task A_mutated_drone_recalled_into_the_bay_is_launched_again_with_the_same_numbers()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, mutatorTypeId: MutatorForDrone);
        await LaunchAsync(flying);
        Events.Of(flying.Session);

        rig.Sim.Say(new SimDroneScooped((ulong)flying.Session.SessionId, flying.Drone));
        await Rig.UntilAsync(() => rig.Items.GetAsync(flying.Drone).GetAwaiter().GetResult() is { Flag: CharacterShips.DroneBayFlag });

        await LaunchAsync(flying);
        var relaunch = rig.Sim.Heard.OfType<SimLaunchDrone>().ToList();
        Assert.Equal(2, relaunch.Count);
        AssertFlownWithTheMutation(relaunch[1]);
    }

    [PgFact]
    public async Task A_mutated_drone_launched_by_a_restarted_host_has_the_numbers_its_mutation_gave()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, mutatorTypeId: MutatorForDrone);
        await LaunchAsync(flying);
        rig.Sim.Say(new SimDroneScooped((ulong)flying.Session.SessionId, flying.Drone));
        await Rig.UntilAsync(() => rig.Items.GetAsync(flying.Drone).GetAwaiter().GetResult() is { Flag: CharacterShips.DroneBayFlag });

        // a host restarted over the same database: new ship and drone services, nothing carried in memory
        var ships = new CharacterShips(rig.Source, rig.Statics, rig.Characters, rig.Items, rig.Skills, rig.Modules);
        var restarted = new SpaceDrones(rig.Sim, ships, rig.Statics, rig.Space, () => rig.Now, rig.Log.Add);
        await restarted.LaunchAsync(flying.Session, [new DroneRequest(flying.Drone, 1)], CancellationToken.None);

        var launch = rig.Sim.Heard.OfType<SimLaunchDrone>().Last();
        Assert.Equal(2, rig.Sim.Heard.OfType<SimLaunchDrone>().Count());
        AssertFlownWithTheMutation(launch);
    }

    [PgFact]
    public async Task A_mutated_drone_a_pilot_reconnects_to_is_primed_with_its_mutated_speed()
    {
        await using var rig = await Rig.CreateAsync();
        var flying = await DroneHostTests.FlyingWithADroneAsync(rig, mutatorTypeId: MutatorForDrone);
        await LaunchAsync(flying);
        await rig.Drones.CommandAsync(flying.Session, SimDroneCommands.Abandon, 0, [flying.Drone], CancellationToken.None);
        rig.Sim.Say(new SimDroneReleased((ulong)flying.Session.SessionId, flying.Drone, [0, 0, 0]));
        Events.Of(flying.Session);

        await rig.EntityCallAsync(flying.Session, "CmdReconnectToDrones", flying.Entity);

        var prime = Assert.Single(Events.Of(flying.Session), e => e.Name == "OnGodmaPrimeItem");
        Assert.Equal(MaxVelocity, PrimedAttribute(prime, 37), 6);
        Assert.Equal(Structure, PrimedAttribute(prime, 9), 6);
    }
}
