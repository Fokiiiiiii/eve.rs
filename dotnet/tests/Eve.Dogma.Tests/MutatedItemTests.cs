namespace Eve.Dogma.Tests;

/// <summary>
/// A mutated module (an Abyssal module) is typed as its result but takes its base values from the input it was made from, its skill requirements from
/// the result, and its mutated values are its own. The worked example is mutaplasmid 47297 on input 12052 (50MN Microwarpdrive I), giving result 47408.
/// The values are the pinned SDE's; the mutated values are the worked example's rolls applied to the input's base (speedFactor 500 * 0.95, and so on).
/// </summary>
public class MutatedItemTests
{
    private const long Owner = 140_000_001, Ship = 9_988_400_000_000, Module = Ship + 1, Twin = Ship + 2;
    private const int Input = 12052, Result = 47408, Rifter = 587;
    private const int Speed = 20, Power = 30, Cpu = 50, PowerLoad = 15, CpuLoad = 49;

    /// <summary>The module's own values after the mutation: capacitorNeed 180, speedFactor 475, power 198.75, cpu 40, signatureRadiusBonus 650.</summary>
    private static readonly (int Attribute, double Value)[] MutatedValues = [(6, 180.0), (Speed, 475.0), (Power, 198.75), (Cpu, 40.0), (554, 650.0)];

    private static async Task<DogmaStatic> StaticsAsync()
    {
        var sde = await PgFactAttribute.SdeAsync();
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([Input, Result, Rifter]);
        return statics;
    }

    private static DogmaItem MutatedModule(ItemState state = ItemState.Offline) => new(Module, Result, Owner, Ship, 11, state, baseTypeId: Input);

    private static void Mutate(DogmaWorld world)
    {
        foreach (var (attribute, value) in MutatedValues)
        {
            world.SetValue(Module, attribute, value);
        }
    }

    [PgFact]
    public async Task A_mutated_module_has_its_mutated_values_and_takes_the_rest_of_its_base_from_its_input()
    {
        var statics = await StaticsAsync();
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(Ship, Rifter, Owner, 0));
        world.Add(MutatedModule());
        Mutate(world);

        foreach (var (attribute, value) in MutatedValues)
        {
            Assert.Equal(value, world.Get(Module, attribute), 9);
        }
        // not mutated: the input's values (the result has none of these)
        Assert.Equal(40.0, world.Get(Module, 9), 9);
        Assert.Equal(10000.0, world.Get(Module, 73), 9);
        Assert.Equal(50.0, world.Get(Module, 1223), 9);

        var attributes = world.Attributes(Module);
        foreach (var attribute in new[] { 6, Speed, Power, Cpu, 554, 9, 73, 182, 277 })
        {
            Assert.Contains(attribute, attributes.Keys);
        }
    }

    [PgFact]
    public async Task A_mutated_module_takes_its_skill_requirements_from_its_result()
    {
        var statics = await StaticsAsync();
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(Ship, Rifter, Owner, 0));
        world.Add(MutatedModule());

        // the input needs a level 1 skill (277 = 1), the result a level 4 one (277 = 4): the result's level is the one that applies
        Assert.Equal(3454.0, world.Get(Module, 182), 9);
        Assert.Equal(4.0, world.Get(Module, 277), 9);
        Assert.Equal(1.0, statics.Type(Input)!.Attributes[277], 9);
    }

    [PgFact]
    public async Task Without_its_own_values_a_mutated_module_reads_the_base_of_its_input()
    {
        var statics = await StaticsAsync();
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(Ship, Rifter, Owner, 0));
        world.Add(MutatedModule());

        var input = statics.Type(Input)!.Attributes;
        Assert.Equal(input[Power], world.Get(Module, Power), 9);
        Assert.Equal(input[Cpu], world.Get(Module, Cpu), 9);
        Assert.Equal(input[Speed], world.Get(Module, Speed), 9);
        Assert.Equal(input[554], world.Get(Module, 554), 9);
        Assert.Equal(input[6], world.Get(Module, 6), 9);
    }

    [PgFact]
    public async Task An_unmutated_twin_of_the_same_input_keeps_the_input_values()
    {
        var statics = await StaticsAsync();
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(Ship, Rifter, Owner, 0));
        world.Add(MutatedModule());
        Mutate(world);
        world.Add(new DogmaItem(Twin, Input, Owner, Ship, 12));

        var twin = world.Item(Twin)!;
        Assert.Null(twin.BaseTypeId);
        Assert.Equal(statics.Type(Input)!.Attributes.Keys.OrderBy(a => a), world.Attributes(Twin).Keys.OrderBy(a => a));
        foreach (var (attribute, value) in statics.Type(Input)!.Attributes)
        {
            Assert.Equal(value, world.Get(Twin, attribute), 9);
        }
        Assert.Equal(150.0, world.Get(Twin, Power), 9);
        Assert.Equal(50.0, world.Get(Twin, Cpu), 9);
        Assert.Equal(1.0, world.Get(Twin, 277), 9);
        Assert.Equal(500.0, world.Get(Twin, Speed), 9);
    }

    [PgFact]
    public async Task The_mutated_power_and_cpu_reach_the_ship_only_while_the_module_is_online()
    {
        var statics = await StaticsAsync();
        var world = new DogmaWorld(statics, shipId: Ship);
        world.Add(new DogmaItem(Ship, Rifter, Owner, 0));
        world.Add(MutatedModule(ItemState.Online));
        Mutate(world);
        world.Add(new DogmaItem(Twin, Input, Owner, Ship, 12, ItemState.Online));

        // the ship's load is the sum of its online modules' power and cpu: the mutated module, and the unmutated twin it is compared with
        Assert.Equal(198.75 + 150.0, world.Get(Ship, PowerLoad), 9);
        Assert.Equal(40.0 + 50.0, world.Get(Ship, CpuLoad), 9);

        world.SetState(Module, ItemState.Offline);
        Assert.Equal(150.0, world.Get(Ship, PowerLoad), 9);
        Assert.Equal(50.0, world.Get(Ship, CpuLoad), 9);

        world.SetState(Module, ItemState.Online);
        Assert.Equal(198.75 + 150.0, world.Get(Ship, PowerLoad), 9);

        // the mutated module adds exactly its mutated amount over the unmutated one: 198.75 - 150 power, 40 - 50 cpu
        world.SetState(Twin, ItemState.Offline);
        Assert.Equal(198.75, world.Get(Ship, PowerLoad), 9);
        Assert.Equal(40.0, world.Get(Ship, CpuLoad), 9);
    }
}
