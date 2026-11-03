namespace Eve.Npc;

/// <summary>
/// The NPC kill security-status recovery formula and four-digit equality check used by the server.
/// </summary>
public static class NpcSecurityStatusRecovery
{
    /// <summary>The EVE.js <c>ATTRIBUTE_FAST_TALK_PERCENTAGE</c> dogma attribute.</summary>
    public const int FastTalkPercentageAttributeId = 359;

    /// <summary>NPC bounty recovery is available for the pirate factions represented by the NPC profile data.</summary>
    public static bool IsEligibleFaction(int factionId) => factionId is 500010 or 500011 or 500012 or 500019 or 500020;

    private const double ModifierPerIsk = 1.0 / 100_000_000.0;
    private const double ModifierCap = 0.0025;
    private const double MinimumStatus = -10.0;
    private const double FormulaTargetStatus = 10.0;
    private const double MaximumNpcRecoveryStatus = 5.0;
    private const int EqualityDigits = 4;

    /// <summary>
    /// Computes one NPC recovery, or null when the bounty gives no gain, the NPC limit is reached, or
    /// the value rounded to four decimal places is unchanged. The caller selects one NPC per five-minute window.
    /// </summary>
    public static double? TryNext(decimal bounty, double previousStatus, double fastTalkMultiplier)
    {
        var amount = NpcBounty.Money(Math.Max(0m, bounty));
        var previous = double.IsFinite(previousStatus)
            ? Math.Clamp(previousStatus, MinimumStatus, FormulaTargetStatus)
            : 0.0;
        var fastTalk = double.IsFinite(fastTalkMultiplier)
            ? Math.Max(0.0, fastTalkMultiplier)
            : 1.0;
        var baseModifier = Math.Min(ModifierCap, (double)amount * ModifierPerIsk);
        var modifier = baseModifier * fastTalk;
        if (!(modifier > 0))
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
