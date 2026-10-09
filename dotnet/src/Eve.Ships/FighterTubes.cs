namespace Eve.Ships;

/// <summary>The broad class of a squadron: a tube slot count is kept per broad class (the standup classes count with the class they stand up from).</summary>
public enum FighterBroadClass
{
    Light,
    Support,
    Heavy,
}

/// <summary>One squadron class: the fighter attribute that says a type is of it, the host attribute that counts its slots, and its broad class.</summary>
public sealed record FighterClassDefinition(string Name, FighterBroadClass Broad, string FighterAttribute, string SlotAttribute);

/// <summary>
/// The fighter tube rules (EVE.js <c>fighterInventory.js</c>): which tube a squadron fits, how many tubes and slots a host has, and how big a squadron is.
/// Attributes are read by name through the caller's readers, so the rules stay pure and the caller decides whose dogma they are (the active ship's or a type's).
/// </summary>
public static class FighterTubes
{
    /// <summary>The five launch tubes of a carrier, in order (the item flags the client uses).</summary>
    public static readonly int[] TubeFlags = [159, 160, 161, 162, 163];

    public const string TubeCountAttribute = "fighterTubes";
    public const string SquadronMaxSizeAttribute = "fighterSquadronMaxSize";
    public const string SquadronSizeAttribute = "fighterSquadronSize";

    /// <summary>The classes in the reference's order: the first whose fighter attribute is above 0 is the class of a type.</summary>
    public static readonly IReadOnlyList<FighterClassDefinition> Classes =
    [
        new("light", FighterBroadClass.Light, "fighterSquadronIsLight", "fighterLightSlots"),
        new("support", FighterBroadClass.Support, "fighterSquadronIsSupport", "fighterSupportSlots"),
        new("heavy", FighterBroadClass.Heavy, "fighterSquadronIsHeavy", "fighterHeavySlots"),
        new("standupLight", FighterBroadClass.Light, "fighterSquadronIsStandupLight", "fighterStandupLightSlots"),
        new("standupSupport", FighterBroadClass.Support, "fighterSquadronIsStandupSupport", "fighterStandupSupportSlots"),
        new("standupHeavy", FighterBroadClass.Heavy, "fighterSquadronIsStandupHeavy", "fighterStandupHeavySlots"),
    ];

    /// <summary>The tube's ordinal (1 to 5), or 0 when the flag is not a tube.</summary>
    public static int Ordinal(int flag) => Array.IndexOf(TubeFlags, flag) + 1;

    public static bool IsTube(int flag) => Ordinal(flag) > 0;

    /// <summary>The class of a fighter type: the first class whose attribute is above 0, or null for a type that is not a fighter squadron.</summary>
    public static FighterClassDefinition? ClassOf(Func<string, double> fighterAttribute) =>
        Classes.FirstOrDefault(definition => fighterAttribute(definition.FighterAttribute) > 0);

    /// <summary>The size of a squadron of a type: its maximum size, else its size, 0 when neither is set.</summary>
    public static int MaxSquadronSize(Func<string, double> typeAttribute)
    {
        var max = Positive(typeAttribute(SquadronMaxSizeAttribute));
        return max > 0 ? max : Positive(typeAttribute(SquadronSizeAttribute));
    }

    /// <summary>The squadron an inventory item is: one singleton is one squadron, a stack is as many as it holds.</summary>
    public static long InventorySize(bool singleton, long quantity) => singleton ? 1 : Math.Max(0, quantity);

    /// <summary>
    /// Whether a squadron may go into a tube of the host now. <paramref name="occupied"/> is the broad class of each squadron already in a tube.
    /// A tube beyond the host's count, a class the host has no slots for, and a tube full of squadrons refuse.
    /// </summary>
    public static bool CanLoad(Func<string, double> hostAttribute, Func<string, double> fighterAttribute, int tubeFlag, IReadOnlyList<FighterBroadClass> occupied) =>
        RefusalOf(hostAttribute, fighterAttribute, tubeFlag, occupied) is null;

    /// <summary>Why a squadron may not go into the tube, or null when it may.</summary>
    public static FighterTubeRefusal? RefusalOf(Func<string, double> hostAttribute, Func<string, double> fighterAttribute, int tubeFlag, IReadOnlyList<FighterBroadClass> occupied)
    {
        var limit = Positive(hostAttribute(TubeCountAttribute));
        var ordinal = Ordinal(tubeFlag);
        if (limit <= 0 || ordinal <= 0 || ordinal > limit)
        {
            return FighterTubeRefusal.NoSuchTube;
        }
        if (ClassOf(fighterAttribute) is not { } definition)
        {
            return FighterTubeRefusal.NotASquadron;
        }
        if (Positive(hostAttribute(definition.SlotAttribute)) <= 0)
        {
            return FighterTubeRefusal.NoSlotForClass;
        }
        if (occupied.Count >= limit)
        {
            return FighterTubeRefusal.TubesFull;
        }
        return occupied.Count(broad => broad == definition.Broad) < Positive(hostAttribute(definition.SlotAttribute))
            ? null
            : FighterTubeRefusal.ClassSlotsFull;
    }

    /// <summary>A positive whole number of an attribute value; anything else (unset, zero, negative, not finite) is 0.</summary>
    private static int Positive(double value) => double.IsFinite(value) && value >= 1 ? (int)Math.Floor(value) : 0;
}

/// <summary>Why a squadron may not go into a launch tube.</summary>
public enum FighterTubeRefusal
{
    NoSuchTube,
    NotASquadron,
    NoSlotForClass,
    ClassSlotsFull,
    TubesFull,
}
