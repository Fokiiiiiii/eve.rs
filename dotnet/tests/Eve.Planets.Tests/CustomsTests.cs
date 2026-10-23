using Eve.Planets;

namespace Eve.Planets.Tests;

public class PlanetSurveyTests
{
    private static readonly int[] Raw = [2267, 2268, 2270, 2272, 2286, 2287, 2288, 2305, 2306, 2307, 2308, 2309, 2310, 2311];

    private sealed class Field(double value) : IResourceField
    {
        public double ValueAt(long planetId, int resourceTypeId, double theta, double phi) => value;
    }

    [Fact]
    public void A_planet_carries_four_distinct_raw_resources_the_same_way_each_time()
    {
        var first = PlanetSurvey.ResourcesOf(40486618, Raw);
        Assert.Equal(PlanetSurvey.ResourcesPerPlanet, first.Count);
        Assert.Equal(first.Distinct().Count(), first.Count);
        Assert.All(first, type => Assert.Contains(type, Raw));
        Assert.Equal(first, PlanetSurvey.ResourcesOf(40486618, Raw));
        Assert.Empty(PlanetSurvey.ResourcesOf(40486618, []));
    }

    [Fact]
    public void The_quality_is_the_field_on_the_client_display_scale_and_the_maximum_shows_as_full()
    {
        var max = PlanetRules.MaxResourceValue;
        // the client divides by planetResourceMaxValue * 255 * 0.5: a field at its maximum is the display's full
        var full = PlanetSurvey.QualityOf(new Field(max), 1, 2268) / (max * 255 * 0.5);
        Assert.Equal(1.0, full, 9);
        Assert.Equal(0.5 * max * 255 * 0.5, PlanetSurvey.QualityOf(new Field(0.5 * max), 1, 2268), 9);
    }
}

public class CustomsTests
{
    private static Colony LaunchpadColony()
    {
        // the launchpad needs 3600 CPU: a level 5 command center (25415 CPU) is needed for it
        var colony = Fixtures.NewColony();
        colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx());
        colony.ApplyCommands([new UpgradeCommandCenterCommand(1, 5)], Fixtures.Ctx(ccSkill: 5));
        colony.ApplyCommands([new CreatePinCommand(2, TestCatalog.Launchpad, 0.5, 1.01)], Fixtures.Ctx());
        return colony;
    }

    [Theory]
    [InlineData(0.858, 0, 0.10)]
    [InlineData(0.858, 5, 0.05)]
    [InlineData(0.858, 9, 0.05)]
    [InlineData(0.2, 5, 0.17)]
    public void The_office_rate_is_the_npc_rate_less_the_skill_in_high_security(double security, int skill, double rate) =>
        Assert.Equal(rate, Customs.Rate(security, skill), 9);

    [Theory]
    [InlineData(10.0, 0.01)]
    [InlineData(5.0, 0.02)]
    [InlineData(0.0, 0.03)]
    [InlineData(-5.0, 0.04)]
    [InlineData(-10.0, 0.05)]
    public void An_owner_rate_uses_the_highest_reached_standing_band(double standing, double expected)
    {
        var rates = new Customs.OwnerTaxRates(0.06, 0.07, 0.01, 0.02, 0.03, 0.04, 0.05);
        Assert.Equal(expected, rates.TierRate(standing), 9);
    }

    [Fact]
    public void Corporation_and_alliance_rates_take_precedence_over_standing()
    {
        var rates = new Customs.OwnerTaxRates(0.06, 0.07, 0.01, 0.02, 0.03, 0.04, 0.05);
        Assert.Equal(0.06, rates.ForPilot(sameCorporation: true, sameAlliance: true, standing: -10), 9);
        Assert.Equal(0.07, rates.ForPilot(sameCorporation: false, sameAlliance: true, standing: -10), 9);
        Assert.Equal(0.05, rates.ForPilot(sameCorporation: false, sameAlliance: false, standing: -10), 9);
    }

    [Theory]
    [InlineData(0.10, 0.02, 0.12)]
    [InlineData(0.05, 0.02, 0.07)]
    [InlineData(0.17, 0.02, 0.19)]
    [InlineData(0.10, 1.0, 1.0)]
    public void Npc_and_owner_rates_combine_without_exceeding_one(double npc, double owner, double expected) =>
        Assert.Equal(expected, Customs.CombinedRate(npc, owner), 9);

    [Fact]
    public void The_fees_are_quantity_times_the_pad_rate_times_the_commodity_multiplier_times_the_office_rate()
    {
        var catalog = TestCatalog.Build();
        var (import, export) = Customs.Taxes(catalog, TestCatalog.Launchpad,
            new Dictionary<int, long> { [TestCatalog.AqueousLiquids] = 100 }, new Dictionary<int, long> { [TestCatalog.Water] = 20 }, 0.10);
        // import: 100 x 0.5 x 5 x 0.10 = 25; export: 20 x 1.0 x 400 x 0.10 = 800
        Assert.Equal(25, import, 9);
        Assert.Equal(800, export, 9);
    }

    [Fact]
    public void A_launchpad_takes_a_commodity_from_the_office_and_gives_one_back()
    {
        var colony = LaunchpadColony();
        colony.ImportToLaunchpad(2, TestCatalog.Water, 50);
        Assert.Equal(50, colony.Pins[2].Contents[TestCatalog.Water]);
        colony.ExportFromLaunchpad(2, TestCatalog.Water, 20);
        Assert.Equal(30, colony.Pins[2].Contents[TestCatalog.Water]);
        Assert.Equal("RouteFailedValidationExpeditedSourceLacksCommodityQty",
            Assert.Throws<PlanetRefusal>(() => colony.ExportFromLaunchpad(2, TestCatalog.Water, 31)).Key);
    }

    [Fact]
    public void An_import_that_does_not_fit_the_launchpad_is_refused()
    {
        var colony = LaunchpadColony();
        // 60000 units of water are 11400 m3 (0.19 each): the launchpad holds 10000 m3
        Assert.Equal("CannotImportCapacityExceeded",
            Assert.Throws<PlanetRefusal>(() => colony.ImportToLaunchpad(2, TestCatalog.Water, 60000)).Key);
        Assert.Empty(colony.Pins[2].Contents);
    }
}
