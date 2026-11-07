using Eve.Universe;

namespace Eve.Wormholes;

/// <summary>The outcome of one ship's transit through a wormhole.</summary>
public enum TransitOutcome
{
    /// <summary>The ship went through and the wormhole has mass left.</summary>
    Passed,

    Refused,

    /// <summary>The ship's mass emptied the wormhole: it collapses behind the ship.</summary>
    Collapsed,
}

public sealed record TransitResult(TransitOutcome Outcome, double MassKg, WormholeMassState State);

public enum WormholeJumpRejection
{
    None,
    UnknownEnd,
    Collapsed,
    Expired,
    WrongSystem,
    Polarized,
    ShipTooMassive,
    SimulationRefused,
}

public sealed record WormholeJumpAttempt(WormholeJumpRejection Rejection, TransitResult? Transit)
{
    public bool Accepted => Rejection == WormholeJumpRejection.None;
}

/// <summary>The lifecycle rules of a connection: its expiry, the mass of a transit, and the seed of its replacement.</summary>
public static class WormholeLifecycle
{
    public const int MaxShipMassSmall = 1;
    public const int MaxShipMassMedium = 2;
    public const int MaxShipMassLarge = 3;
    public const int MaxShipMassVeryLarge = 4;

    /// <summary>When a wormhole opened at <paramref name="createdMs"/> expires (SDE <c>wormholeMaxStableTime</c>, minutes).</summary>
    public static long ExpiresAt(long createdMs, WormholeType type) => createdMs + type.MaxStableMinutes * 60_000L;

    /// <summary>The mass accounting of one transit (kg): refused over the individual limit, otherwise the ship's mass comes off the wormhole.</summary>
    public static TransitResult Transit(WormholeType type, double remainingMassKg, double shipMassKg)
    {
        if (!WormholeRules.MassAllowsTransit(type, shipMassKg))
        {
            return new TransitResult(TransitOutcome.Refused, remainingMassKg, WormholeRules.MassStateOf(remainingMassKg, type.MaxStableMassKg));
        }
        var after = WormholeRules.MassAfterTransit(remainingMassKg, shipMassKg);
        return after <= 0
            ? new TransitResult(TransitOutcome.Collapsed, 0, WormholeMassState.Collapsed)
            : new TransitResult(TransitOutcome.Passed, after, WormholeRules.MassStateOf(after, type.MaxStableMassKg));
    }

    /// <summary>The client's wormholeAge slim value, derived from the stored expiry time (EveJS resolveWormholeAge).</summary>
    public static int AgeState(long nowMs, long expiresMs)
    {
        var remaining = Math.Max(0, expiresMs - nowMs);
        return remaining <= 60 * 60_000L ? 3
            : remaining <= 4 * 60 * 60_000L ? 2
            : remaining <= 24 * 60 * 60_000L ? 1
            : 0;
    }

    /// <summary>The client's wormholeSize slim value: 1 for full mass, .7 after the first mass-state boundary, .4 below half.</summary>
    public static double Size(double remainingMassKg, double maximumMassKg)
    {
        if (maximumMassKg <= 0)
            return 1.0;
        var remainingRatio = Math.Clamp(remainingMassKg / maximumMassKg, 0.0, 1.0);
        return remainingRatio < 0.5 ? 0.4 : remainingRatio < 1.0 ? 0.7 : 1.0;
    }

    /// <summary>The client's maxShipJumpMass category, not the raw per-ship kilogram limit.</summary>
    public static int MaxShipJumpMassCategory(double maximumJumpMassKg) => maximumJumpMassKg <= 20_000_000 ? MaxShipMassSmall
        : maximumJumpMassKg <= 300_000_000 ? MaxShipMassMedium
        : maximumJumpMassKg <= 1_500_000_000 ? MaxShipMassLarge
        : MaxShipMassVeryLarge;

    /// <summary>The seed of the replacement opened after a collapse: the next value of the connection's seed.</summary>
    public static ulong ReplacementSeed(ulong seed) => new SeededRandom(seed).NextUInt64();
}
