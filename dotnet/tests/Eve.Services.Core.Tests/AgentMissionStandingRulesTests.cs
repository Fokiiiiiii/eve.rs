using Eve.Agents;
using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class AgentMissionStandingRulesTests
{
    private const long AgentId = 3008416;
    private const long CorporationId = 1000001;

    private static AgentMissionAgentData Agent(long level = 1, long type = AgentMissionKindRules.TypeBasicAgent,
        string missionKind = "courier", bool importantMission = false) => new(
        AgentId, type, AgentMissionKindRules.DivisionDistribution, level, 60000001, CorporationId,
        false, false, null, importantMission, missionKind, missionKind, null, null, null, 30000001, null, null, null);

    private static AgentMissionContent Content(string title = "Courier Assignment") =>
        new(5001, 0, title, title, false);

    [Theory]
    [InlineData(1, -0.01)]
    [InlineData(2, -0.014)]
    [InlineData(3, -0.02)]
    [InlineData(4, -0.028)]
    [InlineData(5, -0.036)]
    public void A_second_decline_uses_the_reference_agent_and_corporation_raw_changes(long level, double agentLoss)
    {
        var gains = AgentMissionStandingRules.DeclinePenaltyFor(Agent(level), Content());

        Assert.Equal(new MissionStandingGain(CorporationId, agentLoss / 10, true, "Courier Assignment"), gains[0]);
        Assert.Equal(new MissionStandingGain(AgentId, agentLoss, true, "Courier Assignment"), gains[1]);
    }

    [Fact]
    public void A_mining_mission_uses_the_distribution_decline_curve()
    {
        var gains = AgentMissionStandingRules.DeclinePenaltyFor(Agent(missionKind: "mining"), Content("Mining Assignment"));

        Assert.Equal(new MissionStandingGain(AgentId, -0.01, true, "Mining Assignment"), gains[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void An_agent_level_outside_the_reference_reward_table_has_no_fallback(long level)
    {
        var gains = AgentMissionStandingRules.DeclinePenaltyFor(Agent(level), Content());

        Assert.Empty(gains);
    }

    [Fact]
    public void An_important_mission_agent_uses_the_level_decline_curve()
    {
        var gains = AgentMissionStandingRules.DeclinePenaltyFor(Agent(type: AgentMissionKindRules.TypeStorylineMissionAgent,
            missionKind: "encounter", importantMission: true), Content("Important Encounter"));

        Assert.Equal(new MissionStandingGain(AgentId, -0.01, true, "Important Encounter"), gains[1]);
    }

    [Fact]
    public void A_decline_loss_moves_the_standing_down_by_the_raw_change_fraction()
    {
        var loss = AgentMissionStandingRules.DeclinePenaltyFor(Agent(), Content()).Last();

        Assert.Equal(-0.15, loss.DeltaFrom(5.0), 10);
    }
}
