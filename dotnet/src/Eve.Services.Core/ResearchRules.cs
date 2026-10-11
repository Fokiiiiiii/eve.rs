namespace Eve.Services.Core;

/// <summary>
/// The R&amp;D research rules (agents track, AG-3 / A6). Source: EVE University wiki, "Research missions" (also "Datacore farming"):
/// RP/day = (1 + (20 + 5N + AS) / 100) * (SS + AL)^2, where N is the Negotiation level, AS the effective personal standing
/// with the agent (the Connections bonus applies), SS the specialty science level and AL the agent's level. Check: a level IV
/// agent with Negotiation V, standing 10 and specialty V gives (1 + 55/100) * 9^2 = 125.55, the wiki's stated maximum.
/// Science V and a specialty at the agent's level qualify; a character may run as many projects as 1 + Research Project
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

    /// <summary>RP per day of a project: the formula above.</summary>
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

    /// <summary>A specialty qualifies when Science is at level V and the specialty is trained to the agent's level.</summary>
    public static bool Qualifies(int scienceLevel, int specialtyLevel, int agentLevel) =>
        scienceLevel >= ScienceRequiredLevel && specialtyLevel >= agentLevel;
}
