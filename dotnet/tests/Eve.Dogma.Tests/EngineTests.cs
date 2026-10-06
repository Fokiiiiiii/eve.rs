namespace Eve.Dogma.Tests;

/// <summary>The engine against a small hand-made world: the rules of the client's dogma, one at a time.</summary>
public class EngineTests
{
    // attributes: 1 plain, 8 not stackable (penalised), 3 a cap, 4 capped by 3, 7 a whole number, 9 hit points, 15 power, 280 skill level, 327 a skill's bonus
    private static readonly AttributeDef[] Attributes =
    [
        new(1, "plain", 100, true, true, 4),
        new(8, "penalised", 100, false, true, 4),
        new(3, "cap", 150, true, true, 4),
        new(4, "capped", 100, true, true, 4, MaxAttributeId: 3),
        new(5, "count", 0, true, true, 5),
        new(9, "hp", 0, true, true, 4),
        new(15, "power", 0, true, true, 4),
        new(280, "skillLevel", 0, true, true, 4),
        new(327, "bonus", 0, true, true, 4),
        new(6, "bonusOf", 1, true, true, 4),
        new(7, "wholeNumber", 0, true, true, 11),
    ];

    private const int Ship = 100, Module = 101, Skill = 102, Char = 103, Implant = 104;

    private static Modifier Mod(ModifierFunc func, string domain, Operation op, int modified, int modifying, int group = 0, int skill = 0) =>
        new(func, domain, op, modified, modifying, group, skill);

    /// <summary>A world with a ship, and modules of a type that carries the given effect, each contributing attribute 6.</summary>
    private static DogmaWorld Fit(Operation op, int modified, int moduleCount, ItemState state = ItemState.Online, int effectCategory = EffectDef.Passive, double bonus = 20, string domain = "shipID")
    {
        var effect = new EffectDef(1, "fit", effectCategory, [Mod(ModifierFunc.Item, domain, op, modified, 6)]);
        var statics = new DogmaStatic(
            Attributes,
            [effect],
            [
                new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [1] = 100, [8] = 100, [9] = 200, [3] = 150, [4] = 100 }, []),
                new TypeDef(Module, 40, 7, new Dictionary<int, double> { [6] = bonus }, [1]),
            ]);
        var world = new DogmaWorld(statics, shipId: 1000);
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        for (var i = 0; i < moduleCount; i++)
        {
            world.Add(new DogmaItem(2000 + i, Module, 1, 1000, 19 + i, state));
        }
        return world;
    }

    [Fact]
    public void An_attribute_without_modifiers_is_its_base_value()
    {
        var world = Fit(Operation.ModAdd, 1, 0);
        Assert.Equal(100, world.Get(1000, 1));
    }

    [Fact]
    public void An_attribute_the_type_does_not_have_takes_the_attribute_default()
    {
        var world = Fit(Operation.ModAdd, 1, 0);
        Assert.Equal(1, world.Get(1000, 6)); // default 1 in the table above
        Assert.Equal(0, world.Get(1000, 5));
    }

    [Theory]
    [InlineData(Operation.PreAssign, 20)]
    [InlineData(Operation.PreMul, 2000)]
    [InlineData(Operation.PreDiv, 5)]
    [InlineData(Operation.ModAdd, 120)]
    [InlineData(Operation.ModSub, 80)]
    [InlineData(Operation.PostMul, 2000)]
    [InlineData(Operation.PostDiv, 5)]
    [InlineData(Operation.PostPercent, 120)]
    [InlineData(Operation.PostPercentUnpenalised, 120)]
    [InlineData(Operation.PostAssign, 20)]
    public void Each_operation_does_what_its_name_says(Operation op, double expected)
    {
        var world = Fit(op, 1, 1); // attribute 1 is stackable, so no penalty
        Assert.Equal(expected, world.Get(1000, 1), 9);
    }

    [Fact]
    public void The_special_skill_relationship_operation_changes_nothing()
    {
        Assert.Equal(100, Fit(Operation.SkillRelationship, 1, 1).Get(1000, 1));
    }

    [Fact]
    public void Operations_apply_in_the_order_of_the_game_not_the_order_they_were_found()
    {
        // (100 + 20) * 120% first multiplies, then adds, then the percentage: assign < mul < add < post-mul < percent < post-assign
        var effects = new[]
        {
            new EffectDef(1, "a", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 1, 6)]),
            new EffectDef(2, "b", 0, [Mod(ModifierFunc.Item, "shipID", Operation.ModAdd, 1, 6)]),
            new EffectDef(3, "c", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PreMul, 1, 6)]),
        };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [1] = 100 }, []),
            new TypeDef(Module, 40, 7, new Dictionary<int, double> { [6] = 10 }, [1, 2, 3]),
        ]);
        var world = new DogmaWorld(statics, shipId: 1000);
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        world.Add(new DogmaItem(2000, Module, 1, 1000, 19, ItemState.Online));
        // PreMul 100*10 = 1000, ModAdd +10 = 1010, PostPercent *1.10 = 1111
        Assert.Equal(1111, world.Get(1000, 1), 9);
    }

    [Fact]
    public void A_final_assignment_wins_over_everything_before_it()
    {
        var effects = new[]
        {
            new EffectDef(1, "a", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostAssign, 1, 6)]),
            new EffectDef(2, "b", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 1, 6)]),
        };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [1] = 100 }, []),
            new TypeDef(Module, 40, 7, new Dictionary<int, double> { [6] = 7 }, [1, 2]),
        ]);
        var world = new DogmaWorld(statics, shipId: 1000);
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        world.Add(new DogmaItem(2000, Module, 1, 1000, 19, ItemState.Online));
        Assert.Equal(7, world.Get(1000, 1), 9);
    }

    [Fact]
    public void A_stackable_attribute_takes_every_modifier_in_full()
    {
        var world = Fit(Operation.PostPercent, 1, 3);
        Assert.Equal(100 * 1.2 * 1.2 * 1.2, world.Get(1000, 1), 9);
    }

    [Fact]
    public void A_penalised_attribute_weakens_each_further_modifier()
    {
        // three +20% on a non-stackable attribute: 1.2, then 1 + 0.2/exp((1/2.67)^2), then 1 + 0.2/exp((2/2.67)^2)
        var world = Fit(Operation.PostPercent, 8, 3);
        var expected = 100 * 1.2 * (1 + 0.2 / Math.Exp(Math.Pow(1 / 2.67, 2))) * (1 + 0.2 / Math.Exp(Math.Pow(2 / 2.67, 2)));
        Assert.Equal(expected, world.Get(1000, 8), 9);
        Assert.True(world.Get(1000, 8) < 100 * 1.2 * 1.2 * 1.2);
    }

    [Fact]
    public void The_penalty_table_is_the_known_one()
    {
        // the well-known series: 100%, 86.91%, 57.06%, 28.30%, 10.60%, 3.00%, 0.65%, 0.11% of the bonus
        var expected = new[] { 1.0, 0.8691, 0.5706, 0.2830, 0.1060, 0.0300, 0.0065, 0.0011 };
        for (var i = 0; i < expected.Length; i++)
        {
            var factor = PenaltyOf(i);
            Assert.Equal(expected[i], factor, 3);
        }

        static double PenaltyOf(int index) => 1d / Math.Exp(Math.Pow(index / 2.67, 2.0));
    }

    [Fact]
    public void Penalised_modifiers_are_ordered_strongest_first_whatever_order_they_arrive_in()
    {
        var a = DogmaWorld.PenalisedFactor([0.8, 0.9, 0.5]);
        var b = DogmaWorld.PenalisedFactor([0.5, 0.8, 0.9]);
        Assert.Equal(a, b, 12);
        var d1 = Math.Exp(Math.Pow(1 / 2.67, 2));
        var d2 = Math.Exp(Math.Pow(2 / 2.67, 2));
        Assert.Equal(0.5 * ((0.8 - 1) / d1 + 1) * ((0.9 - 1) / d2 + 1), a, 12);
    }

    [Fact]
    public void Modifiers_that_weaken_and_strengthen_are_penalised_on_their_own_rows()
    {
        var factor = DogmaWorld.PenalisedFactor([0.5, 1.5]);
        Assert.Equal(0.5 * 1.5, factor, 12); // each is first of its kind: no penalty
        var second = DogmaWorld.PenalisedFactor([1.5, 1.4]);
        Assert.Equal(1.5 * ((1.4 - 1) / Math.Exp(Math.Pow(1 / 2.67, 2)) + 1), second, 12);
    }

    [Fact]
    public void Only_eight_modifiers_of_a_kind_count()
    {
        var eight = DogmaWorld.PenalisedFactor([.. Enumerable.Repeat(0.9, 8)]);
        var nine = DogmaWorld.PenalisedFactor([.. Enumerable.Repeat(0.9, 9)]);
        Assert.Equal(eight, nine, 12);
    }

    [Fact]
    public void Adding_and_subtracting_are_never_penalised()
    {
        var world = Fit(Operation.ModAdd, 8, 3); // attribute 8 is not stackable
        Assert.Equal(160, world.Get(1000, 8), 9);
    }

    [Fact]
    public void The_unpenalised_percentage_is_never_penalised()
    {
        var world = Fit(Operation.PostPercentUnpenalised, 8, 3);
        Assert.Equal(100 * 1.2 * 1.2 * 1.2, world.Get(1000, 8), 9);
    }

    [Fact]
    public void Skills_implants_ships_and_charges_are_not_penalised_but_modules_are()
    {
        // an implant (category 20) with a percentage on a non-stackable attribute of the ship, three times over
        var effect = new EffectDef(1, "e", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 8, 6)]);
        var statics = new DogmaStatic(Attributes, [effect],
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [8] = 100 }, []),
            new TypeDef(Implant, 300, 20, new Dictionary<int, double> { [6] = 10 }, [1]),
            new TypeDef(Module, 40, 7, new Dictionary<int, double> { [6] = 10 }, [1]),
        ]);
        var world = new DogmaWorld(statics, characterId: 1, shipId: 1000);
        world.Add(new DogmaItem(1, Char, 1, 1000));
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        for (var i = 0; i < 3; i++)
        {
            world.Add(new DogmaItem(3000 + i, Implant, 1, 1, 89 + i));
        }
        Assert.Equal(100 * 1.1 * 1.1 * 1.1, world.Get(1000, 8), 9);
        world = new DogmaWorld(statics, characterId: 1, shipId: 1000);
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        for (var i = 0; i < 3; i++)
        {
            world.Add(new DogmaItem(2000 + i, Module, 1, 1000, 19 + i));
        }
        Assert.True(world.Get(1000, 8) < 100 * 1.1 * 1.1 * 1.1);
    }

    [Fact]
    public void An_offline_module_does_not_apply_an_online_effect_but_a_passive_one_always_applies()
    {
        Assert.Equal(100, Fit(Operation.ModAdd, 1, 1, ItemState.Offline, EffectDef.Online).Get(1000, 1));
        Assert.Equal(120, Fit(Operation.ModAdd, 1, 1, ItemState.Online, EffectDef.Online).Get(1000, 1));
        Assert.Equal(120, Fit(Operation.ModAdd, 1, 1, ItemState.Offline, EffectDef.Passive).Get(1000, 1));
    }

    [Fact]
    public void An_activation_effect_needs_the_module_to_be_active()
    {
        Assert.Equal(100, Fit(Operation.ModAdd, 1, 1, ItemState.Online, EffectDef.Activation).Get(1000, 1));
        Assert.Equal(120, Fit(Operation.ModAdd, 1, 1, ItemState.Active, EffectDef.Activation).Get(1000, 1));
    }

    [Fact]
    public void Target_and_area_effects_do_not_apply_in_a_station()
    {
        Assert.Equal(100, Fit(Operation.ModAdd, 1, 1, ItemState.Active, EffectDef.Target).Get(1000, 1));
        Assert.Equal(100, Fit(Operation.ModAdd, 1, 1, ItemState.Active, EffectDef.Area).Get(1000, 1));
    }

    [Fact]
    public void Switching_a_module_changes_what_is_read_next()
    {
        var world = Fit(Operation.ModAdd, 1, 1, ItemState.Offline, EffectDef.Online);
        Assert.Equal(100, world.Get(1000, 1));
        world.SetState(2000, ItemState.Online);
        Assert.Equal(120, world.Get(1000, 1));
        world.Remove(2000);
        Assert.Equal(100, world.Get(1000, 1));
    }

    [Fact]
    public void A_cap_limits_the_base_and_the_result()
    {
        var world = Fit(Operation.PostPercent, 4, 1, bonus: 100); // 100 * 2 = 200, cap 150
        Assert.Equal(150, world.Get(1000, 4));
    }

    [Fact]
    public void A_base_above_its_cap_is_cut_before_the_modifiers_apply()
    {
        // base 100 of attribute 4 is under its cap; give the ship a base of 200 through an own value: cut to 150, then -50% = 75 (not 100)
        var world = Fit(Operation.PostPercent, 4, 1, bonus: -50);
        world.SetValue(1000, 4, 200);
        Assert.Equal(75, world.Get(1000, 4));
    }

    [Fact]
    public void A_pilots_skill_reaches_the_ship_even_when_the_pilot_is_not_in_it()
    {
        // a character in a station, its skill and the ship it is going to fly: the skill acts on the active ship
        var effects = new[] { new EffectDef(392, "shipHp", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 9, 327)]) };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [9] = 200 }, []),
            new TypeDef(Skill, 255, 16, new Dictionary<int, double> { [327] = 10 }, [392]),
            new TypeDef(Char, 1, 1, new Dictionary<int, double>(), []),
        ]);
        var world = new DogmaWorld(statics, characterId: 1, shipId: 1000);
        world.Add(new DogmaItem(1, Char, 1, 60_000_001)); // in the station, not in the ship
        world.Add(new DogmaItem(1000, Ship, 1, 60_000_001));
        world.Add(new DogmaItem(50, Skill, 1, 1));
        Assert.Equal(220, world.Get(1000, 9), 9);
    }

    [Fact]
    public void Whether_an_item_is_online_is_its_state_and_an_own_value_overrides_it()
    {
        var world = Fit(Operation.ModAdd, 1, 1, ItemState.Online);
        Assert.Equal(1, world.Get(2000, BuiltInEffects.IsOnline));
        world.SetState(2000, ItemState.Offline);
        Assert.Equal(0, world.Get(2000, BuiltInEffects.IsOnline));
        world.SetState(2000, ItemState.Active);
        Assert.Equal(1, world.Get(2000, BuiltInEffects.IsOnline));
        world.SetValue(2000, BuiltInEffects.IsOnline, 0);
        Assert.Equal(0, world.Get(2000, BuiltInEffects.IsOnline));
        Assert.Equal(0, world.Get(1000, BuiltInEffects.IsOnline)); // a ship is not online
    }

    [Fact]
    public void A_skills_level_is_given_and_no_modifier_changes_it()
    {
        var effect = new EffectDef(1, "e", 0, [Mod(ModifierFunc.Item, "itemID", Operation.ModAdd, 280, 327)]);
        var statics = new DogmaStatic(Attributes, [effect], [new TypeDef(Skill, 255, 16, new Dictionary<int, double> { [327] = 5 }, [1])]);
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(50, Skill, 1, 1));
        world.SetValue(50, 280, 3);
        Assert.Equal(3, world.Get(50, 280));
    }

    [Fact]
    public void A_skill_bonus_is_its_level_times_the_bonus_and_reaches_the_ship_the_character_flies()
    {
        // the shape of the game's skills: bonus (327) = 5 * level by a PreMul on itself; a PostPercent on the ship's attribute 9 by the bonus
        var effects = new[]
        {
            new EffectDef(251, "levelBonus", 0, [Mod(ModifierFunc.Item, "itemID", Operation.PreMul, 327, 280)]),
            new EffectDef(392, "shipHp", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 9, 327)]),
        };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [9] = 175 }, []),
            new TypeDef(Skill, 255, 16, new Dictionary<int, double> { [327] = 5 }, [251, 392]),
            new TypeDef(Char, 1, 1, new Dictionary<int, double>(), []),
        ]);
        var world = new DogmaWorld(statics, characterId: 1, shipId: 1000);
        world.Add(new DogmaItem(1, Char, 1, 1000));
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        world.Add(new DogmaItem(50, Skill, 1, 1));
        world.SetValue(50, 280, 3);
        Assert.Equal(15, world.Get(50, 327)); // 5 * level 3
        Assert.Equal(175 * 1.15, world.Get(1000, 9), 9);

        world.SetValue(50, 280, 5);
        Assert.Equal(175 * 1.25, world.Get(1000, 9), 9);
    }

    [Fact]
    public void A_module_lying_in_a_hangar_does_not_act_on_the_ship_the_character_flies()
    {
        var world = Fit(Operation.ModAdd, 1, 1);      // a module fitted to the ship: +20
        world.Add(new DogmaItem(3000, Module, 1, 60_000_001, 4, ItemState.Online)); // another, owned by the same character, in the hangar
        Assert.Equal(120, world.Get(1000, 1));
    }

    [Fact]
    public void A_skill_with_no_ship_in_the_world_reaches_nothing()
    {
        var effects = new[] { new EffectDef(392, "shipHp", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 9, 327)]) };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [9] = 175 }, []),
            new TypeDef(Skill, 255, 16, new Dictionary<int, double> { [327] = 5 }, [392]),
        ]);
        var world = new DogmaWorld(statics, characterId: 1);
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        world.Add(new DogmaItem(50, Skill, 1, 1));
        Assert.Equal(175, world.Get(1000, 9));
    }

    [Fact]
    public void A_required_skill_modifier_reaches_only_the_items_that_need_the_skill_and_belong_to_the_character()
    {
        const int Gunnery = 3300;
        var effects = new[]
        {
            new EffectDef(1, "gunnery", 0, [Mod(ModifierFunc.OwnerRequiredSkill, "charID", Operation.PostPercent, 1, 327, skill: Gunnery)]),
        };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Skill, 255, 16, new Dictionary<int, double> { [327] = 5 }, [1]),
            new TypeDef(Char, 1, 1, new Dictionary<int, double>(), []),
            new TypeDef(Module, 53, 7, new Dictionary<int, double> { [1] = 100, [182] = Gunnery }, []),
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [1] = 100 }, []),
        ]);
        var world = new DogmaWorld(statics, characterId: 1, shipId: 1000);
        world.Add(new DogmaItem(1, Char, 1, 1000));
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        world.Add(new DogmaItem(2000, Module, 1, 1000, 27)); // needs gunnery, the character's
        world.Add(new DogmaItem(2001, Module, 2, 1000, 28)); // needs gunnery, someone else's
        world.Add(new DogmaItem(50, Skill, 1, 1));
        Assert.Equal(105, world.Get(2000, 1), 9);
        Assert.Equal(100, world.Get(2001, 1));
        Assert.Equal(100, world.Get(1000, 1)); // the ship needs no gunnery
    }

    [Fact]
    public void A_location_group_modifier_reaches_the_items_of_a_group_located_in_the_domain_item()
    {
        var effects = new[]
        {
            new EffectDef(1, "e", 0, [Mod(ModifierFunc.LocationGroup, "shipID", Operation.ModAdd, 1, 6, group: 40)]),
        };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [1] = 100, [6] = 3 }, [1]),
            new TypeDef(Module, 40, 7, new Dictionary<int, double> { [1] = 10 }, []),
            new TypeDef(110, 41, 7, new Dictionary<int, double> { [1] = 10 }, []),
        ]);
        var world = new DogmaWorld(statics, shipId: 1000);
        world.Add(new DogmaItem(1000, Ship, 1, 5));
        world.Add(new DogmaItem(2000, Module, 1, 1000, 19));
        world.Add(new DogmaItem(2001, 110, 1, 1000, 20));
        Assert.Equal(13, world.Get(2000, 1));
        Assert.Equal(10, world.Get(2001, 1));
        Assert.Equal(100, world.Get(1000, 1)); // the ship itself is not located in itself
    }

    [Fact]
    public void Two_attributes_that_depend_on_each_other_do_not_loop()
    {
        var effects = new[]
        {
            new EffectDef(1, "a", 0, [Mod(ModifierFunc.Item, "itemID", Operation.ModAdd, 1, 8)]),
            new EffectDef(2, "b", 0, [Mod(ModifierFunc.Item, "itemID", Operation.ModAdd, 8, 1)]),
        };
        var statics = new DogmaStatic(Attributes, effects, [new TypeDef(Module, 40, 7, new Dictionary<int, double> { [1] = 1, [8] = 2 }, [1, 2])]);
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(5, Module, 1, 1, 0, ItemState.Online));
        var value = world.Get(5, 1);
        Assert.True(double.IsFinite(value));
    }

    [Fact]
    public void Power_and_processing_values_are_kept_to_two_decimals_and_whole_number_kinds_are_cut()
    {
        var effects = new[] { new EffectDef(1, "e", 0, [Mod(ModifierFunc.Item, "itemID", Operation.PostPercent, 15, 1)]) };
        var statics = new DogmaStatic(Attributes, effects, [new TypeDef(Ship, 25, 6, new Dictionary<int, double> { [15] = 10, [1] = 3.333, [7] = 7.9 }, [1])]);
        var world = new DogmaWorld(statics);
        world.Add(new DogmaItem(1, Ship, 1, 5));
        Assert.Equal(10.33, world.Get(1, 15), 9); // 10 * 1.03333 = 10.3333 -> 10.33
        Assert.Equal(7, world.Get(1, 7));         // an attribute of a whole-number kind is cut, not rounded
    }

    [Fact]
    public void An_active_booster_runs_only_server_selected_side_effects()
    {
        const int positive = 810, sideEffect = 811, unselectedSideEffect = 812, booster = 105;
        var effects = new[]
        {
            new EffectDef(positive, "booster", EffectDef.Activation, [Mod(ModifierFunc.Item, "charID", Operation.ModAdd, 164, 400)]),
            new EffectDef(sideEffect, "boosterSideEffectA", EffectDef.Activation, [Mod(ModifierFunc.Item, "charID", Operation.ModAdd, 164, 401)]),
            new EffectDef(unselectedSideEffect, "boosterSideEffectB", EffectDef.Activation, [Mod(ModifierFunc.Item, "charID", Operation.ModAdd, 165, 402)]),
        };
        var statics = new DogmaStatic(Attributes, effects,
        [
            new TypeDef(booster, 303, 20, new Dictionary<int, double> { [400] = 3, [401] = -2, [402] = -5 }, [positive, sideEffect, unselectedSideEffect]),
        ]);
        var world = new DogmaWorld(statics, Char);
        world.Add(new DogmaItem(Char, 0, Char, 0));
        world.SetValue(Char, 164, 20);
        world.SetValue(Char, 165, 10);
        world.Add(new DogmaItem(9000, booster, Char, Char, 88, ItemState.Active, new HashSet<int> { positive, sideEffect }));

        Assert.Equal(21, world.Get(Char, 164), 9);
        Assert.Equal(10, world.Get(Char, 165), 9);
    }
}
