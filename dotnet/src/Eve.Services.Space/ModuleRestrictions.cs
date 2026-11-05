using Eve.Universe;

namespace Eve.Services.Space;

/// <summary>
/// Where a module may be used. A port, as it is, of <c>dogma/moduleRestrictions.py</c> of the client (V24.01, <c>Fokiiiiiii/eve-logic</c>): a module whose
/// type says it is not allowed in empire space (<c>disallowInEmpireSpace</c>) or in high security (<c>disallowInHighSec</c>) cannot be activated there, unless
/// the system is fully corrupted (a Triglavian invasion) and the type says it may be used in such a system.
/// </summary>
public static class ModuleRestrictions
{
    public const int AttributeDisallowInEmpireSpace = 1074;
    public const int AttributeDisallowInHighSec = 1970;
    public const int AttributeAllowInFullyCorruptedLowSec = 5599;
    public const int AttributeAllowInFullyCorruptedHighSec = 5600;

    /// <summary>The names of the user errors the client has words for.</summary>
    public const string CantInEmpireSpace = "CantInEmpireSpace";
    public const string CantInHighSecSpace = "CantInHighSecSpace";

    /// <summary><c>disallowActivateOnWarp</c>: the module cannot be activated while its ship aligns to warp (the SDE's wording: "Stops the module from being activated if the ship is aligning to warp").</summary>
    public const int AttributeDisallowActivateOnWarp = 1245;

    /// <summary>The refusal reason (not a client user error) of a module that may not be activated while its ship warps.</summary>
    public const string WarpBlockedReason = "MODULE_WARP_BLOCKED";

    /// <summary>
    /// The refusal of a module that is not allowed on warp, while the ship aligns to warp or is in it (<paramref name="warping"/>, the host's warp state: a warp is
    /// requested until the ship arrives, stops or leaves). Null when the module may be activated.
    /// </summary>
    public static string? CheckWarp(Func<int, double> attribute, bool warping) =>
        warping && attribute(AttributeDisallowActivateOnWarp) != 0 ? WarpBlockedReason : null;

    /// <summary>
    /// <c>CheckAllowedInCurrentSecurityClass</c>: the name of the user error the activation is refused with, or null when the module may be used in a system of this
    /// class. <paramref name="attribute"/> gives the module's value of an attribute (0 when it has none).
    /// </summary>
    public static string? Check(SecurityClass securityClass, Func<int, double> attribute, bool fullyCorrupted = false)
    {
        if (Security.IsLowSec(securityClass))
        {
            return Disallowed(attribute, AttributeDisallowInEmpireSpace, AttributeAllowInFullyCorruptedLowSec, fullyCorrupted) ? CantInEmpireSpace : null;
        }
        if (Security.IsHighSecOrHigher(securityClass))
        {
            if (Disallowed(attribute, AttributeDisallowInEmpireSpace, AttributeAllowInFullyCorruptedHighSec, fullyCorrupted))
            {
                return CantInEmpireSpace;
            }
            if (Disallowed(attribute, AttributeDisallowInHighSec, AttributeAllowInFullyCorruptedHighSec, fullyCorrupted))
            {
                return CantInHighSecSpace;
            }
        }
        return null;
    }

    private static bool Disallowed(Func<int, double> attribute, int disallowing, int allowedException, bool fullyCorrupted) =>
        attribute(disallowing) != 0 && !(fullyCorrupted && attribute(allowedException) != 0);
}
