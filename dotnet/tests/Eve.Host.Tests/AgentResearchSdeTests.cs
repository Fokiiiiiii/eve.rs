using Eve.Host;
using Eve.Services.Core;
using Eve.Sde;
using Npgsql;
using Xunit;

namespace Eve.Host.Tests;

/// <summary>The R&amp;D research fields from the pinned SDE: each datacore names its skill by dogma attribute 182.</summary>
public sealed class AgentResearchSdeTests
{
    [SdeAgentPgFact]
    public async Task The_fields_are_the_science_specialties_with_their_datacores_from_the_pinned_sde()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ToConnectionString(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.True(await sde.BuildAsync() is not null, "no SDE is loaded: run `cargo run -p eve-sde -- ensure --pg $EVE_PG_URL`");

        var fields = await PersistenceAgentResearch.LoadFieldsAsync(sde, CancellationToken.None);

        Assert.Equal(24, fields.Count);
        Assert.Equal(fields.Count, fields.Select(field => field.SkillTypeId).Distinct().Count());
        Assert.Contains(fields, field => field.SkillTypeId == 11455 && field.DatacoreTypeId == 20414 && field.DatacoreName == "Datacore - Quantum Physics");
        Assert.DoesNotContain(fields, field => field.SkillTypeId == ResearchRules.ScienceSkillTypeId);
    }
}
