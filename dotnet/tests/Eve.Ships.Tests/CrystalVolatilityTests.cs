using Eve.Inventory;

namespace Eve.Ships.Tests;

/// <summary>A mining crystal's volatility per cycle (M9): a roll under the chance adds the crystal's damage, and a crystal at full damage is removed.</summary>
public class CrystalVolatilityTests
{
    private const int Veldspar = 1230;

    [PgFact]
    public async Task A_roll_over_the_chance_is_no_hit_and_hits_add_the_damage_until_the_crystal_breaks_and_is_removed()
    {
        await using var rig = await Rig.CreateAsync();
        var c = await rig.NewCharacterAsync();
        var crystal = await rig.GiveAsync(c.CharacterId, Veldspar, singleton: true, quantity: 1, location: c.ShipId, flag: ItemFlags.HiSlot0);

        var miss = await rig.Ships.ApplyCrystalVolatilityAsync(crystal, roll: 0.5, chance: 0.1, damage: 0.4);
        Assert.Equal(new CrystalVolatility(false, false, 0), miss);
        Assert.Null((await rig.Items.GetAsync(crystal))!.CustomInfo);

        var first = await rig.Ships.ApplyCrystalVolatilityAsync(crystal, roll: 0.05, chance: 0.1, damage: 0.4);
        Assert.Equal((true, false, 0.4), (first.Hit, first.Broke, first.Damage));
        Assert.Equal("0.4", (await rig.Items.GetAsync(crystal))!.CustomInfo);

        var second = await rig.Ships.ApplyCrystalVolatilityAsync(crystal, roll: 0.05, chance: 0.1, damage: 0.4);
        Assert.Equal((true, false, 0.8), (second.Hit, second.Broke, second.Damage));

        // the third hit takes the crystal past full damage: it breaks, and the item is gone
        var broke = await rig.Ships.ApplyCrystalVolatilityAsync(crystal, roll: 0.05, chance: 0.1, damage: 0.4);
        Assert.Equal((true, true, 1d), (broke.Hit, broke.Broke, broke.Damage));
        Assert.Null(await rig.Items.GetAsync(crystal));
    }
}
