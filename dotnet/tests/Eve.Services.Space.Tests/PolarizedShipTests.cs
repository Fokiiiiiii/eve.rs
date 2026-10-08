using Eve.Data;
using Eve.Dogma;
using Eve.Sde;
using Eve.Ships;
using Eve.Space;
using Npgsql;

namespace Eve.Services.Space.Tests;

/// <summary>
/// A fitted Polarized weapon pins the ship's resonances, and nothing that runs on the ship lifts the pin: not the numbers a hardener is told, not the
/// numbers the simulation is sent. The ship, the weapon and the hardeners are the real SDE's; the fitting is set up here, not by the fitting rules.
/// </summary>
public class PolarizedShipTests
{
    private const long CharacterId = 70001, ShipId = 70002, Station = 60003760;
    private const int Rifter = 587, Polarized = 34274, EmShieldHardener = 2293, AdaptiveArmorHardener = 4403;
    private const int MidSlot = 19, HighSlot = 27;
    private const string EmShieldEffect = "modifyActiveShieldResonancePostPercent", AdaptiveArmorEffect = "adaptiveArmorHardener";
    private const int EmShield = 271, EmArmor = 267;
    private static readonly int[] ShieldResonances = [271, 274, 273, 272];
    private static readonly int[] ArmorResonances = [267, 270, 269, 268];

    /// <summary>A Rifter with the modules fitted (id, type, flag, state), as the snapshot of a pilot in space.</summary>
    private static async Task<(ShipSnapshot Snapshot, DogmaStatic Statics)> ShipAsync(params (long Id, int TypeId, int Flag, ItemState State)[] fitted)
    {
        var source = NpgsqlDataSource.Create(PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var statics = await DogmaStatic.LoadAsync(new SdeReader(source));
        await statics.EnsureTypesAsync([Rifter, .. fitted.Select(module => module.TypeId)]);
        var world = new DogmaWorld(statics, CharacterId, ShipId);
        world.Add(new DogmaItem(ShipId, Rifter, CharacterId, Station, 0, ItemState.Online));
        foreach (var (id, typeId, flag, state) in fitted)
        {
            world.Add(new DogmaItem(id, typeId, CharacterId, ShipId, flag, state));
        }
        var snapshot = new ShipSnapshot
        {
            Character = null!,
            Ship = new Eve.Inventory.Item(ShipId, Rifter, CharacterId, Station, 0, 1, true, null),
            Fitted = [.. fitted.Select(module => new Eve.Inventory.Item(module.Id, module.TypeId, CharacterId, ShipId, module.Flag, 1, true, null))],
            Charges = [],
            Skills = [],
            World = world,
        };
        return (snapshot, statics);
    }

    private static SimEffect PackageOf(ShipSnapshot snapshot, DogmaStatic statics, long moduleId, string effectName)
    {
        var result = ModulePackages.Of(snapshot, statics, moduleId, effectName, 0, 1);
        Assert.Null(result.Reason);
        return Assert.IsType<SimEffect>(result.Effect);
    }

    private static double Told(IEnumerable<SimChange> changes, int attribute) =>
        changes.Single(change => change.ItemId == ShipId && change.AttributeId == attribute).Value;

    [PgFact]
    public async Task The_simulation_is_sent_the_pinned_resonances_while_a_polarized_weapon_is_fitted_and_the_hardener_runs()
    {
        var (snapshot, _) = await ShipAsync((2, Polarized, HighSlot, ItemState.Online), (1, EmShieldHardener, MidSlot, ItemState.Active));

        var vitals = SimShips.VitalsOf(snapshot);

        Assert.All(vitals.Resonances.Shield, value => Assert.Equal(1.0, value));
        Assert.All(vitals.Resonances.Armor, value => Assert.Equal(1.0, value));
        Assert.All(vitals.Resonances.Structure, value => Assert.Equal(1.0, value));
    }

    [PgFact]
    public async Task Without_the_weapon_the_simulation_is_sent_the_hardener_lowered_resonance()
    {
        var (snapshot, statics) = await ShipAsync((1, EmShieldHardener, MidSlot, ItemState.Active));
        var bare = statics.Type(Rifter)!.Attributes[EmShield];

        Assert.Equal(bare * 0.5, SimShips.VitalsOf(snapshot).Resonances.Shield[0], 9);
    }

    [PgFact]
    public async Task A_hardener_is_told_the_pinned_resonance_while_a_polarized_weapon_is_fitted()
    {
        var (snapshot, statics) = await ShipAsync((2, Polarized, HighSlot, ItemState.Online), (1, EmShieldHardener, MidSlot, ItemState.Online));

        var package = PackageOf(snapshot, statics, 1, EmShieldEffect);

        // the four shield resonances stay at one, whichever way the hardener is told: started (on) and stopped (off)
        foreach (var attribute in ShieldResonances)
        {
            Assert.Equal(1.0, Told(package.OnChanges, attribute));
            Assert.Equal(1.0, Told(package.OffChanges, attribute));
        }
    }

    [PgFact]
    public async Task Without_the_weapon_a_hardener_is_told_the_lowered_resonance_and_the_bare_one_when_it_stops()
    {
        var (snapshot, statics) = await ShipAsync((1, EmShieldHardener, MidSlot, ItemState.Online));
        var bare = statics.Type(Rifter)!.Attributes[EmShield];

        var package = PackageOf(snapshot, statics, 1, EmShieldEffect);

        Assert.Equal(bare * 0.5, Told(package.OnChanges, EmShield), 9);
        Assert.Equal(bare, Told(package.OffChanges, EmShield), 9);
    }

    [PgFact]
    public async Task An_adaptive_armor_hardener_is_not_told_its_own_multiplier_over_a_pinned_armor_resonance()
    {
        // the adaptive hardener multiplies the armor resonances by its own values (the type's 0.85): told as a PostMul, not a write over the world
        var (pinned, statics) = await ShipAsync((2, Polarized, HighSlot, ItemState.Online), (1, AdaptiveArmorHardener, MidSlot, ItemState.Online));
        var (bare, _) = await ShipAsync((1, AdaptiveArmorHardener, MidSlot, ItemState.Online));

        var pinnedPackage = PackageOf(pinned, statics, 1, AdaptiveArmorEffect);
        Assert.All(ArmorResonances, attribute => Assert.Equal(1.0, Told(pinnedPackage.OnChanges, attribute)));

        var multiplier = statics.Type(AdaptiveArmorHardener)!.Attributes[EmArmor];
        var unpinnedPackage = PackageOf(bare, statics, 1, AdaptiveArmorEffect);
        Assert.Equal(Math.Round(statics.Type(Rifter)!.Attributes[EmArmor] * multiplier, 6), Told(unpinnedPackage.OnChanges, EmArmor), 6);
    }

    [PgFact]
    public async Task A_second_hardener_is_told_what_the_world_gives_once_it_runs_and_the_penalty_applies()
    {
        // what is told is what dogma then has, not a multiplication over the world's value before the other hardener
        var (one, statics) = await ShipAsync((1, EmShieldHardener, MidSlot, ItemState.Active), (2, EmShieldHardener, MidSlot + 1, ItemState.Online));
        var (two, _) = await ShipAsync((1, EmShieldHardener, MidSlot, ItemState.Active), (2, EmShieldHardener, MidSlot + 1, ItemState.Active));

        var told = Told(PackageOf(one, statics, 2, EmShieldEffect).OnChanges, EmShield);

        Assert.Equal(two.World.Get(ShipId, EmShield), told, 9);
        Assert.True(told < statics.Type(Rifter)!.Attributes[EmShield] * 0.5, "a second hardener of the same kind weakens less than the first");
    }
}
