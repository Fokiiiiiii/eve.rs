namespace Eve.Dogma;

/// <summary>How a modifier changes the value it is applied to. The order the game applies them in is <see cref="Operations.Precedence"/>.</summary>
public enum Operation
{
    PreAssign = -1,
    PreMul = 0,
    PreDiv = 1,
    ModAdd = 2,
    ModSub = 3,
    PostMul = 4,
    PostDiv = 5,
    PostPercent = 6,
    PostAssign = 7,
    PostPercentUnpenalised = 8,

    /// <summary>The skill-level relationship of the skill effect. The engine takes a skill's level as given, so it changes nothing.</summary>
    SkillRelationship = 9,
}

public static class Operations
{
    /// <summary>The order the operations are applied in: assign first, then multiply, add, multiply again, the percentages, and the final assignment last.</summary>
    public static readonly Operation[] Precedence =
    [
        Operation.PreAssign, Operation.PreMul, Operation.PreDiv, Operation.ModAdd, Operation.ModSub,
        Operation.PostMul, Operation.PostDiv, Operation.PostPercent, Operation.PostPercentUnpenalised, Operation.PostAssign,
    ];

    /// <summary>The operations the stacking penalty applies to (when the attribute is penalised at all).</summary>
    public static bool IsPenalisable(Operation op) => op is Operation.PreMul or Operation.PostMul or Operation.PostPercent or Operation.PreDiv or Operation.PostDiv;

    public static double Apply(Operation op, double value, double modifier) => op switch
    {
        Operation.PreAssign or Operation.PostAssign => modifier,
        Operation.PreMul or Operation.PostMul => value * modifier,
        Operation.PreDiv or Operation.PostDiv => value / modifier,
        Operation.ModAdd => value + modifier,
        Operation.ModSub => value - modifier,
        Operation.PostPercent or Operation.PostPercentUnpenalised => value * (100 + modifier) / 100,
        _ => value,
    };
}

/// <summary>Which items a modifier reaches, counted from the domain it names.</summary>
public enum ModifierFunc
{
    /// <summary>The one item in the domain.</summary>
    Item,

    /// <summary>Every item located in the domain item (the modules of a ship, the skills of a character).</summary>
    Location,

    /// <summary>Items located in the domain item that belong to a group.</summary>
    LocationGroup,

    /// <summary>Items located in the domain item that need a skill.</summary>
    LocationRequiredSkill,

    OwnerRequiredSkill,
}

/// <summary>
/// One change an effect makes: the attribute of a target modified by the value of an attribute of the source. <paramref name="Literal"/> is the value the
/// modifier applies when it carries its own (a timed buff's value, <see cref="BuffCollection"/>), used instead of <paramref name="ModifyingAttributeId"/>;
/// null for every modifier of the static data.
/// </summary>
public sealed record Modifier(
    ModifierFunc Func, string? Domain, Operation Operation, int ModifiedAttributeId, int ModifyingAttributeId, int GroupId = 0, int SkillTypeId = 0,
    double? Literal = null)
{
    /// <summary>As the skill: the modifier reaches what needs the skill that carries the effect (one effect serves every skill with a bonus of its kind).</summary>
    public const int OwnSkill = -1;
}

/// <summary>An effect: when it runs its modifiers apply. The category says when it runs.</summary>
public sealed record EffectDef(int Id, string Name, int Category, IReadOnlyList<Modifier> Modifiers)
{
    public const int Passive = 0;
    public const int Activation = 1;
    public const int Target = 2;
    public const int Area = 3;
    public const int Online = 4;
    public const int Overload = 5;

    /// <summary>Whether the SDE publishes this dogma effect. This does not determine whether a module type is published.</summary>
    public bool Published { get; init; } = true;

    /// <summary>What the client's effects are called in the destiny stream (<c>effects.Afterburner</c>); empty when the data has none.</summary>
    public string Guid { get; init; } = "";

    /// <summary>The attribute that holds how far the effect reaches (0 when it has no range).</summary>
    public int RangeAttributeId { get; init; }

    /// <summary>The attribute that holds how long a cycle of the effect takes (0 when it has none).</summary>
    public int DurationAttributeId { get; init; }

    /// <summary>The attribute that holds the falloff of the effect's range (0 when it has none: a hard cutoff at the range).</summary>
    public int FalloffAttributeId { get; init; }

    /// <summary>The attribute that holds the capacitor a cycle takes (0 when it needs none).</summary>
    public int DischargeAttributeId { get; init; }

    /// <summary>The type attribute containing the chance for this effect to trigger (0 when guaranteed).</summary>
    public int ChanceAttributeId { get; init; }
}

/// <summary>What an attribute is: its default, whether stacking penalties apply to it, its cap and floor, and its kind of number.</summary>
public sealed record AttributeDef(int Id, string Name, double DefaultValue, bool Stackable, bool HighIsGood, int DataType, int MaxAttributeId = 0, int MinAttributeId = 0);

/// <summary>A type as dogma sees it: its attributes and the effects it has.</summary>
public sealed record TypeDef(int TypeId, int GroupId, int CategoryId, IReadOnlyDictionary<int, double> Attributes, IReadOnlyList<int> EffectIds, string Name = "", int GraphicId = 0, int RaceId = 0)
{
    /// <summary>Whether the SDE publishes this type to ordinary clients.</summary>
    public bool Published { get; init; } = true;

    /// <summary>The attributes that name the skills a type needs (requiredSkill1 to requiredSkill6).</summary>
    public static readonly int[] RequiredSkillAttributes = [182, 183, 184, 1285, 1289, 1290];

    /// <summary>The attributes that give the level of each skill a type needs, in the order of <see cref="RequiredSkillAttributes"/>.</summary>
    public static readonly int[] RequiredSkillLevelAttributes = [277, 278, 279, 1286, 1287, 1288];

    /// <summary>Whether an attribute names a skill a type needs, or the level of one.</summary>
    public static bool IsRequiredSkillAttribute(int attributeId) =>
        Array.IndexOf(RequiredSkillAttributes, attributeId) >= 0 || Array.IndexOf(RequiredSkillLevelAttributes, attributeId) >= 0;

    public IEnumerable<int> RequiredSkills =>
        RequiredSkillAttributes.Where(Attributes.ContainsKey).Select(a => (int)Attributes[a]).Where(s => s > 0);
}

/// <summary>How far a fitted item is switched on. It decides which of its effects apply.</summary>
public enum ItemState
{
    Offline = 0,
    Online = 1,
    Active = 2,
    Overloaded = 3,
}
