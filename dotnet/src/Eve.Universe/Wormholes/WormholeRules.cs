namespace Eve.Universe;

/// <summary>How much mass a wormhole has left against its stable mass (wiki "Wormhole": above 50%, below 50% "first shrink", below 10% "crit").</summary>
public enum WormholeMassState
{
    Stable,
    Shrunk,
    Critical,
    Collapsed,
}

/// <summary>The lifetime display states (wiki "Wormhole": under 1 day, under 4 hours, under 1 hour, expired).</summary>
public enum WormholeLifetimeState
{
    Stable,
    UnderOneDay,
    UnderFourHours,
    UnderOneHour,
    Expired,
}

/// <summary>
/// Wormhole space rules: which solar systems are J-space (the SDE ids 31000000 to 31999999, all in the regions 11000000 to
/// 11999999), the mass a wormhole takes per transit, and the state labels. The numbers come from <see cref="WormholeType"/>
/// (the SDE); the state thresholds and the polarization time are the EVE University wiki's, not SDE fields.
/// </summary>
public static class WormholeRules
{
    public const int FirstSystemId = 31_000_000;
    public const int SystemIdLimit = 32_000_000;

    /// <summary>A pilot who went through a wormhole in one direction cannot go through it that way again for this long (wiki "Wormhole", "Polarization").</summary>
    public static readonly TimeSpan Polarization = TimeSpan.FromMinutes(5);

    public static bool IsJSpaceSystem(int solarSystemId) => solarSystemId is >= FirstSystemId and < SystemIdLimit;

    /// <summary>A ship may pass when its mass is at most the wormhole's individual limit (SDE <c>wormholeMaxJumpMass</c>).</summary>
    public static bool MassAllowsTransit(WormholeType type, double shipMassKg) => shipMassKg <= type.MaxJumpMassKg;

    /// <summary>The mass left after a ship passes. Zero or less means the wormhole collapses.</summary>
    public static double MassAfterTransit(double remainingMassKg, double shipMassKg) => remainingMassKg - shipMassKg;

    public static WormholeMassState MassStateOf(double remainingMassKg, double maxStableMassKg)
    {
        if (remainingMassKg <= 0)
        {
            return WormholeMassState.Collapsed;
        }
        var fraction = remainingMassKg / maxStableMassKg;
        return fraction >= 0.5 ? WormholeMassState.Stable
            : fraction >= 0.1 ? WormholeMassState.Shrunk
            : WormholeMassState.Critical;
    }

    public static WormholeLifetimeState LifetimeStateOf(TimeSpan remaining) =>
        remaining <= TimeSpan.Zero ? WormholeLifetimeState.Expired
        : remaining < TimeSpan.FromHours(1) ? WormholeLifetimeState.UnderOneHour
        : remaining < TimeSpan.FromHours(4) ? WormholeLifetimeState.UnderFourHours
        : remaining < TimeSpan.FromDays(1) ? WormholeLifetimeState.UnderOneDay
        : WormholeLifetimeState.Stable;
}
