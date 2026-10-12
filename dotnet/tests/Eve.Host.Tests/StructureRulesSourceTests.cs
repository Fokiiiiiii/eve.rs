using Eve.Sde;
using Eve.Structures;
using Npgsql;

namespace Eve.Host.Tests;

/// <summary>The timeline's rules from the pinned static data: the size from the type's rigSize, the band from the system (SDE build 3396210).</summary>
public sealed class StructureRulesSourceTests
{
    [PgFact]
    public async Task The_size_comes_from_the_type_and_the_band_from_the_system()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var rules = await StructureRulesSource.LoadAsync(sde);

        Assert.Equal(StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec), rules.RulesOf(35832, 30000142));
        Assert.Equal(StructureRules.Of(StructureSize.Large, SecurityBand.LowSec), rules.RulesOf(35833, 30000012));
        Assert.Equal(StructureRules.Of(StructureSize.ExtraLarge, SecurityBand.NullSec), rules.RulesOf(35834, 30000208));
        Assert.Equal(StructureRules.Of(StructureSize.Flex, SecurityBand.Wormhole), rules.RulesOf(35841, 31000001));
        Assert.Equal(StructureRules.Of(StructureSize.Medium, SecurityBand.Pochven), rules.RulesOf(35832, 30000021));
    }

    [PgFact]
    public async Task A_type_or_a_system_without_static_data_is_an_error_and_not_a_guess()
    {
        await using var source = NpgsqlDataSource.Create(Eve.Data.PgConnection.ForTest(Environment.GetEnvironmentVariable("EVE_PG_URL")!));
        var sde = new SdeReader(source);
        Assert.NotNull(await sde.BuildAsync());
        var rules = await StructureRulesSource.LoadAsync(sde);

        Assert.Throws<InvalidOperationException>(() => rules.RulesOf(1, 30000142));
        Assert.Throws<InvalidOperationException>(() => rules.RulesOf(35832, 1));
    }
}
