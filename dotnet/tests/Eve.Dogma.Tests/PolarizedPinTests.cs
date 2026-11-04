namespace Eve.Dogma.Tests;

/// <summary>
/// A fitted Polarized weapon pins the ship's twelve resonances (shield, armor and hull, each em, thermal, kinetic and explosive) at one: the
/// effects 5994 and 5995 are PostAssign on the ship, and the attribute caps are one. The values are the pinned SDE's; the premises are read from it.
/// </summary>
public class PolarizedPinTests
{
    private const long Owner = 140_000_001, Ship = 9_988_500_000_000, Station = 60_003_760;
    private const int Rifter = 587, Polarized = 34274, EmShieldHardener = 2293, ThermalShieldHardener = 2295, KineticShieldHardener = 2291, DamageControl = 2046;
    private const int LowSlot = 11, MidSlot = 19, HighSlot = 27;
    private const int ResistanceKillerHullAll = 5994, ResistanceKillerShieldArmorAll = 5995;

    private static readonly int[] Shield = [271, 274, 273, 272];
    private static readonly int[] Armor = [267, 270, 269, 268];
    private static readonly int[] Hull = [113, 110, 109, 111];
    private static readonly int[] Twelve = [.. Shield, .. Armor, .. Hull];

    private static async Task<DogmaStatic> StaticsAsync()
    {
        var sde = await PgFactAttribute.SdeAsync();
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([Rifter, Polarized, EmShieldHardener, ThermalShieldHardener, KineticShieldHardener, DamageControl]);
        return statics;
    }

    private static DogmaItem Fitted(long id, int typeId, int flag, ItemState state = ItemState.Online) => new(id, typeId, Owner, Ship, flag, state);

    private static DogmaWorld World(DogmaStatic statics, params DogmaItem[] fitted)
    {
        var world = new DogmaWorld(statics, 0, Ship);
        world.Add(new DogmaItem(Ship, Rifter, Owner, Station, 0, ItemState.Online));
        foreach (var item in fitted)
        {
            world.Add(item);
        }
        return world;
    }

    private static double[] Resonances(DogmaWorld world) => [.. Twelve.Select(attribute => world.Get(Ship, attribute))];

    private static void AssertAll(double expected, DogmaWorld world)
    {
        foreach (var (attribute, value) in Twelve.Zip(Resonances(world)))
        {
            Assert.True(expected == value, $"resonance {attribute} is {value}, expected {expected}");
        }
    }

    [PgFact]
    public async Task The_static_data_caps_each_of_the_twelve_resonances_at_one_and_the_weapon_carries_the_pin()
    {
        var statics = await StaticsAsync();
        foreach (var attribute in Twelve)
        {
            var max = statics.Attribute(attribute)?.MaxAttributeId ?? 0;
            Assert.True(max > 0, $"resonance {attribute} has no cap in the static data");
            Assert.Equal(1.0, statics.Attribute(max)?.DefaultValue);
        }
        var effects = statics.Type(Polarized)!.EffectIds;
        Assert.Contains(ResistanceKillerHullAll, effects);
        Assert.Contains(ResistanceKillerShieldArmorAll, effects);
    }

    [PgFact]
    public async Task A_bare_ship_has_its_own_resonances_from_its_type()
    {
        var statics = await StaticsAsync();
        var type = statics.Type(Rifter)!;
        var world = World(statics);
        Assert.Equal(Twelve.Select(attribute => type.Attributes[attribute]), Resonances(world));
    }

    [PgFact]
    public async Task A_fitted_polarized_weapon_pins_all_twelve_resonances_at_one()
    {
        var statics = await StaticsAsync();
        var world = World(statics, Fitted(1, Polarized, HighSlot));

        AssertAll(1.0, world);
    }

    [PgFact]
    public async Task The_pin_holds_whether_the_weapon_is_offline_online_or_active()
    {
        // fitted is what counts (CCP: "when fitted all resistances are set to 0"); the state of the weapon does not release the pin
        var statics = await StaticsAsync();
        foreach (var state in new[] { ItemState.Offline, ItemState.Online, ItemState.Active })
        {
            AssertAll(1.0, World(statics, Fitted(1, Polarized, HighSlot, state)));
        }
    }

    [PgFact]
    public async Task Hardeners_and_damage_control_that_run_cannot_lower_a_pinned_resonance_in_either_item_order()
    {
        // R2 (no hardener, damage control or rig changes a pinned value) and R3 (the order the modules come online or active does not matter)
        var statics = await StaticsAsync();
        var hardeners = new[]
        {
            Fitted(10, EmShieldHardener, MidSlot, ItemState.Active),
            Fitted(11, ThermalShieldHardener, MidSlot + 1, ItemState.Active),
            Fitted(12, KineticShieldHardener, MidSlot + 2, ItemState.Active),
            Fitted(13, DamageControl, LowSlot, ItemState.Online),
        };
        var weaponFirst = World(statics, [Fitted(1, Polarized, HighSlot), .. hardeners]);
        var weaponLast = World(statics, [Fitted(99, Polarized, HighSlot), .. hardeners]);
        AssertAll(1.0, weaponFirst);
        AssertAll(1.0, weaponLast);
        Assert.Equal(Resonances(weaponFirst), Resonances(weaponLast));
    }

    [PgFact]
    public async Task Without_a_weapon_an_active_hardener_lowers_its_resonance_by_its_percentage()
    {
        // the contrast: the hardener itself works (EM shield 271 of the Rifter: its own value, halved by the hardener's -50)
        var statics = await StaticsAsync();
        var bare = statics.Type(Rifter)!.Attributes[271];
        var world = World(statics, Fitted(10, EmShieldHardener, MidSlot, ItemState.Active));

        Assert.Equal(bare * 0.5, world.Get(Ship, 271), 9);
        Assert.Equal(statics.Type(Rifter)!.Attributes[274], world.Get(Ship, 274), 9);
    }

    [PgFact]
    public async Task Removing_the_weapon_restores_the_ships_own_resonances()
    {
        var statics = await StaticsAsync();
        var bare = statics.Type(Rifter)!;
        var world = World(statics, Fitted(1, Polarized, HighSlot));
        AssertAll(1.0, world);

        Assert.True(world.Remove(1));

        Assert.Equal(Twelve.Select(attribute => bare.Attributes[attribute]), Resonances(world));
    }
}
