using Eve.Dogma;

namespace Eve.Fitting;

/// <summary>The kinds of slot a ship has, and so of module it takes.</summary>
public enum SlotFamily
{
    Low,
    Medium,
    High,
    Rig,
    Subsystem,
}

/// <summary>Where on a ship a module can be: the inventory flags of the slots, and how many a ship has of each.</summary>
public static class Slots
{
    public const int LowFirst = 11;
    public const int MediumFirst = 19;
    public const int HighFirst = 27;
    public const int RigFirst = 92;
    public const int SubsystemFirst = 125;

    /// <summary>Every family has flags for eight slots; a ship uses the first few.</summary>
    public const int FlagsPerFamily = 8;

    // the slot attributes of a ship
    public const int LowSlots = 12;
    public const int MediumSlots = 13;
    public const int HighSlots = 14;
    public const int RigSlots = 1137;
    public const int MaxSubsystems = 1367;

    // the effects that say which slot a module needs (the static data files them under power)
    private const int HighPowerEffect = 12;
    private const int MediumPowerEffect = 13;
    private const int LowPowerEffect = 11;

    public static int FirstFlag(SlotFamily family) => family switch
    {
        SlotFamily.Low => LowFirst,
        SlotFamily.Medium => MediumFirst,
        SlotFamily.High => HighFirst,
        SlotFamily.Rig => RigFirst,
        _ => SubsystemFirst,
    };

    /// <summary>The kind of slot a flag is, or null when the flag is no slot.</summary>
    public static SlotFamily? FamilyOfFlag(int flag)
    {
        foreach (var family in Enum.GetValues<SlotFamily>())
        {
            var first = FirstFlag(family);
            if (flag >= first && flag < first + FlagsPerFamily)
            {
                return family;
            }
        }
        return null;
    }

    public static bool IsSlotFlag(int flag) => FamilyOfFlag(flag) is not null;

    /// <summary>The kind of slot a type of module needs, or null when it is not a module that can be fitted.</summary>
    public static SlotFamily? FamilyOfType(TypeDef type)
    {
        foreach (var effect in type.EffectIds)
        {
            switch (effect)
            {
                case HighPowerEffect: return SlotFamily.High;
                case MediumPowerEffect: return SlotFamily.Medium;
                case LowPowerEffect: return SlotFamily.Low;
                case BuiltInEffects.RigSlot: return SlotFamily.Rig;
                case BuiltInEffects.SubSystem: return SlotFamily.Subsystem;
            }
        }
        return null;
    }

    /// <summary>How many slots of a kind the ship has now.</summary>
    public static int Count(DogmaWorld world, long shipId, SlotFamily family)
    {
        var attribute = family switch
        {
            SlotFamily.Low => LowSlots,
            SlotFamily.Medium => MediumSlots,
            SlotFamily.High => HighSlots,
            SlotFamily.Rig => RigSlots,
            _ => MaxSubsystems,
        };
        return (int)Math.Clamp(world.Get(shipId, attribute), 0, FlagsPerFamily);
    }

    /// <summary>The flags of the slots a ship has of a kind, in order.</summary>
    public static IEnumerable<int> Flags(DogmaWorld world, long shipId, SlotFamily family) =>
        Enumerable.Range(FirstFlag(family), Count(world, shipId, family));
}
