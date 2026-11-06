namespace Eve.Ships;

/// <summary>
/// The ore an asteroid belt holds, by the true security of its system. The SDE places each belt and gives its size but no ore, so this is the one
/// are merged by true security: the SDE system record carries the number, not the security class letter. Mercoxit (Deep Core Mining) and the
/// </summary>
public static class BeltOres
{
    /// <summary>The volume a seeded rock is given, in m3: the floor of EVE's estimate for a field (miningRuntimeState.js, estimateOriginalQuantity).</summary>
    // shortcut: the belt's radius is not passed to the host, so every rock takes the floor, not radius squared; upgrade when the radius reaches the seeding.
    public const double RockVolumeM3 = 15_000d;

    /// <summary>The ores a belt of this true security holds, most common first.</summary>
    public static IReadOnlyList<string> For(double security)
    {
        if (security >= 0.9) return ["Veldspar", "Scordite"];
        if (security > 0.7) return ["Veldspar", "Scordite", "Pyroxeres"];
        if (security >= 0.45) return ["Veldspar", "Scordite", "Pyroxeres", "Plagioclase"];
        if (security > 0d)
        {
            return security < 0.25
                ? ["Pyroxeres", "Kernite", "Jaspet", "Omber", "Hemorphite", "Hedbergite"]
                : ["Pyroxeres", "Kernite", "Jaspet", "Omber"];
        }
        if (security > -0.5) return ["Kernite", "Pyroxeres"];
        return security > -0.6 ? ["Kernite", "Pyroxeres", "Bistot"] : ["Kernite", "Pyroxeres", "Bistot", "Arkonor"];
    }

    /// <summary>The units a rock of this ore (unit volume in m3) holds: its volume divided by the unit volume, at least one unit.</summary>
    public static long UnitsFor(double unitVolume) => Math.Max(1L, (long)Math.Floor(RockVolumeM3 / unitVolume));
}
