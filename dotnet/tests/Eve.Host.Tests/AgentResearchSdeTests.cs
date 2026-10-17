using Eve.Host;
using Eve.Services.Core;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>The R&amp;D research fields from the pinned SDE: each datacore names its skill by dogma attribute 182.</summary>
public sealed class AgentResearchSdeTests
{
    /// <summary>The pinned SDE (build 3396210): research agent 3009318 offers skills 11448 and 11450, which datacores 20417 and 20410 name.</summary>
    [SdeAgentPgFact]
    public async Task The_fields_of_an_agent_are_its_sde_skills_each_with_the_datacore_that_names_it()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");

        var fields = await PersistenceAgentResearch.LoadFieldsAsync(sde, CancellationToken.None);

        var agent = fields[3009318];
        Assert.Equal(new long[] { 11448, 11450 }, agent.Select(field => field.SkillTypeId));
        Assert.Equal(new long[] { 20417, 20410 }, agent.Select(field => field.DatacoreTypeId));
    }

    /// <summary>The pinned SDE has 244 research agents (npcCharacters with agentTypeID 4); each offers at least one field.</summary>
    [SdeAgentPgFact]
    public async Task The_pinned_sde_has_244_research_agents_and_each_offers_fields()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var fields = await PersistenceAgentResearch.LoadFieldsAsync(new SdeReader(source), CancellationToken.None);

        Assert.Equal(244, fields.Count);
        Assert.All(fields.Values, agent => Assert.NotEmpty(agent));
    }
}
