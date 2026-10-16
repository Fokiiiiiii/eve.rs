namespace Eve.Skills;

/// <summary>
/// The daily downtime (11:00 UTC) that resets the Daily Alpha Injector: one use per downtime, and the next one is due at the first downtime
/// after the use (EveJS <c>resolveNextDowntimeFileTime</c>, skillTradingAuthority.js).
/// </summary>
public static class SkillInjectorDowntime
{
    public const int HourUtc = 11;
    private const long DayMs = 86_400_000;

    /// <summary>The first downtime strictly after the moment (Unix milliseconds).</summary>
    public static long NextAfterMs(long unixMs)
    {
        var boundary = (unixMs / DayMs) * DayMs + HourUtc * 3_600_000L;
        return unixMs >= boundary ? boundary + DayMs : boundary;
    }

    /// <summary>The last downtime at or before the moment (Unix milliseconds): a use before it is spent.</summary>
    public static long PreviousAtOrBeforeMs(long unixMs) => NextAfterMs(unixMs) - DayMs;
}
