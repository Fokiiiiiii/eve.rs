namespace Eve.Dogma.Tests;

/// <summary>
/// The command burst value chain on the static data: a charge's buff value (<c>warfareBuff1Multiplier</c>, 2596) multiplies the module's value
/// (<c>warfareBuff1Value</c>, 2469; 1.0 for Tech I, 1.25 for Tech II) by effect 6737 (<c>chargeBonusWarfareCharge</c>, PostMul). The Shield Harmonizing
/// (42529) -8. The buff is then applied to the ship's shield resonances through the registry, and it runs out at the module's buff duration (2535).
/// </summary>
public class CommandBurstValueTests
{
    private const long Owner = 140_000_001, Ship = 9_988_500_000_000, Station = 60_003_760;
    private const long BurstII = 9_988_500_000_001, BurstI = 9_988_500_000_002, Charge = 9_988_500_000_003;
    private const int Rifter = 587, ShieldBurstI = 42529, ShieldBurstII = 43555, ShieldHarmonizing = 42695;
    private const int MidSlot = 19;
    private const int BuffValue = 2469, BuffId = 2468, BuffDuration = 2535;
    private const int ShieldResistance = 10;
    private static readonly int[] Shield = [271, 272, 273, 274];

    private static async Task<DogmaStatic> StaticsAsync()
    {
        var sde = await PgFactAttribute.SdeAsync();
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([Rifter, ShieldBurstI, ShieldBurstII, ShieldHarmonizing]);
        return statics;
    }

    private static DogmaWorld World(DogmaStatic statics, int burstType, long burstId)
    {
        var world = new DogmaWorld(statics, 0, Ship);
        world.Add(new DogmaItem(Ship, Rifter, Owner, Station, 0, ItemState.Online));
        world.Add(new DogmaItem(burstId, burstType, Owner, Ship, MidSlot, ItemState.Online));
        world.Add(new DogmaItem(Charge, ShieldHarmonizing, Owner, Ship, MidSlot, ItemState.Offline));
        return world;
    }

    [PgFact]
    public async Task A_tech_two_shield_burst_with_shield_harmonizing_has_the_sde_value_minus_ten()
    {
        var statics = await StaticsAsync();
        var world = World(statics, ShieldBurstII, BurstII);

        Assert.Equal(-10d, world.Get(BurstII, BuffValue));
        Assert.Equal(10d, world.Get(BurstII, BuffId));
    }

    [PgFact]
    public async Task A_tech_one_shield_burst_with_shield_harmonizing_has_the_sde_value_minus_eight()
    {
        var statics = await StaticsAsync();
        var world = World(statics, ShieldBurstI, BurstI);

        Assert.Equal(-8d, world.Get(BurstI, BuffValue));
    }

    [PgFact]
    public async Task The_shield_resistance_buff_scales_the_ships_resonances_by_the_burst_value_and_runs_out_after_its_duration()
    {
        var statics = await StaticsAsync();
        var world = World(statics, ShieldBurstII, BurstII);
        var collection = statics.BuffCollections[ShieldResistance];
        Assert.True(collection.Weakest);

        var registry = new BuffRegistry();
        var duration = world.Get(BurstII, BuffDuration);
        Assert.Equal(60_000d, duration);
        var before = Shield.Select(attribute => world.Get(Ship, attribute)).ToArray();

        registry.Apply(Ship, collection, world.Get(BurstII, BuffValue), nowMs: 0, durationMs: duration);
        registry.Sync(world, Ship, 1_000);
        foreach (var (attribute, value) in Shield.Zip(before))
        {
            Assert.Equal(value * 0.9, world.Get(Ship, attribute), 9);
        }

        registry.Sync(world, Ship, 60_001);
        foreach (var (attribute, value) in Shield.Zip(before))
        {
            Assert.Equal(value, world.Get(Ship, attribute), 9);
        }
    }
}
