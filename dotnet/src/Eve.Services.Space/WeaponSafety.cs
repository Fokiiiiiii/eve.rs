using Eve.Crimewatch;
using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// The safety of a pilot, for the one case a weapon meets it: aimed at the ship of another pilot. A port of the part of <c>evaluateOffensiveAggression</c>
/// (<c>server/src/services/security/crimewatchState.js</c> of EVE.js, ADR 0009) that an unarmed war, no standing and no timers leave: an attack on another pilot's ship in empire space
/// is a crime, and the safety the pilot has set (<see cref="SafetyLevel"/>: none 0, partial 1, full 2) stops it unless it is at most what that crime asks for. In high security the
/// aggression makes the attacker a criminal (the safety must be none); in low security it makes the attacker a suspect (partial is enough), a criminal when the target is a capsule
/// (none again). Anywhere else (null security) the attack is no crime and the safety lets it be. A ship nobody flies is no pilot's: it may be shot anywhere.
/// </summary>
public static class WeaponSafety
{
    /// <summary>The error of the activation the safety stops (the reference's <c>errorMsg</c>; the client is shown it in <c>Failed to activate …</c>).</summary>
    public const string Refusal = "SafetyActivated";

    /// <summary>The highest safety level that lets a pilot attack another pilot's ship in a system of this class, or <c>null</c> when attacking there is no crime.</summary>
    public static int? RequiredLevel(SecurityClass securityClass, bool targetIsCapsule) =>
        Security.IsHighSecOrHigher(securityClass) ? SafetyLevel.None
        : Security.IsLowSec(securityClass) ? (targetIsCapsule ? SafetyLevel.None : SafetyLevel.Partial)
        : null;

    /// <summary>Whether a pilot with this safety level is stopped from firing at the ship of another pilot in a system of this class.</summary>
    public static bool StopsFire(SecurityClass securityClass, int safetyLevel, bool targetIsCapsule = false) =>
        RequiredLevel(securityClass, targetIsCapsule) is { } required && safetyLevel > required;

    /// <summary>Whether a pilot with the safety full (the level every pilot has until it sets another) is stopped.</summary>
    public static bool StopsFire(SecurityClass securityClass) => StopsFire(securityClass, SafetyLevel.Full);
}
