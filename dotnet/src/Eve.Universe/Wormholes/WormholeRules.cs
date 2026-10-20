namespace Eve.Universe;

public enum WormholeMassState
{
    Stable,
    Shrunk,
    Critical,
    Collapsed,
}

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
/// </summary>
public static class WormholeRules
{
    public const int FirstSystemId = 31_000_000;
    public const int SystemIdLimit = 32_000_000;

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
