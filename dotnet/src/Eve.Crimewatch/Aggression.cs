using Eve.Universe;

namespace Eve.Crimewatch;

/// <summary>What an attack on another pilot's ship was, to the attacker.</summary>
public enum AggressionReason
{
    /// <summary>The law allows it (a war, an engagement, the same corporation where that is allowed): only the timers run.</summary>
    Legal,

    /// <summary>No crime where it happened (null security): only the timers run.</summary>
    NoCrime,

    /// <summary>The target is a criminal or a suspect already: anyone may shoot it, only the timers run.</summary>
    TargetFlagged,

    /// <summary>A crime of a suspect: shooting a ship in low security.</summary>
    Suspect,

    /// <summary>A crime of a criminal: shooting a ship in high security, or a capsule in low security. CONCORD answers it in high security, the security status falls and the target gets a kill right.</summary>
    Criminal,
}

/// <summary>What <see cref="Aggression.Record"/> made of the attacker, and why.</summary>
public sealed record AggressionOutcome(CombatState Attacker, AggressionReason Reason)
{
    public bool IsCriminalOffense => Reason == AggressionReason.Criminal;
}

/// <summary>
/// What an attack on the ship of another pilot does to the attacker. A port of <c>recordHighSecCriminalAggression</c> (and the <c>triggerHighSecCriminalOffense</c> it ends in) of
/// <c>crimewatchState.js</c> of EVE.js (ADR 0009), for the part that is the attacker's own state: the weapons timer (a minute) and the PvP timer (fifteen minutes) run in every system
/// whatever the law says; the flag is the crime. In high security the attacker is a criminal, in low security a suspect (a criminal for a capsule), in null security nothing, and nothing
/// when the target is a criminal or a suspect already or the law allows the attack. The consequences of a crime (the security status, kill rights, CONCORD) are the caller's.
/// </summary>
public static class Aggression
{
    /// <summary>The attacker after it attacked <paramref name="target"/> at <paramref name="nowMs"/> in a system of this class, and what the attack was.</summary>
    /// <param name="attacker">What crimewatch keeps for the attacker.</param>
    /// <param name="target">What crimewatch keeps for the pilot it attacked.</param>
    /// <param name="where">The security class of the system.</param>
    /// <param name="targetIsCapsule">Whether the target's ship is a capsule (a crime in low security too).</param>
    /// <param name="legal">Whether the law allows the attack (a war, a limited engagement, the same corporation).</param>
    public static AggressionOutcome Record(CombatState attacker, CombatState target, SecurityClass where, long solarSystemId, bool targetIsCapsule, bool legal, long nowMs)
    {
        var timed = attacker with
        {
            WeaponsUntilMs = Math.Max(attacker.WeaponsUntilMs, nowMs + CombatState.WeaponsDurationMs),
            PvpUntilMs = Math.Max(attacker.PvpUntilMs, nowMs + CombatState.PvpDurationMs),
            SolarSystemId = solarSystemId,
        };
        if (legal)
        {
            return new AggressionOutcome(timed, AggressionReason.Legal);
        }
        var crime = Security.IsHighSecOrHigher(where) ? Flag.Criminal
            : Security.IsLowSec(where) ? (targetIsCapsule ? Flag.Criminal : Flag.Suspect)
            : Flag.None;
        if (crime == Flag.None)
        {
            return new AggressionOutcome(timed, AggressionReason.NoCrime);
        }
        if (target.FlagAt(nowMs) != Flag.None)
        {
            return new AggressionOutcome(timed, AggressionReason.TargetFlagged);
        }
        return new AggressionOutcome(timed.Flagged(crime, nowMs), crime == Flag.Criminal ? AggressionReason.Criminal : AggressionReason.Suspect);
    }
}
