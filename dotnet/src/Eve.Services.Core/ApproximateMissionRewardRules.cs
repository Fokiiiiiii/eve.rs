namespace Eve.Services.Core;

/// <summary>Report-supplied calibration values for mission LP. Both values are approximate and unconfirmed.</summary>
public sealed record ApproximateMissionRewardConfiguration(
    decimal SystemSecurityFactor = 1.6288m,
    decimal SecurityConnectionsBonusPerLevel = 0.10m);

/// <summary>Formula plumbing for the owner-approved, approximate mission LP calculation.</summary>
public static class ApproximateMissionRewardRules
{
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
