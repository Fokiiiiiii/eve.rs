using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public class ResearchRulesTests
{
    [Fact]
    public void The_wiki_maximum_for_a_level_four_agent_is_reproduced_exactly()
    {

        Assert.Equal(125.55, ResearchRules.PointsPerDay(5, 10, 5, 4), 9);
    }

    [Theory]
    [InlineData(0, 0.0, 1, 1, 4.8)]
    [InlineData(2, 4.0, 3, 2, 1.34 * 25)]
    [InlineData(5, 0.0, 5, 3, 1.45 * 64)]
    public void The_rate_is_one_plus_negotiation_standing_over_hundred_times_the_levels_squared(int negotiation, double standing, int specialty, int agent, double expected)
    {
        Assert.Equal(expected, ResearchRules.PointsPerDay(negotiation, standing, specialty, agent), 9);
    }

    [Fact]
    public void The_effective_standing_comes_from_the_connections_bonus_on_the_personal_standing()
    {
        // Connections at V adds 2.0 to the standing scale: 10 * (1 - (1 - s/10)(1 - b/10)) with b = 5 * 0.4.
        var effective = AgentAccessRule.EffectiveStanding(4.0, 500001, skill => skill == AgentAccessRule.ConnectionsSkill ? 5 : 0);
        Assert.Equal(10.0 * (1.0 - (1.0 - 0.4) * (1.0 - 0.2)), effective, 9);
        Assert.Equal(4.0, AgentAccessRule.EffectiveStanding(4.0, 500001, _ => 0), 9);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    public void One_project_slot_and_one_more_per_research_project_management_level(int management, int slots)
    {
        Assert.Equal(slots, ResearchRules.MaxConcurrentProjects(management));
    }

    [Fact]
    public void Science_five_and_the_agents_level_qualify_a_specialty()
    {
        Assert.True(ResearchRules.Qualifies(5, 3, 3));
        Assert.False(ResearchRules.Qualifies(4, 5, 1));
        Assert.False(ResearchRules.Qualifies(5, 2, 3));
    }

    [Fact]
    public void A_datacore_costs_one_hundred_points_and_ten_thousand_isk()
    {
        Assert.Equal(100L, ResearchRules.DatacoreResearchPoints);
        Assert.Equal(10_000L, ResearchRules.DatacoreIsk);
    }
}
