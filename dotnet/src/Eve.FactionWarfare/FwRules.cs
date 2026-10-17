namespace Eve.FactionWarfare;

/// <summary>The contest state of a system, from the attacking side's victory points (wiki "Faction Warfare": vulnerable at 75,000 VP).</summary>
public enum ContestState
{
    Stable,
    Contested,
    Vulnerable,
}

/// <summary>
/// The formulas of factional warfare as the V24.01 client computes them (<c>facwarCommon.py</c>, <c>appConst.py</c> 1052-1059), with the
/// project rules that the wiki and the client disagree on recorded in DEVIATIONS "Factional warfare".
/// </summary>
public static class FwRules
{
    /// <summary>The system LP pool at which each upgrade level is reached (<c>facwarSolarSystemUpgradeThresholds</c>).</summary>
    public static IReadOnlyList<long> UpgradeThresholds { get; } = [4, 379, 380, 381, 382, 383, 384];

    public const int MaxUpgradeLevel = 5;

    /// <summary>The LP pool that caps the upgrade (<c>facwarSolarSystemMaxLPPool</c>).</summary>
    public static long MaxLpPool => UpgradeThresholds[^1];

    /// <summary>The minimum LP that can be donated to a system at once (<c>facwarMinLPDonation</c>).</summary>
    public const long MinLpDonation = 10;

    /// <summary>The standing a character or corporation needs to enlist: the client's <c>facwarMinStandingsToJoinDefault</c>, pirates included.</summary>
    public const double MinStandingToEnlist = -0.0001;

    /// <summary>The victory points that make a system vulnerable (wiki "Faction Warfare": 75,000).</summary>
    public const long VulnerabilityPoints = 75_000;

    /// <summary>The cooldown after a character leaves FW by a direct enlistment (wiki: 24 hours; the client checks the same window).</summary>
    public static readonly TimeSpan EnlistmentCooldown = TimeSpan.FromDays(1);

    /// <summary>
    /// The upgrade level of a system from its LP pool (the client's <c>GetLPUpgradeLevel</c>). A pool at or above the cap is level 5. The client
    /// returns -1 for a pool of 1 to 3, which would raise broker fees; the project clamps it to 0 (DEVIATIONS "Factional warfare").
    /// </summary>
    public static int UpgradeLevel(long lpPool)
    {
        if (lpPool <= 0)
            return 0;
        if (lpPool >= MaxLpPool)
            return MaxUpgradeLevel;
        for (var i = 0; i < UpgradeThresholds.Count; i++)
        {
            if (lpPool < UpgradeThresholds[i])
                return Math.Max(0, i - 1);
        }
        return MaxUpgradeLevel;
    }

    /// <summary>
    /// The upgrade level a faction's pilot sees in a system (the client's <c>GetSolarSystemUpgradeLevel</c> with a faction): a system held by an
    /// enemy of that faction gives no benefit.
    /// </summary>
    public static int EffectiveUpgradeLevel(long lpPool, long occupierId, long pilotFactionId)
    {
        if (pilotFactionId != 0 && occupierId != 0 && FwFactions.IsCombatEnemy(pilotFactionId, occupierId))
            return 0;
        return UpgradeLevel(lpPool);
    }

    /// <summary>The broker fee percentage after the upgrade discount: 10 percent less per level (the client's <c>GetAdjustedFeePercentage</c>).</summary>
    public static double AdjustedFeePercentage(double feePercentage, int upgradeLevel) => feePercentage * (1 - 0.1 * upgradeLevel);

    /// <summary>The NPC station industry cost factor after the upgrade discount: 10 percent less per level (the client's <c>BENEFITS_BY_LEVEL</c>).</summary>
    public static double IndustryCostFactor(int upgradeLevel) => 1 - 0.1 * upgradeLevel;

    /// <summary>
    /// The infrastructure hub donation tax of an occupier: <c>5 * p^3</c> on the share p of the zone's points it holds, then
    /// <c>1 - 1 / (1 + raw)</c> rounded to two places (the client's <c>GetDonationTax</c>). Zero for a non-occupier or an empty zone.
    /// </summary>
    public static double DonationTax(long factionId, long zonePoints, long maxZonePoints)
    {
        if (!FwFactions.IsOccupier(factionId) || maxZonePoints <= 0)
            return 0.0;
        var share = zonePoints / (double)maxZonePoints;
        var raw = 5 * Math.Pow(share, 3);
        return Math.Round(1 - 1 / (1 + raw), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Whether a standing meets the enlistment minimum (the client's <c>GetPersonalStandingRequiredToEnlist</c>).</summary>
    public static bool MeetsEnlistmentStanding(double standing) => standing >= MinStandingToEnlist;

    /// <summary>The militia rank of a standing: the whole part of the standing, from 0 to 9 (the client's <c>min(max(int(standing), 0), 9)</c>).</summary>
    public static int RankOf(double standing) => (int)Math.Clamp(Math.Truncate(standing), 0, 9);

    /// <summary>The contest state of a system from the attacking side's victory points.</summary>
    public static ContestState Contest(long attackerPoints) => attackerPoints switch
    {
        <= 0 => ContestState.Stable,
        >= VulnerabilityPoints => ContestState.Vulnerable,
        _ => ContestState.Contested,
    };

    /// <summary>The contested percentage (0 to 100) of a system from the attacking side's victory points.</summary>
    public static int ContestedPercent(long attackerPoints) => (int)Math.Min(100, Math.Max(0, attackerPoints) * 100 / VulnerabilityPoints);

    /// <summary>
    /// The first downtime strictly after <paramref name="nowMs"/>. shortcut: the wiki gives no downtime time; the project uses 11:00 UTC, the
    /// daily downtime of the live service, so an enlistment change takes effect at the next 11:00 UTC.
    /// </summary>
    public static long NextDowntimeMs(long nowMs)
    {
        const long day = 86_400_000L;
        const long downtimeOffset = 11 * 3_600_000L;
        var dayStart = nowMs - Mod(nowMs, day);
        var candidate = dayStart + downtimeOffset;
        return candidate > nowMs ? candidate : candidate + day;
    }

    /// <summary>The UTC day of a unix time in milliseconds: the key of the daily statistics rows.</summary>
    public static int DayOf(long unixMs) => (int)(unixMs / 86_400_000L);

    private static long Mod(long value, long modulus) => ((value % modulus) + modulus) % modulus;
}
