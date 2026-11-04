namespace Eve.Services.Core.Tests;

/// <summary>The client's access rule (eveCfg.CanUseAgent), table-driven: thresholds per level, the floor, research, aura and the skill bonuses.</summary>
public class AgentAccessRuleTests
{
    private const long NoFaction = 500004; // a faction with no skill exception and no pirate rule
    private const long AngelCartel = 500011;
    private const long Drifters = 500024;

    private static bool Use(long level, long type, double faction, double corporation, double character, long factionId = NoFaction,
        IReadOnlyDictionary<int, int>? skills = null) =>
        AgentAccessRule.CanUse(level, type, new AgentStandings(faction, corporation, character), factionId,
            skill => skills is not null && skills.TryGetValue(skill, out var found) ? found : 0);

    [Theory]
    [InlineData(2, 1.0, true)]
    [InlineData(2, 0.9, false)]
    [InlineData(3, 3.0, true)]
    [InlineData(3, 2.9, false)]
    [InlineData(4, 5.0, true)]
    [InlineData(4, 4.9, false)]
    [InlineData(5, 7.0, true)]
    [InlineData(5, 6.9, false)]
    public void The_best_of_the_three_standings_must_reach_the_level_threshold(long level, double standing, bool expected)
    {
        // The best of the three standings must reach the threshold (eveCfg.py:139).
        Assert.Equal(expected, Use(level, 2, faction: standing, corporation: 0, character: 0));
        Assert.Equal(expected, Use(level, 2, faction: 0, corporation: 0, character: standing));
    }

    [Fact]
    public void A_level_one_agent_is_open_to_anyone_except_research_agents()
    {
        Assert.True(Use(1, 2, -10, -10, -10));
        Assert.False(Use(1, AgentAccessRule.ResearchAgentType, -10, -10, -10));
        Assert.True(Use(1, AgentAccessRule.ResearchAgentType, 0, 0, 0));
    }

    [Fact]
    public void Any_standing_below_minus_two_refuses_the_agent_even_when_another_standing_is_high()
    {
        Assert.False(Use(2, 2, faction: 10, corporation: -2, character: 10));
        Assert.True(Use(2, 2, faction: 10, corporation: -1.9, character: 10));
    }

    [Fact]
    public void A_research_agent_is_refused_when_the_corporation_standing_is_below_its_threshold_minus_two()
    {
        // At level 2 the threshold is 1, so the research floor is -1: a corporation standing of -1.5 is refused.
        Assert.False(Use(2, AgentAccessRule.ResearchAgentType, faction: 5, corporation: -1.5, character: 5));
        Assert.True(Use(2, AgentAccessRule.ResearchAgentType, faction: 5, corporation: -0.5, character: 5));
    }

    [Fact]
    public void An_aura_agent_ignores_the_rule()
    {
        Assert.True(Use(5, AgentAccessRule.AuraAgentType, -10, -10, -10));
    }

    // The faction and corporation standings are kept negative in these tests: a 0 standing takes the Connections bonus too

    [Fact]
    public void A_connections_skill_raises_a_positive_standing_by_its_bonus()
    {
        // 0.9 with Connections 5 (bonus 2.0): 10 * (1 - 0.91 * 0.8) = 2.72, which reaches the level-two threshold.
        Assert.True(Use(2, 2, -1, -1, 0.9, skills: new Dictionary<int, int> { [AgentAccessRule.ConnectionsSkill] = 5 }));
        Assert.False(Use(2, 2, -1, -1, 0.9));
    }

    [Fact]
    public void A_diplomacy_skill_lifts_a_negative_standing()
    {
        // -1 with Diplomacy 5 (bonus 2.0): 10 * (1 - 1.1 * 0.8) = 1.2, which reaches the level-two threshold.
        Assert.True(Use(2, 2, -1, -1, -1, skills: new Dictionary<int, int> { [AgentAccessRule.DiplomacySkill] = 5 }));
        Assert.False(Use(2, 2, -1, -1, -1, skills: new Dictionary<int, int> { [AgentAccessRule.ConnectionsSkill] = 5 }));
    }

    [Fact]
    public void A_pirate_faction_takes_the_criminal_connections_bonus_for_a_positive_standing()
    {
        var connectionsOnly = new Dictionary<int, int> { [AgentAccessRule.ConnectionsSkill] = 5 };
        Assert.False(Use(2, 2, -1, -1, 0.9, AngelCartel, connectionsOnly));
        Assert.True(Use(2, 2, -1, -1, 0.9, AngelCartel, new Dictionary<int, int> { [AgentAccessRule.CriminalConnectionsSkill] = 5 }));
    }

    [Fact]
    public void Factions_without_a_skill_bonus_take_none()
    {
        Assert.Null(AgentAccessRule.BonusSkill(0.5, Drifters));
        Assert.False(Use(2, 2, 0, 0, 0.9, Drifters, new Dictionary<int, int> { [AgentAccessRule.ConnectionsSkill] = 5 }));
        Assert.False(Use(2, 2, 0, 0, -1, Drifters, new Dictionary<int, int> { [AgentAccessRule.DiplomacySkill] = 5 }));
    }
}
