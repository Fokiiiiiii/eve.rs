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

/// <summary>The lifecycle rules of a connection: its expiry, the mass of a transit, and the seed of its replacement.</summary>
public static class WormholeLifecycle
{
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

    /// <summary>The seed of the replacement opened after a collapse: the next value of the connection's seed.</summary>
    public static ulong ReplacementSeed(ulong seed) => new SeededRandom(seed).NextUInt64();
}
