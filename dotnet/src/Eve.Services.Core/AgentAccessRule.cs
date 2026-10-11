namespace Eve.Services.Core;

/// <summary>
/// A character's standings toward an agent's three owners, as the client reads them: the agent's faction, the agent's
/// corporation and the agent itself (eve-logic standingsvc.py:100-104). Each is -10 to +10; an unknown owner is 0.
/// </summary>
public sealed record AgentStandings(double Faction, double Corporation, double Character);

/// <summary>
/// Whether a character may use an agent. A port of the V24.01 client rule (eve-logic eveCfg.py:112-143 <c>CanUseAgent</c>),
/// with the skill bonuses of standingUtil.py:15-43. The rule has no quality term (the client has none; see the design
/// document, section 4.1): the level threshold, the floor of -2, the research rule and the aura bypass are the whole of it.
/// </summary>
public static class AgentAccessRule
{
    /// <summary>SDE agent type 11 (eve-logic appConst.py:79 <c>agentTypeAura</c>): no standing rule.</summary>
    public const long AuraAgentType = 11;

    /// <summary>SDE agent type 4 (eve-logic appConst.py:74 <c>agentTypeResearchAgent</c>).</summary>
    public const long ResearchAgentType = 4;

    /// <summary>Skill type IDs (eve-logic inventorycommon/const.py:1441, 1456, 1474).</summary>
    public const int ConnectionsSkill = 3359;
    public const int DiplomacySkill = 3357;
    public const int CriminalConnectionsSkill = 3361;

    /// <summary>eve-logic appConst.py:618-619 <c>factionsPirates</c>.</summary>
    private static readonly HashSet<long> PirateFactions = [500011, 500010, 500012, 500019, 500020, 500029];

    /// <summary>eve-logic appConst.py:628-632 <c>factionsWhoseStandingsAreNotAffectedBySkillBonuses</c>.</summary>
    private static readonly HashSet<long> FactionsWithoutSkillBonus = [500024, 500025, 500026, 500027];

    /// <summary>
    /// The client's test, with <paramref name="trainedLevelOf"/> giving a skill's trained level (0 when untrained). The
    /// client's effective level also counts implants, which eve.rs does not model yet; the bonus is 0.4 per level.
    /// </summary>
    public static bool CanUse(
        long level,
        long agentTypeId,
        AgentStandings standings,
        long factionId,
        Func<int, int> trainedLevelOf)
    {
        if (agentTypeId == AuraAgentType) return true;
        if (level == 1 && agentTypeId != ResearchAgentType) return true;

        var m = (level - 1) * 2.0 - 1.0;
        var faction = WithBonus(standings.Faction, factionId, trainedLevelOf);
        var corporation = WithBonus(standings.Corporation, factionId, trainedLevelOf);
        var character = WithBonus(standings.Character, factionId, trainedLevelOf);

        if (Math.Max(Math.Max(faction, corporation), character) >= m &&
            Math.Min(Math.Min(faction, corporation), character) > -2.0)
        {
            return !(agentTypeId == ResearchAgentType && corporation < m - 2.0);
        }
        return false;
    }

    /// <summary>The skill a standing's bonus uses, or null when the faction's standings take none (standingUtil.py:26-37).</summary>
    public static int? BonusSkill(double standing, long factionId)
    {
        if (FactionsWithoutSkillBonus.Contains(factionId)) return null;
        if (standing < 0.0) return DiplomacySkill;
        return PirateFactions.Contains(factionId) ? CriminalConnectionsSkill : ConnectionsSkill;
    }

    /// <summary>The standing an agent's skill bonus raises it to (eveCfg.py:130-138): the effective agent standing AS of the research rate.</summary>
    public static double EffectiveStanding(double standing, long factionId, Func<int, int> trainedLevelOf) =>
        WithBonus(standing, factionId, trainedLevelOf);

    /// <summary>eveCfg.py:130-138: the standing raised by its bonus, as 10 * (1 - (1 - s/10)(1 - b/10)).</summary>
    private static double WithBonus(double standing, long factionId, Func<int, int> trainedLevelOf)
    {
        var skill = BonusSkill(standing, factionId);
        if (skill is not { } skillId) return standing;
        var bonus = Math.Max(0, trainedLevelOf(skillId)) * 0.4;
        if (bonus <= 0.0) return standing;
        return (1.0 - (1.0 - standing / 10.0) * (1.0 - bonus / 10.0)) * 10.0;
    }
}
