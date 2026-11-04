using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public class AgentResearchFieldsTests
{
    private const int ResearchAgentType = (int)AgentAccessRule.ResearchAgentType;
    private const int OtherAgentType = 2;

    [Fact]
    public void Each_research_agent_offers_its_own_skills_and_no_other_agents_fields()
    {
        var fields = AgentResearchFields.FieldsByAgent(
            [Datacore(11001, skill: 11442), Datacore(11002, skill: 11443)],
            [Agent(1, ResearchAgentType, 11443, 11442), Agent(2, ResearchAgentType, 11443)]);

        Assert.Equal(new long[] { 11442, 11443 }, fields[1].Select(field => field.SkillTypeId));
        Assert.Equal(new long[] { 11443 }, fields[2].Select(field => field.SkillTypeId));
    }

    [Fact]
    public void An_agent_of_another_type_offers_no_research_fields()
    {
        var fields = AgentResearchFields.FieldsByAgent(
            [Datacore(11001, skill: 11442)],
            [Agent(3, OtherAgentType, 11442)]);

        Assert.DoesNotContain(3, fields.Keys);
    }

    [Fact]
    public void Only_a_published_group_333_type_is_a_datacore()
    {
        var fields = AgentResearchFields.FieldsByAgent(
            [Datacore(11001, skill: 11442, published: false), Datacore(11002, skill: 11442, group: 334)],
            [Agent(1, ResearchAgentType, 11442)]);

        var field = Assert.Single(fields[1]);
        Assert.Equal(0, field.DatacoreTypeId);
    }

    [Fact]
    public void When_two_datacores_name_one_skill_the_higher_type_id_wins()
    {
        var fields = AgentResearchFields.FieldsByAgent(
            [Datacore(11003, skill: 11442, name: "Datacore - Later"), Datacore(11001, skill: 11442, name: "Datacore - Earlier")],
            [Agent(1, ResearchAgentType, 11442)]);

        var field = Assert.Single(fields[1]);
        Assert.Equal(11003, field.DatacoreTypeId);
        Assert.Equal("Datacore - Later", field.DatacoreName);
    }

    [Fact]
    public void A_skill_with_no_datacore_stays_a_field_and_the_fields_are_in_ascending_order()
    {
        var fields = AgentResearchFields.FieldsByAgent(
            [Datacore(11001, skill: 11444)],
            [Agent(1, ResearchAgentType, 11444, 11442, 11442)]);

        Assert.Equal(new long[] { 11442, 11444 }, fields[1].Select(field => field.SkillTypeId));
        Assert.Equal(0, fields[1][0].DatacoreTypeId);
        Assert.Equal(11001, fields[1][1].DatacoreTypeId);
    }

    [Fact]
    public void A_research_agent_with_no_skills_is_absent()
    {
        var fields = AgentResearchFields.FieldsByAgent([], [Agent(1, ResearchAgentType)]);

        Assert.Empty(fields);
    }

    private static AgentResearchTypeRow Datacore(long typeId, long skill, bool published = true, long group = 333, string name = "Datacore - Test") =>
        new(typeId, name, group, published, skill);

    private static AgentResearchAgentRow Agent(long agentId, int agentType, params long[] skills) =>
        new(agentId, agentType, skills);
}
