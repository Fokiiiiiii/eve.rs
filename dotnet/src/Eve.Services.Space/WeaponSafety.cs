using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// The safety of a pilot, for the one case a weapon meets it: aimed at the ship of another pilot. A port of the part of <c>evaluateAggression</c>
/// (<c>server/src/services/security/crimewatchState.js</c> of EVE.js, ADR 0009) that an unarmed war, no standing and no timers leave: a pilot with the
/// safety full (the one every pilot has: nothing sets it yet) cannot start a weapon at another pilot's ship in empire space, where the aggression would make
/// the attacker a criminal (high security: needs the safety off) or a suspect (low security: needs it at partial). Anywhere else (null security) the
/// attack is no crime and the safety lets it be. A ship nobody flies is no pilot's: it may be shot anywhere.
/// </summary>
public static class WeaponSafety
{
    /// <summary>The error of the activation the safety stops (the reference's <c>errorMsg</c>; the client is shown it in <c>Failed to activate …</c>).</summary>
    public const string Refusal = "SafetyActivated";

    /// <summary>Whether a pilot with the safety full is stopped from firing at the ship of another pilot in a system of this class.</summary>
    public static bool StopsFire(SecurityClass securityClass) => Security.IsLowSecOrHigher(securityClass);
}
