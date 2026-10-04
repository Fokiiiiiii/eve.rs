using Eve.Crimewatch;
using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// The already-resolved facts that decide whether a targeted ship attack can cause a Crimewatch
/// consequence. War and limited-engagement values must describe active permissions for this pair.
/// </summary>
public readonly record struct WeaponTargetContext(
    bool AttackerIsPlayer,
    bool TargetIsPlayerProperty,
    bool TargetIsCapsule = false,
    bool SamePilot = false,
    bool TargetIsCriminal = false,
    bool TargetIsSuspect = false,
    bool TargetIsOutlaw = false,
    bool ActiveWar = false,
    bool ActiveLimitedEngagement = false,
    bool LegalFriendlyFire = false);

/// <summary>
/// The targeted ship-weapon safety gate ported from EVE.js <c>evaluateOffensiveAggression</c>.
/// Safety blocks only an unlawful player-on-player attack; legal flags, war, limited engagements,
/// and attacks involving NPCs do not trigger this gate.
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

    /// <summary>Whether Crimewatch safety stops an attack using the resolved attacker, target, and legal-permission facts.</summary>
    public static bool StopsFire(SecurityClass securityClass, int safetyLevel, WeaponTargetContext target)
    {
        if (!target.AttackerIsPlayer || !target.TargetIsPlayerProperty || target.SamePilot
            || target.TargetIsCriminal || target.TargetIsSuspect || target.TargetIsOutlaw
            || target.ActiveWar || target.ActiveLimitedEngagement || target.LegalFriendlyFire)
        {
            return false;
        }

        return StopsFire(securityClass, safetyLevel, target.TargetIsCapsule);
    }

    /// <summary>Whether a pilot with this safety level is stopped from firing at the ship of another pilot in a system of this class.</summary>
    public static bool StopsFire(SecurityClass securityClass, int safetyLevel, bool targetIsCapsule = false) =>
        RequiredLevel(securityClass, targetIsCapsule) is { } required
            && SafetyLevel.Normalize(safetyLevel) > required;

    /// <summary>Whether a pilot with the safety full (the level every pilot has until it sets another) is stopped.</summary>
    public static bool StopsFire(SecurityClass securityClass) => StopsFire(securityClass, SafetyLevel.Full);
}
