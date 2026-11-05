using Eve.Dogma;

namespace Eve.Dogma.Tests;

/// <summary>A system's beacon as dogma modifiers: the multipliers and bonuses reach the attribute of their base, and the names with no base are listed.</summary>
public sealed class SystemEnvironmentTests
{
    private static int? Attribute(string name) => name switch
    {
        "maxTargetRange" => 1,
        "armorEmDamageResonance" => 2,
        "capacitorCapacity" => 3,
        _ => null,
    };

    [Fact]
    public void A_multiplier_is_a_post_multiplication_and_a_bonus_a_post_percentage_of_the_base_attribute()
    {
        var beacon = new Dictionary<string, double>
        {
            ["maxTargetRangeMultiplier"] = 2.0,
            ["armorEmDamageResistanceBonus"] = 50.0,
            ["capacitorCapacityMultiplier"] = 1.3,
        };

        var (effect, notApplied) = SystemEnvironment.EffectOf(30854, "Class 6 Black Hole Effects", beacon, Attribute);

        Assert.NotNull(effect);
        Assert.Equal(EffectDef.Passive, effect.Category);
        Assert.Equal("environment:Class 6 Black Hole Effects", effect.Name);
        Assert.Empty(notApplied);
        var item = effect.Modifiers.Where(m => m.Func == ModifierFunc.Item).ToArray();
        Assert.Equal(
            // a resistance bonus of 50 percent is a resonance 50 percent smaller (the SDE names the resonance)
            new[] { (2, Operation.PostPercent, -50.0), (3, Operation.PostMul, 1.3), (1, Operation.PostMul, 2.0) },
            item.Select(m => (m.ModifiedAttributeId, m.Operation, m.Literal!.Value)).ToArray());
        // the ship and the items located in it (its modules) get the same modifiers
        Assert.Equal(item.Length, effect.Modifiers.Count(m => m.Func == ModifierFunc.Location));
    }

    [Fact]
    public void A_name_with_no_base_attribute_is_listed_and_a_beacon_with_none_has_no_effect()
    {
        var beacon = new Dictionary<string, double> { ["shieldBonusMultiplier"] = 0.85, ["rechargeRateMultiplier"] = 1.15 };

        var (effect, notApplied) = SystemEnvironment.EffectOf(30846, "Class 1 Cataclysmic Variable Effects", beacon, Attribute);

        Assert.Null(effect);
        Assert.Equal(new[] { "rechargeRateMultiplier", "shieldBonusMultiplier" }, notApplied);
    }

    [Fact]
    public void The_environment_of_a_system_changes_the_ship_and_the_modules_in_it_and_nothing_once_it_is_taken_away()
    {
        const long Owner = 1, Ship = 100, Module = 101;
        const int ShipType = 1, ModuleType = 2, Speed = 20, Rate = 21;
        var statics = new DogmaStatic(
            [],
            [],
            [
                new TypeDef(ShipType, 1, 6, new Dictionary<int, double> { [Speed] = 100 }, []),
                new TypeDef(ModuleType, 1, 7, new Dictionary<int, double> { [Rate] = 10 }, []),
            ]);
        var world = new DogmaWorld(statics, 0, Ship);
        world.Add(new DogmaItem(Ship, ShipType, Owner, 0, 0, ItemState.Online));
        world.Add(new DogmaItem(Module, ModuleType, Owner, Ship, 11, ItemState.Online));
        var beacon = new Dictionary<string, double> { ["maxTargetRangeMultiplier"] = 2.0, ["capacitorCapacityMultiplier"] = 0.5 };
        var attributes = new Dictionary<string, int> { ["maxTargetRange"] = Speed, ["capacitorCapacity"] = Rate };
        var (effect, _) = SystemEnvironment.EffectOf(30854, "Class 6 Black Hole Effects", beacon, name => attributes.TryGetValue(name, out var id) ? id : null);
        Assert.NotNull(effect);

        world.SetBuffs(Ship, [effect]);
        Assert.Equal(200, world.Get(Ship, Speed));
        Assert.Equal(5, world.Get(Module, Rate));

        world.SetBuffs(Ship, []);
        Assert.Equal(100, world.Get(Ship, Speed));
        Assert.Equal(10, world.Get(Module, Rate));
    }
}
