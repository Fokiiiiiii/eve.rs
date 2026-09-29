namespace Eve.Dogma;

/// <summary>Whose attributes a brain effect changes.</summary>
public enum BrainDomain
{
    Character,
    Ship,
    Structure,
}

/// <summary>
/// What one trained skill does to one attribute, already worked out for the level trained: the client puts these on the
/// character, the ship or the structure it is in, and applies them itself when it fits a ship on its own (the fitting window's
/// preview). <see cref="ModifierType"/> says whose things it reaches: <c>M</c> the item itself, <c>L</c> everything in it,
/// <c>LG</c> a group in it, <c>LRS</c> what in it needs a skill, <c>ORS</c> what the character owns and needs a skill.
/// </summary>
public sealed record BrainEffect(int SkillTypeId, string ModifierType, BrainDomain Domain, int TargetAttributeId, int Operation, IReadOnlyList<int> Extras, double Value);

/// <summary>The brain of a character: its effects on the character, on the ship it flies and on the structure it is in.</summary>
public sealed record Brain(IReadOnlyList<BrainEffect> Character, IReadOnlyList<BrainEffect> Ship, IReadOnlyList<BrainEffect> Structure);

/// <summary>Works out the brain from the skills a character has trained and the effects the static data gives them.</summary>
public static class BrainBuilder
{
    private const int SkillCategory = 16;

    /// <summary>The character attributes the industry skills change; their effects are listed on their own, in the order of these skills.</summary>
    private static readonly HashSet<int> IndustryAttributes = [196, 219, 385, 387, 398, 467, 1959, 2662, 2664];

    private static readonly int[] IndustrySkills = [3380, 3387, 3388, 3402, 3403, 3406, 3409, 24624, 24625, 45746, 45748, 45749];

    /// <summary>The skill bonuses the static data leaves to code, as the game files them: (attribute changed, attribute carrying the bonus, kind, domain).</summary>
    private static readonly Dictionary<int, (int Target, int Source, string Type, BrainDomain Domain)> Named = new()
    {
        [BuiltInEffects.DroneDamageBonus] = (BuiltInEffects.DamageMultiplier, BuiltInEffects.DamageMultiplierBonus, "ORS", BrainDomain.Character),
        [BuiltInEffects.MissileEmDamageBonus] = (BuiltInEffects.EmDamage, BuiltInEffects.DamageMultiplierBonus, "LRS", BrainDomain.Ship),
        [BuiltInEffects.MissileExplosiveDamageBonus] = (BuiltInEffects.ExplosiveDamage, BuiltInEffects.DamageMultiplierBonus, "LRS", BrainDomain.Ship),
        [BuiltInEffects.MissileKineticDamageBonus] = (BuiltInEffects.KineticDamage, BuiltInEffects.DamageMultiplierBonus, "LRS", BrainDomain.Ship),
        [BuiltInEffects.MissileThermalDamageBonus] = (BuiltInEffects.ThermalDamage, BuiltInEffects.DamageMultiplierBonus, "LRS", BrainDomain.Ship),
        [BuiltInEffects.SelfRof] = (BuiltInEffects.RateOfFire, BuiltInEffects.RofBonus, "LRS", BrainDomain.Ship),
    };

    public static Brain Build(DogmaWorld world, DogmaStatic statics)
    {
        var character = new List<BrainEffect>();
        var ship = new List<BrainEffect>();
        var structure = new List<BrainEffect>();

        var skills = world.Items
            .Where(i => i.OwnerId == world.CharacterId && statics.Type(i.TypeId)?.CategoryId == SkillCategory && Level(world, i) > 0)
            .ToList();
        foreach (var skill in skills)
        {
            foreach (var effectId in statics.Type(skill.TypeId)!.EffectIds)
            {
                if (statics.Effect(effectId) is not { } effect)
                {
                    continue;
                }
                if (Named.TryGetValue(effect.Id, out var named))
                {
                    // the game files these by hand: the bonus is the skill's own, and it acts on what needs this very skill
                    if (Definition(world, skill, named.Target, named.Source, named.Type, named.Domain, [skill.TypeId], (int)Operation.PostPercent) is { } kept)
                    {
                        Into(kept, character, ship, structure);
                    }
                    continue;
                }
                foreach (var modifier in effect.Modifiers)
                {
                    if (FromModifier(world, skill, modifier) is { } definition)
                    {
                        Into(definition, character, ship, structure);
                    }
                }
            }
        }
        character.Sort(Compare);
        ship.Sort(Compare);
        structure.Sort(Compare);
        character.AddRange(Industry(world, statics));
        return new Brain(character, ship, structure);
    }

    private static int Level(DogmaWorld world, DogmaItem skill) => (int)Math.Clamp(world.Get(skill.Id, DogmaWorld.SkillLevel), 0, 5);

    private static void Into(BrainEffect effect, List<BrainEffect> character, List<BrainEffect> ship, List<BrainEffect> structure)
    {
        switch (effect.Domain)
        {
            case BrainDomain.Character: character.Add(effect); break;
            case BrainDomain.Ship: ship.Add(effect); break;
            default: structure.Add(effect); break;
        }
    }

    private static BrainEffect? Definition(DogmaWorld world, DogmaItem skill, int target, int source, string type, BrainDomain domain, IReadOnlyList<int> extras, int operation)
    {
        if (target <= 0 || source <= 0)
        {
            return null;
        }
        var value = world.Get(skill.Id, source);
        return double.IsFinite(value) && Math.Abs(value) > 1e-9
            ? new BrainEffect(skill.TypeId, type, domain, target, operation, extras, Math.Round(value, 6, MidpointRounding.AwayFromZero))
            : null;
    }

    private static BrainEffect? FromModifier(DogmaWorld world, DogmaItem skill, Modifier modifier)
    {
        var (type, extras) = modifier.Func switch
        {
            ModifierFunc.Item => ("M", (IReadOnlyList<int>?)[]),
            ModifierFunc.Location => ("L", []),
            ModifierFunc.LocationGroup => ("LG", modifier.GroupId > 0 ? [modifier.GroupId] : null),
            ModifierFunc.LocationRequiredSkill => ("LRS", modifier.SkillTypeId > 0 ? [modifier.SkillTypeId] : null),
            ModifierFunc.OwnerRequiredSkill => ("ORS", modifier.SkillTypeId > 0 ? [modifier.SkillTypeId] : null),
            _ => ("", null),
        };
        var domain = modifier.Domain switch
        {
            "charID" => (BrainDomain?)BrainDomain.Character,
            "shipID" => BrainDomain.Ship,
            "structureID" => BrainDomain.Structure,
            _ => null,
        };
        if (extras is null || domain is null || (domain == BrainDomain.Character && IndustryAttributes.Contains(modifier.ModifiedAttributeId)))
        {
            return null;
        }
        return Definition(world, skill, modifier.ModifiedAttributeId, modifier.ModifyingAttributeId, type, domain.Value, extras, (int)modifier.Operation);
    }

    /// <summary>The industry skills' effects on the character's own attributes, in the order of the skills and of their effects.</summary>
    private static IEnumerable<BrainEffect> Industry(DogmaWorld world, DogmaStatic statics)
    {
        foreach (var skillType in IndustrySkills)
        {
            if (world.Items.FirstOrDefault(i => i.OwnerId == world.CharacterId && i.TypeId == skillType) is not { } skill || Level(world, skill) <= 0
                || statics.Type(skillType) is not { } type)
            {
                continue;
            }
            foreach (var effectId in type.EffectIds)
            {
                foreach (var m in statics.Effect(effectId)?.Modifiers ?? [])
                {
                    if (m.Domain != "charID" || m.Func != ModifierFunc.Item || m.ModifiedAttributeId <= 0 || m.ModifyingAttributeId <= 0)
                    {
                        continue;
                    }
                    var value = m.ModifyingAttributeId == DogmaWorld.SkillLevel ? Level(world, skill) : world.Get(skill.Id, m.ModifyingAttributeId);
                    if (double.IsFinite(value) && value != 0)
                    {
                        yield return new BrainEffect(skillType, "M", BrainDomain.Character, m.ModifiedAttributeId, (int)m.Operation, [], Math.Round(value, 6, MidpointRounding.AwayFromZero));
                    }
                }
            }
        }
    }

    /// <summary>The order the effects are listed in: by skill, then kind, attribute, operation, what they reach and value.</summary>
    private static int Compare(BrainEffect a, BrainEffect b)
    {
        var order = a.SkillTypeId.CompareTo(b.SkillTypeId);
        order = order != 0 ? order : string.CompareOrdinal(a.ModifierType, b.ModifierType);
        order = order != 0 ? order : a.TargetAttributeId.CompareTo(b.TargetAttributeId);
        order = order != 0 ? order : a.Operation.CompareTo(b.Operation);
        order = order != 0 ? order : string.CompareOrdinal(Json(a.Extras), Json(b.Extras));
        return order != 0 ? order : a.Value.CompareTo(b.Value);

        static string Json(IReadOnlyList<int> extras) => "[" + string.Join(',', extras) + "]";
    }
}
