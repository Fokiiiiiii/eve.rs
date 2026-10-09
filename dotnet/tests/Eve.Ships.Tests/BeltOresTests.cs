namespace Eve.Ships.Tests;

/// <summary>The ores an asteroid belt holds by its system's true security, and the units a seeded rock holds.</summary>
public class BeltOresTests
{
    [Theory]
    [InlineData(1.0, "Veldspar,Scordite")]
    [InlineData(0.9, "Veldspar,Scordite")]
    [InlineData(0.8, "Veldspar,Scordite,Pyroxeres")]
    [InlineData(0.7, "Veldspar,Scordite,Pyroxeres,Plagioclase")]
    [InlineData(0.5, "Veldspar,Scordite,Pyroxeres,Plagioclase")]
    [InlineData(0.4, "Pyroxeres,Kernite,Jaspet,Omber")]
    [InlineData(0.2, "Pyroxeres,Kernite,Jaspet,Omber,Hemorphite,Hedbergite")]
    [InlineData(0.0, "Kernite,Pyroxeres")]
    [InlineData(-0.4, "Kernite,Pyroxeres")]
    [InlineData(-0.5, "Kernite,Pyroxeres,Bistot")]
    [InlineData(-0.55, "Kernite,Pyroxeres,Bistot")]
    [InlineData(-0.6, "Kernite,Pyroxeres,Bistot,Arkonor")]
    [InlineData(-0.9, "Kernite,Pyroxeres,Bistot,Arkonor")]
    public void Each_security_band_holds_the_ores_of_the_eve_university_belt_rows(double security, string ores) =>
        Assert.Equal(ores.Split(','), BeltOres.For(security));

    [Fact]
    public void A_seeded_rock_holds_its_volume_in_units_at_least_one()
    {
        Assert.Equal(150_000L, BeltOres.UnitsFor(0.1));
        Assert.Equal(1L, BeltOres.UnitsFor(1_000_000d));
    }
}
