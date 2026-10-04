namespace Eve.Crimewatch;

/// <summary>
/// What a crime costs the attacker in security status: a port of <c>applySecurityStatusPenaltyForAggression</c> of <c>crimewatchState.js</c> of EVE.js (ADR 0009). Shooting a ship
/// costs 0.025 of the way to 10, a capsule ten times that, each scaled by how safe the system is and by how far the target's standing is from the attacker's:
/// <c>modification = base × security × (1 + (target − attacker) / 100)</c>, <c>next = attacker + (10 − attacker) × modification</c>, kept between −10 and 10 and rounded to four places.
/// Nothing is lost in null security, and a pilot that has lost for a target in the last minute loses nothing more for it.
/// </summary>
public static class SecurityPenalty
{
    public const double ShipModifier = -0.025;
    public const double CapsuleModifier = -0.25;
    public const double Min = -10;
    public const double Max = 10;

    /// <summary>How long after a loss for a target another shot at it costs nothing.</summary>
    public const long DuplicateWindowMs = CombatState.WeaponsDurationMs;

    /// <summary>A security status as it is kept: between −10 and 10, four decimals (half away from zero, as <c>toFixed(4)</c> of the reference).</summary>
    public static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Clamp(value, Min, Max), 4, MidpointRounding.AwayFromZero) : 0;

    /// <summary>The security of a system as the penalty reads it: between 0 and 1, and at least 0.05 where there is any (<c>getSolarSystemPseudoSecurity</c>).</summary>
    public static double PseudoSecurity(double security)
    {
        var clamped = double.IsFinite(security) ? Math.Clamp(security, 0, 1) : 0;
        return clamped is > 0 and < 0.05 ? 0.05 : clamped;
    }

    /// <summary>The attacker's security status after shooting a ship or capsule of a pilot with <paramref name="target"/> in a system of this security (the same as before when none is lost).</summary>
    public static double Next(double attacker, double target, double systemSecurity, bool targetIsCapsule)
    {
        var previous = Clamp(attacker);
        var modification = (targetIsCapsule ? CapsuleModifier : ShipModifier) * PseudoSecurity(systemSecurity) * (1 + (Clamp(target) - previous) / 100);
        return Clamp(previous + (Max - previous) * modification);
    }
}
