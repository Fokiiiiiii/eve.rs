using Eve.Data;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>The R&amp;D research rate, the project slots and the qualifying skills, as the EVE University wiki states them.</summary>
public sealed class AgentResearchSdeTests
{
    // The pinned SDE (build 3396210): research agent 3009318 offers skills 11448 and 11450, and the published group 333 datacores
    // 20417 and 20410 name them (dogma attribute 182).
    [Fact]
    public async Task The_research_fields_of_an_agent_are_its_sde_skills_linked_to_their_datacores()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var fields = await PersistenceAgentResearch.LoadFieldsAsync(new SdeReader(source), CancellationToken.None);

        var agent = fields[3009318];
        Assert.Equal(new long[] { 11448, 11450 }, agent.Select(field => field.SkillTypeId));
        Assert.Equal(new long[] { 20417, 20410 }, agent.Select(field => field.DatacoreTypeId));
    }

    [Fact]
    public async Task The_pinned_sde_has_244_research_agents_and_each_offers_fields()
    {
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var fields = await PersistenceAgentResearch.LoadFieldsAsync(new SdeReader(source), CancellationToken.None);

        Assert.Equal(244, fields.Count);
        Assert.All(fields.Values, agent => Assert.NotEmpty(agent));
    }
}
