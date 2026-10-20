namespace Eve.Fitting;

/// <summary>
/// The Strategic Cruiser (SDE group 963) rules for its subsystems: one subsystem of each of the four classes (Core 125, Defensive 126, Offensive 127,
/// (<c>STRATEGIC_CRUISER_SUBSYSTEM_FLAGS</c>, <c>getStrategicCruiserSubsystemStatus</c>) and <c>shipService.js</c> (<c>_assertStrategicCruiserSubsystems</c>),
/// which mirror the client's <c>assembleModularShip.py</c>. A hull's own <c>maxSubSystems</c> (1367) says five; the SDE holds four classes.
/// </summary>
public static class StrategicCruisers
{
    public const int GroupId = 963;

    /// <summary>The attribute that names the subsystem class (the flag its subsystems are fitted to).</summary>
    public const int SubSystemSlotAttribute = 1366;

    /// <summary>The attribute that names the hull a subsystem fits (SDE 1380, <c>fitsToShipType</c>).</summary>
    public const int FitsToShipTypeAttribute = 1380;

    /// <summary>The four subsystem classes, by the flag they are fitted to.</summary>
    public static readonly int[] Flags = [125, 126, 127, 128];

    /// <summary>The flags of the classes a hull has no subsystem for, in the order of <see cref="Flags"/>.</summary>
    public static IReadOnlyList<int> Missing(IEnumerable<int> subsystemFlags)
    {
        var fitted = subsystemFlags.ToHashSet();
        return [.. Flags.Where(flag => !fitted.Contains(flag))];
    }

    /// <summary>
    /// The refusal a Strategic Cruiser with these subsystem flags gets, or null when its fit is complete (one subsystem of each class, no more).
    /// </summary>
    public static string? IncompleteFit(IEnumerable<int> subsystemFlags)
    {
        var flags = subsystemFlags.ToList();
        var missing = Missing(flags);
        if (missing.Count == 0 && flags.Count == Flags.Length)
        {
            return null;
        }
        const string required = "A strategic cruiser requires one Core, Defensive, Offensive, and Propulsion subsystem before it can be used.";
        if (missing.Count == 0)
        {
            return $"{required} Remove duplicate or incompatible subsystems before boarding.";
        }
        var names = missing.Select(flag => flag switch { 125 => "Core", 126 => "Defensive", 127 => "Offensive", _ => "Propulsion" });
        return $"{required} Missing: {string.Join(", ", names)}.";
    }
}
