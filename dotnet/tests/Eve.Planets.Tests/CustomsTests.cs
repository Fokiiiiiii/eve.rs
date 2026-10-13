using Eve.Planets;

namespace Eve.Planets.Tests;

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
