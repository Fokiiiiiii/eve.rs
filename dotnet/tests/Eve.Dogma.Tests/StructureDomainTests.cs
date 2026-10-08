namespace Eve.Dogma.Tests;

/// <summary>
/// The <c>structureID</c> domain: what a structure's modules and the skills its pilot has act on the structure, the way the ship's domain acts on the ship.
/// The real cases are worked out from the pinned static data; the synthetic ones cover the functions the static data does not pair with this domain.
/// </summary>
public class StructureDomainTests
{
    private const long CharId = 140_000_001, Ship = 9_988_400_000_000, Hull = 1_000_000_000_001, Module = Hull + 1, Skill = CharId * 100_000 + 37798, CorpId = 98_000_001, SolarSystem = 30_000_142;
    private const int Astrahus = 35832, BallisticControl = 35959, VariableSpectrumEcm = 35940, StructureElectronicSystems = 37798, CharType = 1373, Ibis = 601;
    private const int HiddenMissileDamageMultiplier = 2750, CapacitorNeed = 6, CapNeedBonus = 317;

    private static async Task<(DogmaWorld World, DogmaStatic Statics)> StructureAsync(int moduleType, ItemState moduleState, int skillLevel = 0)
    {
        var sde = await PgFactAttribute.SdeAsync();
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([Astrahus, moduleType, StructureElectronicSystems, CharType]);

        var world = new DogmaWorld(statics, CharId);
        world.Add(new DogmaItem(CharId, CharType, CharId, SolarSystem, 57));
        world.Add(new DogmaItem(Hull, Astrahus, CorpId, SolarSystem));
        world.SetStructure(Hull);
        world.Add(new DogmaItem(Module, moduleType, CorpId, Hull, 0, moduleState));
        if (skillLevel > 0)
        {
            world.Add(new DogmaItem(Skill, StructureElectronicSystems, CharId, CharId, 7));
            world.SetValue(Skill, DogmaWorld.SkillLevel, skillLevel);
        }
        return (world, statics);
    }

    [PgFact]
    public async Task An_online_structure_module_changes_the_structure_it_is_fitted_to()
    {
        var (world, statics) = await StructureAsync(BallisticControl, ItemState.Online);
        // the module's effect multiplies the structure's hidden missile multiplier (1.0 on an Astrahus) by the module's own bonus (1.07)
        var expected = statics.Type(Astrahus)!.Attributes[HiddenMissileDamageMultiplier] * statics.Type(BallisticControl)!.Attributes[213];
        Assert.Equal(1.07, expected, 9);
        Assert.Equal(expected, world.Get(Hull, HiddenMissileDamageMultiplier), 9);
    }

    [PgFact]
    public async Task An_offline_structure_module_leaves_the_structure_as_it_is_until_it_is_online()
    {
        var (world, statics) = await StructureAsync(BallisticControl, ItemState.Offline);
        var unchanged = statics.Type(Astrahus)!.Attributes[HiddenMissileDamageMultiplier];
        Assert.Equal(1.0, unchanged, 9);
        Assert.Equal(unchanged, world.Get(Hull, HiddenMissileDamageMultiplier), 9);

        world.SetState(Module, ItemState.Online);
        Assert.Equal(1.07, world.Get(Hull, HiddenMissileDamageMultiplier), 9);

        world.SetState(Module, ItemState.Offline);
        Assert.Equal(unchanged, world.Get(Hull, HiddenMissileDamageMultiplier), 9);
    }

    [PgFact]
    public async Task A_skill_the_pilot_carries_acts_on_the_modules_fitted_to_the_structure()
    {
        var (world, statics) = await StructureAsync(VariableSpectrumEcm, ItemState.Online, skillLevel: 5);
        // the skill's capacitor need bonus is -3 per level, so -15 at level 5, and the module's capacitor need (3000) moves by that percentage: 3000 x 85%
        Assert.Equal(-15, world.Get(Skill, CapNeedBonus), 9);
        var expected = statics.Type(VariableSpectrumEcm)!.Attributes[CapacitorNeed] * (100 + world.Get(Skill, CapNeedBonus)) / 100;
        Assert.Equal(2550, expected, 9);
        Assert.Equal(expected, world.Get(Module, CapacitorNeed), 9);
    }

    [PgFact]
    public async Task A_ship_world_does_not_resolve_the_structure_domain()
    {
        var sde = await PgFactAttribute.SdeAsync();
        var statics = await DogmaStatic.LoadAsync(sde);
        await statics.EnsureTypesAsync([Ibis, Astrahus, BallisticControl, VariableSpectrumEcm, StructureElectronicSystems, CharType]);

        // a ship with the online structure module fitted in it: nothing is reached, so the ship keeps the default of the attribute (1.0)
        var world = new DogmaWorld(statics, CharId, Ship);
        world.Add(new DogmaItem(CharId, CharType, CharId, Ship, 57));
        world.Add(new DogmaItem(Ship, Ibis, CharId, SolarSystem, 4));
        world.Add(new DogmaItem(Module, BallisticControl, CorpId, Ship, 19, ItemState.Online));
        Assert.Equal(1.0, world.Get(Ship, HiddenMissileDamageMultiplier), 9);

        // a structure in the same world that is not the one the world names is not reached either
        world.Add(new DogmaItem(Hull, Astrahus, CorpId, SolarSystem));
        Assert.Equal(1.0, world.Get(Hull, HiddenMissileDamageMultiplier), 9);

        // the pilot's structure skill finds no structure to act on: a fitted module of its groups keeps its capacitor need
        world.Add(new DogmaItem(Skill, StructureElectronicSystems, CharId, CharId, 7));
        world.SetValue(Skill, DogmaWorld.SkillLevel, 5);
        var ecm = new DogmaItem(Module + 1, VariableSpectrumEcm, CorpId, Ship, 20, ItemState.Online);
        world.Add(ecm);
        Assert.Equal(statics.Type(VariableSpectrumEcm)!.Attributes[CapacitorNeed], world.Get(ecm.Id, CapacitorNeed), 9);
    }
}

/// <summary>The domain's rules with hand-made static data: the functions the SDE does not pair with <c>structureID</c>, and the cross-domain cases.</summary>
public class StructureDomainRulesTests
{
    private const long CharId = 500, Hull = 600, Ship = 700, SolarSystem = 800, ModInStructure = 601, ModInShip = 701, Skill = 900;
    private const int CharType = 1373, SkillType = 3300, StructureHullType = 40020, ShipHullType = 40010, FittedInStructure = 40001, FittedInShip = 40002;
    private const int SkillEffect = 900, ShipDomainEffect = 901, StructureDomainEffect = 902;

    private static DogmaStatic Statics() => new(
        [
            new AttributeDef(6, "capNeed", 0, true, true, 4),
            new AttributeDef(9, "hp", 0, true, true, 4),
            new AttributeDef(182, "requiredSkill1", 0, true, true, 4),
            new AttributeDef(280, "skillLevel", 0, true, true, 4),
            new AttributeDef(327, "bonus", 0, true, true, 4),
        ],
        [
            new EffectDef(SkillEffect, "skillStructureBonus", EffectDef.Passive,
            [
                new Modifier(ModifierFunc.LocationRequiredSkill, "structureID", Operation.PostPercent, 6, 327, SkillTypeId: SkillType),
                new Modifier(ModifierFunc.Item, "structureID", Operation.PostPercent, 9, 327),
            ]),
            new EffectDef(ShipDomainEffect, "moduleShipHp", EffectDef.Passive, [new Modifier(ModifierFunc.Item, "shipID", Operation.ModAdd, 9, 6)]),
            new EffectDef(StructureDomainEffect, "moduleStructureHp", EffectDef.Passive, [new Modifier(ModifierFunc.Item, "structureID", Operation.ModAdd, 9, 6)]),
        ],
        [
            new TypeDef(SkillType, 255, 16, new Dictionary<int, double>(), [SkillEffect]),
            new TypeDef(FittedInStructure, 1400, 66, new Dictionary<int, double> { [6] = 100, [182] = SkillType }, [ShipDomainEffect]),
            new TypeDef(FittedInShip, 1400, 66, new Dictionary<int, double> { [6] = 100 }, [StructureDomainEffect]),
            new TypeDef(ShipHullType, 25, 6, new Dictionary<int, double> { [9] = 50 }, []),
            new TypeDef(StructureHullType, 1657, 65, new Dictionary<int, double> { [9] = 500 }, []),
        ]);

    [Fact]
    public void A_skill_the_pilot_carries_acts_on_the_structure_and_on_what_needs_it_in_the_structure()
    {
        var world = new DogmaWorld(Statics(), CharId, structureId: Hull);
        world.Add(new DogmaItem(CharId, CharType, CharId, SolarSystem, 57));
        world.Add(new DogmaItem(Hull, StructureHullType, 1, SolarSystem));
        world.Add(new DogmaItem(Skill, SkillType, CharId, CharId, 7));
        world.SetValue(Skill, 327, 10);
        world.Add(new DogmaItem(ModInStructure, FittedInStructure, 1, Hull, 0, ItemState.Online));

        Assert.Equal(110, world.Get(ModInStructure, 6), 9); // what in the structure needs the skill: 100 plus 10 percent
        Assert.Equal(550, world.Get(Hull, 9), 9); // the structure itself: 500 plus 10 percent
    }

    [Fact]
    public void A_skill_that_is_not_carried_by_the_pilot_does_not_act_on_the_structure()
    {
        var world = new DogmaWorld(Statics(), CharId, structureId: Hull);
        world.Add(new DogmaItem(Hull, StructureHullType, 1, SolarSystem));
        world.Add(new DogmaItem(Skill, SkillType, 999, 999, 7)); // someone else's skill
        world.SetValue(Skill, 327, 10);
        world.Add(new DogmaItem(ModInStructure, FittedInStructure, 1, Hull, 0, ItemState.Online));

        Assert.Equal(100, world.Get(ModInStructure, 6), 9);
        Assert.Equal(500, world.Get(Hull, 9), 9);
    }

    [Fact]
    public void A_module_in_the_structure_reaches_the_ship_only_through_the_ship_domain_and_the_structure_only_through_its_own()
    {
        var world = new DogmaWorld(Statics(), shipId: Ship, structureId: Hull);
        world.Add(new DogmaItem(Ship, ShipHullType, 1, SolarSystem, 4));
        world.Add(new DogmaItem(Hull, StructureHullType, 1, SolarSystem));
        world.Add(new DogmaItem(ModInStructure, FittedInStructure, 1, Hull, 0, ItemState.Online)); // its effect names the ship: not in the structure's reach
        world.Add(new DogmaItem(ModInShip, FittedInShip, 1, Ship, 19, ItemState.Online)); // its effect names the structure: not in the ship's reach

        Assert.Equal(50, world.Get(Ship, 9), 9);
        Assert.Equal(500, world.Get(Hull, 9), 9);
    }

    [Fact]
    public void A_world_without_a_structure_does_not_resolve_the_structure_domain()
    {
        var world = new DogmaWorld(Statics(), CharId, shipId: Ship);
        world.Add(new DogmaItem(CharId, CharType, CharId, SolarSystem, 57));
        world.Add(new DogmaItem(Ship, ShipHullType, CharId, SolarSystem, 4));
        world.Add(new DogmaItem(Hull, StructureHullType, 1, SolarSystem));
        world.Add(new DogmaItem(Skill, SkillType, CharId, CharId, 7));
        world.SetValue(Skill, 327, 10);
        world.Add(new DogmaItem(ModInStructure, FittedInStructure, 1, Hull, 0, ItemState.Online));

        Assert.Equal(100, world.Get(ModInStructure, 6), 9);
        Assert.Equal(500, world.Get(Hull, 9), 9);
        Assert.Equal(50, world.Get(Ship, 9), 9);
    }

    [Fact]
    public void A_structure_set_after_the_world_is_built_acts_as_one_given_at_construction()
    {
        var world = new DogmaWorld(Statics(), CharId);
        world.Add(new DogmaItem(CharId, CharType, CharId, SolarSystem, 57));
        world.Add(new DogmaItem(Hull, StructureHullType, 1, SolarSystem));
        world.Add(new DogmaItem(Skill, SkillType, CharId, CharId, 7));
        world.SetValue(Skill, 327, 10);
        world.Add(new DogmaItem(ModInStructure, FittedInStructure, 1, Hull, 0, ItemState.Online));
        Assert.Equal(100, world.Get(ModInStructure, 6), 9);

        world.SetStructure(Hull);
        Assert.Equal(110, world.Get(ModInStructure, 6), 9);
        Assert.Equal(Hull, world.StructureId);
    }
}
