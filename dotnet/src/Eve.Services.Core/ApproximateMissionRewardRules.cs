namespace Eve.Services.Core;

/// <summary>Configurable calibration values for mission LP; the official mission data has no LP base reward.</summary>
public sealed record ApproximateMissionRewardConfiguration(
    decimal SystemSecurityFactor = 1.6288m,
    decimal SecurityConnectionsBonusPerLevel = 0.10m,
    IReadOnlyDictionary<(string Kind, int Level), decimal>? BaseLoyaltyPoints = null)
{
    public decimal BaseLoyaltyPointsFor(string kind, int level)
    {
        var table = BaseLoyaltyPoints ?? ApproximateMissionRewardRules.DefaultBaseLoyaltyPoints;
        return table.GetValueOrDefault((kind, level));
    }
}


public static class ApproximateMissionRewardRules
{
    public static int DivisionConnectionsSkillTypeId(string kind) => kind switch
    {
        "encounter" or "security" => 3895,
        "courier" or "distribution" => 3894,
        "mining" => 3893,
        _ => 0,
    };

    public static IReadOnlyDictionary<(string Kind, int Level), decimal> DefaultBaseLoyaltyPoints { get; } =
        new Dictionary<(string Kind, int Level), decimal>
        {
            [("encounter", 1)] = 105, [("encounter", 2)] = 250, [("encounter", 3)] = 600,
            [("encounter", 4)] = 1450, [("encounter", 5)] = 3500,
            [("courier", 1)] = 53, [("courier", 2)] = 125, [("courier", 3)] = 310,
            [("courier", 4)] = 730, [("courier", 5)] = 1750,
            [("distribution", 1)] = 53, [("distribution", 2)] = 125, [("distribution", 3)] = 310,
            [("distribution", 4)] = 730, [("distribution", 5)] = 1750,
            [("mining", 1)] = 53, [("mining", 2)] = 125, [("mining", 3)] = 310,
            [("mining", 4)] = 730, [("mining", 5)] = 1750,
        };

    public static long LoyaltyPoints(string kind, int level, decimal systemSecurity, int divisionConnectionsLevel,
        ApproximateMissionRewardConfiguration? configuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(divisionConnectionsLevel);
        var settings = configuration ?? new ApproximateMissionRewardConfiguration();
        var basePoints = settings.BaseLoyaltyPointsFor(kind, level);
        if (basePoints <= 0) return 0;
        var displayedSecurity = decimal.Round(systemSecurity, 1, MidpointRounding.AwayFromZero);
        var securityFactor = Math.Max(0.1m, settings.SystemSecurityFactor - displayedSecurity);
        var skillFactor = 1m + settings.SecurityConnectionsBonusPerLevel * Math.Min(5, divisionConnectionsLevel);
        return decimal.ToInt64(decimal.Round(basePoints * securityFactor * skillFactor,
            0, MidpointRounding.AwayFromZero));
    }

    public static decimal LoyaltyPoints(decimal basePoints, decimal systemSecurity, decimal divisionSkillBonus,
        int securityConnectionsLevel, ApproximateMissionRewardConfiguration? configuration = null)
    {
        var settings = configuration ?? new ApproximateMissionRewardConfiguration();
        var securityConnections = 1m + settings.SecurityConnectionsBonusPerLevel * securityConnectionsLevel;
        return basePoints * (settings.SystemSecurityFactor - systemSecurity) * (1m + divisionSkillBonus) * securityConnections;
    }
}

/// <summary>Report-supplied, approximate and unconfirmed per-level contract capacity configuration.</summary>
public sealed record ApproximateContractLimitConfiguration(int AdvancedContractingPerLevel = 4);

/// <summary>The contract system's skill-level limit hook; base capacity remains owned by its contract provider.</summary>
public static class ApproximateContractLimitRules
{
    public static int AdditionalOutstandingContracts(int advancedContractingLevel, ApproximateContractLimitConfiguration? configuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(advancedContractingLevel);
        var settings = configuration ?? new ApproximateContractLimitConfiguration();
        return checked(advancedContractingLevel * settings.AdvancedContractingPerLevel);
    }
}
