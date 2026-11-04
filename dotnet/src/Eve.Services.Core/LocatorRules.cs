namespace Eve.Services.Core;


public enum LocateDistance
{
    SameSystem = 0,
    SameConstellation = 1,
    SameRegion = 2,
    OtherRegion = 3,
}

/// <summary>



/// only prices a locate. A level-one locator cannot locate beyond its constellation and a level-two one cannot locate in another region (the table's "cannot").
/// </summary>
public static class LocatorRules
{
    private static readonly long?[,] Fees =
    {
        // same system, same constellation, same region, other region
        { 1_000, 5_000, null, null },
        { 5_000, 10_000, 25_000, null },
        { 10_000, 25_000, 50_000, 100_000 },
        { 25_000, 50_000, 100_000, 250_000 },
        { 50_000, 100_000, 250_000, 500_000 },
    };

    private static readonly int[] CooldownMinutes = [5, 5, 15, 30, 60];

    /// <summary>The faction standing a locator of this level needs (level one: any standing).</summary>
    private static readonly double?[] RequiredStanding = [null, 1.0, 3.0, 5.0, 7.0];

    /// <summary>The fee in ISK for a locate at this distance, or null when this level cannot locate that far.</summary>
    public static long? Fee(int agentLevel, LocateDistance distance)
    {
        ValidateLevel(agentLevel);
        return Fees[agentLevel - 1, (int)distance];
    }

    /// <summary>The cooldown after a locate, in minutes.</summary>
    public static int CooldownMinutesFor(int agentLevel)
    {
        ValidateLevel(agentLevel);
        return CooldownMinutes[agentLevel - 1];
    }

    /// <summary>The faction standing needed to use this locator, or null for any standing.</summary>
    public static double? RequiredStandingFor(int agentLevel)
    {
        ValidateLevel(agentLevel);
        return RequiredStanding[agentLevel - 1];
    }


    public static LocateDistance Classify(long agentSystemId, long agentConstellationId, long agentRegionId,
        long targetSystemId, long targetConstellationId, long targetRegionId)
    {
        if (agentSystemId == targetSystemId) return LocateDistance.SameSystem;
        if (agentConstellationId == targetConstellationId) return LocateDistance.SameConstellation;
        if (agentRegionId == targetRegionId) return LocateDistance.SameRegion;
        return LocateDistance.OtherRegion;
    }


    public static bool IsWormhole(long solarSystemId) => solarSystemId is >= 31_000_000 and < 32_000_000;

    private static void ValidateLevel(int agentLevel)
    {
        if (agentLevel is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(agentLevel), "a locator agent is level 1 to 5");
    }
}
