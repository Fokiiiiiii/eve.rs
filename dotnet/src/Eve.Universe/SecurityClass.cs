namespace Eve.Universe;

/// <summary>How safe a solar system is, in the four classes the rules are written for.</summary>
public enum SecurityClass
{
    ZeroSec = 0,
    LowSec = 1,
    HighSec = 2,
    SafeSec = 3,
}

/// <summary>
/// The security classes and what is asked of them. A port, as it is, of <c>eveuniverse/security.py</c> of the client (V24.01, <c>Fokiiiiiii/eve-logic</c>): the
/// class of a system comes from its security status as the game has it, not as the map rounds it (a system of 0.45 is high security).
/// </summary>
public static class Security
{
    /// <summary><c>SecurityClassFromLevel</c>: null security up to 0.0, low security below 0.45, high security below 0.95, safe security above.</summary>
    public static SecurityClass ClassFromLevel(double level) =>
        level <= 0.0 ? SecurityClass.ZeroSec
        : level < 0.45 ? SecurityClass.LowSec
        : level < 0.95 ? SecurityClass.HighSec
        : SecurityClass.SafeSec;

    public static bool IsSafeSec(SecurityClass securityClass) => securityClass == SecurityClass.SafeSec;

    public static bool IsHighSecOrHigher(SecurityClass securityClass) => securityClass >= SecurityClass.HighSec;

    public static bool IsLowSec(SecurityClass securityClass) => securityClass == SecurityClass.LowSec;

    public static bool IsLowSecOrLower(SecurityClass securityClass) => securityClass <= SecurityClass.LowSec;

    public static bool IsLowSecOrHigher(SecurityClass securityClass) => securityClass >= SecurityClass.LowSec;

    public static bool IsZeroSec(SecurityClass securityClass) => securityClass == SecurityClass.ZeroSec;

    /// <summary><c>SECURITY_CLASS_TO_TEXT</c>.</summary>
    public static string Text(SecurityClass securityClass) => securityClass switch
    {
        SecurityClass.ZeroSec => "Null Security",
        SecurityClass.LowSec => "Low Security",
        SecurityClass.HighSec => "High Security",
        SecurityClass.SafeSec => "Safe Security",
        _ => "Unknown Security Class",
    };
}
