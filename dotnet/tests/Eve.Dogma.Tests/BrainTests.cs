namespace Eve.Dogma.Tests;

/// <summary>The brain of a character, worked out from hand-made skills.</summary>
public class BrainTests
{
    private const int Char = 1, Ship = 2, SkillA = 3300, SkillB = 3301, IndustrySkill = 3380, ScienceSkill = 3402, CharType = 1373, ShipType = 601;

    private static readonly AttributeDef[] Attributes =
    [
        new(280, "skillLevel", 0, true, true, 4), new(327, "bonus", 0, true, true, 4), new(292, "damageMultiplierBonus", 0, true, true, 4),
        new(293, "rofBonus", 0, true, true, 4), new(9, "hp", 0, true, true, 4), new(51, "speed", 0, true, true, 4), new(64, "damageMultiplier", 1, true, true, 4),
        new(114, "emDamage", 0, true, true, 4), new(219, "manufactureTime", 1, true, true, 4), new(387, "copyTime", 1, true, true, 4), new(70, "agility", 0, true, true, 4),
        new(80, "other", 0, true, true, 4),
    ];

    private static Modifier Mod(ModifierFunc func, string domain, Operation op, int modified, int modifying, int group = 0, int skill = 0) =>
        new(func, domain, op, modified, modifying, group, skill);

    private static (DogmaWorld World, DogmaStatic Statics) Build(EffectDef[] effects, (int Type, int Level, Dictionary<int, double> Attributes, int[] Effects)[] skills, long owner = Char)
    {
        var types = new List<TypeDef>
        {
            new(CharType, 1, 1, new Dictionary<int, double>(), []),
            new(ShipType, 25, 6, new Dictionary<int, double> { [9] = 100 }, []),
        };
        foreach (var (type, _, attributes, effectIds) in skills)
        {
            types.Add(new TypeDef(type, 255, 16, attributes, effectIds));
        }
        var statics = new DogmaStatic(Attributes, BuiltInEffects.Merge(effects), types);
        var world = new DogmaWorld(statics, Char, Ship);
        world.Add(new DogmaItem(Char, CharType, Char, Ship, 57));
        world.Add(new DogmaItem(Ship, ShipType, Char, 60_000_001, 4));
        var next = 100L;
        foreach (var (type, level, _, _) in skills)
        {
            var id = next++;
            world.Add(new DogmaItem(id, type, owner, Char, 7));
            world.SetValue(id, DogmaWorld.SkillLevel, level);
        }
        return (world, statics);
    }

    private static string Show(BrainEffect e) => $"{e.SkillTypeId} {e.ModifierType} {e.TargetAttributeId} op{e.Operation} [{string.Join(',', e.Extras)}] {e.Value}";

    [Fact]
    public void A_skill_bonus_on_the_ship_is_worked_out_for_the_level_trained()
    {
        // bonus (327) = 5 per level; the effect acts on the ship's hit points
        var effects = new[]
        {
            new EffectDef(251, "levelBonus", 0, [Mod(ModifierFunc.Item, "itemID", Operation.PreMul, 327, 280)]),
            new EffectDef(392, "shipHp", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 9, 327)]),
        };
        var (world, statics) = Build(effects, [(SkillA, 3, new Dictionary<int, double> { [327] = 5 }, [251, 392])]);
        var brain = BrainBuilder.Build(world, statics);
        Assert.Equal([$"{SkillA} M 9 op6 [] 15"], brain.Ship.Select(Show));
        Assert.Empty(brain.Character);
        Assert.Empty(brain.Structure);
    }

    [Fact]
    public void A_skill_that_is_not_trained_or_belongs_to_someone_else_adds_nothing()
    {
        var effects = new[] { new EffectDef(392, "shipHp", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 9, 327)]) };
        var (untrained, s1) = Build(effects, [(SkillA, 0, new Dictionary<int, double> { [327] = 5 }, [392])]);
        Assert.Empty(BrainBuilder.Build(untrained, s1).Ship);
        var (foreign, s2) = Build(effects, [(SkillA, 3, new Dictionary<int, double> { [327] = 5 }, [392])], owner: 999);
        Assert.Empty(BrainBuilder.Build(foreign, s2).Ship);
    }

    [Fact]
    public void A_bonus_of_nothing_is_not_listed()
    {
        var effects = new[] { new EffectDef(392, "shipHp", 0, [Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 9, 327)]) };
        var (world, statics) = Build(effects, [(SkillA, 3, new Dictionary<int, double> { [327] = 0 }, [392])]);
        Assert.Empty(BrainBuilder.Build(world, statics).Ship);
    }

    [Fact]
    public void The_kinds_of_modifier_and_whose_things_they_reach_are_told_apart()
    {
        var effects = new[]
        {
            new EffectDef(1, "e", 0,
            [
                Mod(ModifierFunc.LocationGroup, "shipID", Operation.PostPercent, 9, 327, group: 55),
                Mod(ModifierFunc.LocationRequiredSkill, "shipID", Operation.PostPercent, 51, 327, skill: 77),
                Mod(ModifierFunc.OwnerRequiredSkill, "charID", Operation.PostPercent, 64, 327, skill: 88),
                Mod(ModifierFunc.Location, "structureID", Operation.ModAdd, 80, 327),
                Mod(ModifierFunc.Item, "targetID", Operation.ModAdd, 80, 327), // not a place the brain is kept
                Mod(ModifierFunc.LocationGroup, "shipID", Operation.PostPercent, 9, 327, group: 0), // a group that names nothing
            ]),
        };
        var (world, statics) = Build(effects, [(SkillA, 1, new Dictionary<int, double> { [327] = 4 }, [1])]);
        var brain = BrainBuilder.Build(world, statics);
        Assert.Equal([$"{SkillA} LG 9 op6 [55] 4", $"{SkillA} LRS 51 op6 [77] 4"], brain.Ship.Select(Show));
        Assert.Equal([$"{SkillA} ORS 64 op6 [88] 4"], brain.Character.Select(Show));
        Assert.Equal([$"{SkillA} L 80 op2 [] 4"], brain.Structure.Select(Show));
    }

    [Fact]
    public void Skill_bonuses_the_static_data_leaves_to_code_are_listed_for_the_skill_they_belong_to()
    {
        var (world, statics) = Build([],
        [
            (SkillA, 4, new Dictionary<int, double> { [293] = -2 }, [BuiltInEffects.SelfRof]),
            (SkillB, 2, new Dictionary<int, double> { [292] = 5 }, [BuiltInEffects.DroneDamageBonus, BuiltInEffects.MissileEmDamageBonus]),
        ]);
        var brain = BrainBuilder.Build(world, statics);
        Assert.Equal([$"{SkillA} LRS 51 op6 [{SkillA}] -2", $"{SkillB} LRS 114 op6 [{SkillB}] 5"], brain.Ship.Select(Show));
        Assert.Equal([$"{SkillB} ORS 64 op6 [{SkillB}] 5"], brain.Character.Select(Show));
    }

    [Fact]
    public void Effects_are_listed_by_skill_then_kind_then_attribute_and_then_what_they_reach_as_text()
    {
        var effects = new[]
        {
            new EffectDef(1, "e", 0,
            [
                Mod(ModifierFunc.LocationRequiredSkill, "shipID", Operation.PostPercent, 9, 327, skill: 3300),
                Mod(ModifierFunc.Item, "shipID", Operation.PostPercent, 51, 327),
                Mod(ModifierFunc.LocationGroup, "shipID", Operation.PostPercent, 9, 327, group: 862),
                Mod(ModifierFunc.LocationRequiredSkill, "shipID", Operation.PostPercent, 9, 327, skill: 862),
                Mod(ModifierFunc.LocationRequiredSkill, "shipID", Operation.PostPercent, 9, 327, skill: 55033),
                Mod(ModifierFunc.LocationRequiredSkill, "shipID", Operation.PostPercent, 9, 327, skill: 3319),
            ]),
        };
        var (world, statics) = Build(effects, [(SkillB, 1, new Dictionary<int, double> { [327] = 1 }, [1]), (SkillA, 1, new Dictionary<int, double> { [327] = 2 }, [1])]);
        var brain = BrainBuilder.Build(world, statics);
        Assert.Equal(
        [
            // SkillA (3300) comes before SkillB (3301); LG before LRS before M; extras as text: [3300] < [3319] < [55033] < [862]
            $"{SkillA} LG 9 op6 [862] 2", $"{SkillA} LRS 9 op6 [3300] 2", $"{SkillA} LRS 9 op6 [3319] 2", $"{SkillA} LRS 9 op6 [55033] 2", $"{SkillA} LRS 9 op6 [862] 2", $"{SkillA} M 51 op6 [] 2",
            $"{SkillB} LG 9 op6 [862] 1", $"{SkillB} LRS 9 op6 [3300] 1", $"{SkillB} LRS 9 op6 [3319] 1", $"{SkillB} LRS 9 op6 [55033] 1", $"{SkillB} LRS 9 op6 [862] 1", $"{SkillB} M 51 op6 [] 1",
        ], brain.Ship.Select(Show));
    }

    [Fact]
    public void The_industry_skills_have_their_own_list_after_the_others_in_the_order_of_a_fixed_list_of_skills()
    {
        var effects = new[]
        {
            new EffectDef(1, "industry", 0, [Mod(ModifierFunc.Item, "charID", Operation.PostPercent, 219, 327)]),
            new EffectDef(2, "science", 0, [Mod(ModifierFunc.Item, "charID", Operation.PostPercent, 387, 327)]),
            new EffectDef(3, "agility", 0, [Mod(ModifierFunc.Item, "charID", Operation.PostPercent, 70, 327)]),
            new EffectDef(4, "levelBonus", 0, [Mod(ModifierFunc.Item, "itemID", Operation.PreMul, 327, 280)]),
        };
        // trained in the opposite order: Science (3402) before Industry (3380), and a skill of another kind between them
        var (world, statics) = Build(effects,
        [
            (ScienceSkill, 4, new Dictionary<int, double> { [327] = -5 }, [4, 2]),
            (SkillA, 2, new Dictionary<int, double> { [327] = -2 }, [4, 3]),
            (IndustrySkill, 1, new Dictionary<int, double> { [327] = -4 }, [4, 1]),
        ]);
        var brain = BrainBuilder.Build(world, statics);
        Assert.Equal([$"{SkillA} M 70 op6 [] -4", $"{IndustrySkill} M 219 op6 [] -4", $"{ScienceSkill} M 387 op6 [] -20"], brain.Character.Select(Show));
    }
}
