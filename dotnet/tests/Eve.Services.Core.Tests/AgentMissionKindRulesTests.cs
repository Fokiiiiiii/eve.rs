using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

/// <summary>The eve.rs rule for an agent's mission kind and importance (AgentMissionKindRules).</summary>
public sealed class AgentMissionKindRulesTests
{
    [Theory]
    [InlineData(2, 24, "encounter", false)]
    [InlineData(2, 22, "courier", false)]
    [InlineData(2, 23, "mining", false)]
    [InlineData(6, 24, "encounter", true)]
    [InlineData(7, 22, "courier", true)]
    [InlineData(10, 24, "epicArc", true)]
    [InlineData(4, 18, "research", false)]
    [InlineData(9, 24, "encounter", false)]
    [InlineData(13, 37, "heraldry", false)]
    public void A_named_type_and_division_give_the_kind_the_rule_states(long typeId, long divisionId, string kind, bool important)
    {
        Assert.Equal(new AgentMissionClass(kind, important), AgentMissionKindRules.Classify(typeId, divisionId));
    }

    [Theory]
    [InlineData(2, 18)]   // R&D is no mission division on the wiki's Agent page
    [InlineData(1, 22)]   // non-agent
    [InlineData(5, 24)]   // CONCORD: the wiki does not describe it
    [InlineData(8, 22)]   // event
    [InlineData(12, 25)]  // career: no specific mission type on the wiki page
    [InlineData(99, 24)]  // a type the SDE does not name
    [InlineData(2, 99)]   // a division the rule does not name
    public void A_type_or_division_the_rule_does_not_name_gets_no_kind(long typeId, long divisionId)
    {
        Assert.Equal(new AgentMissionClass("", false), AgentMissionKindRules.Classify(typeId, divisionId));
    }

    [Fact]
    public void The_division_names_are_the_security_distribution_and_mining_ids_of_the_sde()
    {
        Assert.Equal("encounter", AgentMissionKindRules.DivisionMissionKind(AgentMissionKindRules.DivisionSecurity));
        Assert.Equal("courier", AgentMissionKindRules.DivisionMissionKind(AgentMissionKindRules.DivisionDistribution));
        Assert.Equal("mining", AgentMissionKindRules.DivisionMissionKind(AgentMissionKindRules.DivisionMining));
        Assert.Null(AgentMissionKindRules.DivisionMissionKind(AgentMissionKindRules.DivisionResearchAndDevelopment));
    }
}
