using Eve.Sde;
using Npgsql;

namespace Eve.Universe.Tests;

/// <summary>The wormhole types from the pinned SDE (build 3396210): the numbers the rules use, checked against known types.</summary>
public sealed class WormholeTypeCatalogTests
{
    [PgFact]
    public async Task The_catalog_holds_the_129_wormhole_types_with_their_dogma_numbers()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var types = await WormholeTypeCatalog.LoadAsync(new SdeReader(source));

        Assert.Equal(129, types.Count);
        Assert.False(types.ContainsKey(30831), "K162 is the generic exit and carries no wormhole mass attribute");
        Assert.Equal(new WormholeType(30661, 2, 960, 1_000_000_000, 0, 62_000_000, 297), types[30661]);
        Assert.Equal(new WormholeType(30579, 1, 960, 100_000_000, 0, 62_000_000, 296), types[30579]);
        Assert.Equal(new WormholeType(30702, 5, 1440, 3_300_000_000, 0, 2_000_000_000, 300), types[30702]);
    }

    [PgFact]
    public async Task Thera_shattered_drifter_and_pochven_exits_have_their_target_classes()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var types = await WormholeTypeCatalog.LoadAsync(new SdeReader(source));

        Assert.Equal(12, types[34372].TargetSystemClass);
        Assert.Equal(13, types[34439].TargetSystemClass);
        Assert.Equal(new[] { 14, 15, 16, 17, 18 }, new[] { 35650, 35651, 35652, 35653, 35654 }.Select(t => types[t].TargetSystemClass));
        Assert.Equal(25, types[56542].TargetSystemClass);
        Assert.Equal(new WormholeType(56026, -1, 720, 1_000_000_000, 0, 410_000_000, 2925), types[56026]);
    }
}
