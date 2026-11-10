using Eve.Data;
using Eve.Services.Space.Sites;
using Eve.Sde;
using Npgsql;

namespace Eve.Services.Space.Tests;

public sealed class SdeSiteAnchorsTests
{
    [PgFact]
    public async Task Batch_anchor_reads_match_the_per_system_sde_rows_and_order()
    {
        var url = Environment.GetEnvironmentVariable("EVE_PG_URL")!;
        await using var source = NpgsqlDataSource.Create(PgConnection.ForTest(url));
        var anchors = new SdeSiteAnchors(new SdeReader(source));
        int[] systems = [30_000_208, 30_000_021, 30_000_142];

        var batch = await anchors.ForSystemsAsync(systems);

        Assert.Equal(systems.Order(), batch.Keys.Order());
        foreach (var system in systems)
        {
            var expected = await anchors.OfAsync(system);
            Assert.Equal(expected.Select(anchor => anchor.Id), batch[system].Select(anchor => anchor.Id));
            for (var index = 0; index < expected.Count; index++)
                Assert.Equal(expected[index].Position, batch[system][index].Position);
        }
    }
}
