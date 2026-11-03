namespace Eve.Npc;

/// <summary>
/// The NPC kill security-status recovery formula and four-digit equality check used by the server.
/// </summary>
public static class NpcSecurityStatusRecovery
{
    /// <summary>The EVE.js <c>ATTRIBUTE_FAST_TALK_PERCENTAGE</c> dogma attribute.</summary>
    public const int FastTalkPercentageAttributeId = 359;

    /// <summary>NPC security-status recovery is available for the pirate factions represented by the NPC profile data.</summary>
    public static bool IsEligibleFaction(int factionId) => factionId is 500010 or 500011 or 500012 or 500019 or 500020;

    private const double MinimumStatus = -10.0;
    private const double FormulaTargetStatus = 10.0;
    private const double MaximumNpcRecoveryStatus = 5.0;
    private const int EqualityDigits = 4;

    /// <summary>
    /// Computes one NPC recovery, or null when the NPC has no security status bonus, the NPC limit is reached, or
    /// the value rounded to four decimal places is unchanged. The caller selects one NPC per five-minute window.
    /// </summary>
    public static double? TryNext(double securityStatusKillBonusPercent, double previousStatus, double fastTalkMultiplier)
    {
        var previous = double.IsFinite(previousStatus)
            ? Math.Clamp(previousStatus, MinimumStatus, FormulaTargetStatus)
            : 0.0;
        var fastTalk = double.IsFinite(fastTalkMultiplier)
            ? Math.Max(0.0, fastTalkMultiplier)
            : 1.0;
        var baseModifier = double.IsFinite(securityStatusKillBonusPercent)
            ? Math.Max(0, securityStatusKillBonusPercent) / 100.0
            : 0.0;
        var modifier = baseModifier * fastTalk;
        if (!(modifier > 0) || !double.IsFinite(modifier))
        {
            return null;
        }

        var next = Math.Min(MaximumNpcRecoveryStatus, previous + ((FormulaTargetStatus - previous) * modifier));
        return next <= previous || RoundNumber(next, EqualityDigits) == RoundNumber(previous, EqualityDigits)
            ? null
            : next;
    }

    // bountyRuntime.js: roundNumber(n, digits) => Math.round(n * 10**digits) / 10**digits.
    private static double RoundNumber(double value, int digits)
    {
        var factor = Math.Pow(10, digits);
        return Math.Floor(value * factor + 0.5) / factor;
    }
}
