namespace Eve.Npc;

/// <summary>
/// The NPC kill security-status recovery rule from EVE.js v0.12.9
/// <c>server/src/services/bounty/bountyRuntime.js</c> at
/// <c>828823a75f211fdecb945b087a26c225f111491f</c>. The EVE.js 4-digit equality check
/// uses JavaScript <c>Math.round</c>; this method returns the unrounded value the server stores.
/// </summary>
public static class NpcSecurityStatusRecovery
{
    /// <summary>The EVE.js <c>ATTRIBUTE_FAST_TALK_PERCENTAGE</c> dogma attribute.</summary>
    public const int FastTalkPercentageAttributeId = 359;

    private const double ModifierPerIsk = 1.0 / 100_000_000.0;
    private const double ModifierCap = 0.0025;
    private const double MinimumStatus = -10.0;
    private const double MaximumStatus = 10.0;
    private const int EqualityDigits = 4;

    /// <summary>
    /// Computes the next status for one fresh NPC kill, or null when the bounty gives no gain or the
    /// value rounded to four decimal places is unchanged. There is no five-minute aggregation window:
    /// the pinned EVE.js server applies recovery once for each fresh victim reference.
    /// </summary>
    public static double? TryNext(decimal bounty, double previousStatus, double fastTalkMultiplier)
    {
        var amount = NpcBounty.Money(Math.Max(0m, bounty));
        var previous = double.IsFinite(previousStatus)
            ? Math.Clamp(previousStatus, MinimumStatus, MaximumStatus)
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

        var next = Math.Min(MaximumStatus, previous + ((MaximumStatus - previous) * modifier));
        return RoundNumber(next, EqualityDigits) == RoundNumber(previous, EqualityDigits)
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
