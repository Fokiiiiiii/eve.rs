namespace Eve.FactionWarfare.Tests;

public sealed class FwNpcStationTaxTests
{
    [Theory]
    [InlineData(FwRules.TaxModifiedStation, FwFactions.GuristasPirates, 0.1)]
    [InlineData(FwRules.TaxModifiedStation, FwFactions.AngelCartel, 0.1)]
    [InlineData(FwRules.TaxModifiedStation, FwFactions.AmarrEmpire, 1.0)]
    [InlineData(FwRules.TaxModifiedStation, 0L, 1.0)]
    [InlineData(60000001L, FwFactions.GuristasPirates, 1.0)]
    public void The_npc_station_tax_factor_is_the_client_table_value_or_one(long station, long warFaction, double factor) =>
        Assert.Equal(factor, FwRules.NpcStationTaxModifier(station, warFaction), 12);

    [Fact]
    public void The_factor_scales_a_facility_tax_as_the_client_applies_it()
    {

        var baseTax = 0.05m;
        Assert.Equal(0.005m, baseTax * (decimal)FwRules.NpcStationTaxModifier(FwRules.TaxModifiedStation, FwFactions.GuristasPirates));
    }
}
