namespace Eve.Services.Core;

/// <summary>

/// RP/day = (1 + (20 + 5N + AS) / 100) * (SS + AL)^2, where N is the Negotiation level, AS the effective personal standing



/// Management level; a datacore costs 100 RP and 10,000 ISK. CCP publishes no formula (the community source is the only one).
/// </summary>
public static class ResearchRules
{
    public const int NegotiationSkillTypeId = 3356;
    public const int ScienceSkillTypeId = 3402;
    public const int ScienceRequiredLevel = 5;
    public const int ResearchProjectManagementSkillTypeId = 12179;
    public const long DatacoreResearchPoints = 100;
    public const long DatacoreIsk = 10_000;

    /// <summary>


    /// </summary>
    public static double PointsPerDay(int negotiationLevel, double effectiveStanding, int specialtyLevel, int agentLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(negotiationLevel);
        ArgumentOutOfRangeException.ThrowIfNegative(specialtyLevel);
        ArgumentOutOfRangeException.ThrowIfNegative(agentLevel);
        var multiplier = 1.0 + (20.0 + 5.0 * negotiationLevel + effectiveStanding) / 100.0;
        return Math.Max(0.0, multiplier * Math.Pow(specialtyLevel + agentLevel, 2));
    }

    /// <summary>The number of projects a character may run at once: one, and one more per Research Project Management level.</summary>
    public static int MaxConcurrentProjects(int projectManagementLevel) => 1 + Math.Max(0, projectManagementLevel);


    public static bool Qualifies(int scienceLevel, int specialtyLevel, int agentLevel) =>
        scienceLevel >= ScienceRequiredLevel && specialtyLevel >= agentLevel;
}
