using Eve.Planets;

namespace Eve.Planets.Tests;

public class ValidationTests
{
    private static string Refusal(Action action)
    {
        var ex = Assert.Throws<PlanetRefusal>(action);
        return ex.Key;
    }

    [Fact]
    public void Nothing_but_a_command_center_can_be_placed_first()
    {
        var colony = Fixtures.NewColony();
        Assert.Equal("CannotManagePlanetWithoutCommandCenter",
            Refusal(() => colony.ApplyCommands([new CreatePinCommand(2, TestCatalog.Storage, 0.5, 1.0)], Fixtures.Ctx())));
    }

    [Fact]
    public void A_second_command_center_is_refused()
    {
        var colony = Fixtures.NewColony();
        colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx());
        Assert.Equal("CannotBuildMultipleCommandPins",
            Refusal(() => colony.ApplyCommands([new CreatePinCommand(9, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx())));
    }

    [Fact]
    public void A_pin_that_needs_another_planet_type_is_refused()
    {
        var colony = Fixtures.NewColony();
        // a temperate command center (restriction 11) on a barren planet (2016)
        Assert.Equal("CannotCreatePinWrongPlanetType",
            Refusal(() => colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.TemperateCommandCenter, 0.5, 1.0)], Fixtures.Ctx())));
    }

    [Fact]
    public void A_second_planet_needs_one_planet_per_consolidation_level()
    {
        var colony = Fixtures.NewColony();
        Assert.Equal("CannotPlaceCommandCenterNotEnoughSkill",
            Refusal(() => colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx(planets: 1, consolidation: -1))));
        Assert.Equal("CannotPlaceCommandCenterNotEnoughSkill",
            Refusal(() => colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx(planets: 2, consolidation: 1))));
        colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx(planets: 1, consolidation: 1));
        Assert.NotNull(colony.CommandCenter);
    }

    [Fact]
    public void A_command_center_upgrade_needs_the_skill_and_costs_the_level_difference()
    {
        var colony = Fixtures.NewColony();
        colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx());
        Assert.Equal("CantUpgradeCommandCenterSkillRequired",
            Refusal(() => colony.ApplyCommands([new UpgradeCommandCenterCommand(1, 2)], Fixtures.Ctx(ccSkill: 1))));
        var outcome = colony.ApplyCommands([new UpgradeCommandCenterCommand(1, 2)], Fixtures.Ctx(ccSkill: 2));
        Assert.Equal(1510000, outcome.IskCost);
        Assert.Equal(2, colony.Level);
    }

    [Fact]
    public void A_pin_costs_its_base_price_and_a_short_wallet_refuses_the_batch()
    {
        var colony = Fixtures.NewColony();
        colony.ApplyCommands([new CreatePinCommand(1, TestCatalog.CommandCenter, 0.5, 1.0)], Fixtures.Ctx());
        Assert.Equal("CannotBuildNotEnoughCash",
            Refusal(() => colony.ApplyCommands([new CreatePinCommand(2, TestCatalog.Storage, 0.5, 1.0)], Fixtures.Ctx(balance: 1))));
    }

    [Fact]
    public void A_head_outside_the_area_of_influence_is_refused_and_the_heads_are_capped()
    {
        // power caps a 1-planet colony at level 0 (6000 PG): ten heads need the level-5 command center (19000 PG)
        var colony = Fixtures.Chain();
        colony.ApplyCommands([new UpgradeCommandCenterCommand(1, 5)], Fixtures.Ctx(ccSkill: 5));
        // heads cannot change while a program runs (the client refuses it too): clear the program after its install cooldown
        colony.RunTo(colony.CurrentSimTime + PlanetRules.ProgramInstallCooldown);
        colony.ApplyCommands([new InstallProgramCommand(3, null, 0.01)], Fixtures.Ctx());
        Assert.Equal("CannotPlaceHeadTooFarAway",
            Refusal(() => colony.ApplyCommands([new AddExtractorHeadCommand(3, 1, 0.5, 2.0)], Fixtures.Ctx())));
        for (var i = 1; i < 10; i++)
            colony.ApplyCommands([new AddExtractorHeadCommand(3, i, 0.5, 1.0)], Fixtures.Ctx());
        Assert.Equal(10, colony.Pins[3].Heads.Count);
        Assert.Equal("CannotPlaceHeadLimitReached",
            Refusal(() => colony.ApplyCommands([new AddExtractorHeadCommand(3, 11, 0.5, 1.0)], Fixtures.Ctx())));
    }

    [Fact]
    public void A_route_that_makes_more_than_the_pin_produces_is_refused()
    {
        var colony = Fixtures.Chain();
        Assert.Equal("CreateRouteCommodityProductionTooSmall",
            Refusal(() => colony.ApplyCommands([new CreateRouteCommand(50, [3, 2], TestCatalog.AqueousLiquids, 10_000_000)], Fixtures.Ctx())));
    }

    [Fact]
    public void A_storage_route_that_needs_more_bandwidth_than_its_link_is_refused()
    {
        var colony = Fixtures.Chain();
        // 200000 units a 1800 s cycle: 0.005 m3 * 200000 * 3600 / 1800 = 2000 m3/h, the link carries 1250 m3/h
        Assert.Equal("RouteFailedValidationCannotRouteCommodities",
            Refusal(() => colony.ApplyCommands([new CreateRouteCommand(50, [2, 4], TestCatalog.AqueousLiquids, 200_000)], Fixtures.Ctx())));
    }

    [Fact]
    public void A_storage_cannot_route_to_a_storage()
    {
        var colony = Fixtures.Chain();
        Assert.Equal("CannotRouteStorageMustGoToConsumer",
            Refusal(() => colony.ApplyCommands([new CreateRouteCommand(50, [2, 1], TestCatalog.AqueousLiquids, 1)], Fixtures.Ctx())));
    }

    [Fact]
    public void Removing_the_command_center_abandons_the_colony()
    {
        var colony = Fixtures.Chain();
        var outcome = colony.ApplyCommands([new RemovePinCommand(1)], Fixtures.Ctx());
        Assert.True(outcome.Abandoned);
        Assert.Empty(colony.Pins);
    }

    [Fact]
    public void A_launch_is_refused_inside_the_cooldown_and_takes_the_commodities()
    {
        var colony = Fixtures.Chain();
        colony.Pins[1].Contents[TestCatalog.Water] = 50;
        colony.Launch(1, new Dictionary<int, long> { [TestCatalog.Water] = 5 });
        Assert.Equal(45, colony.Pins[1].Contents[TestCatalog.Water]);
        Assert.Equal("CannotLaunchCommandPinNotReady",
            Refusal(() => colony.Launch(1, new Dictionary<int, long> { [TestCatalog.Water] = 5 })));
        colony.RunTo(colony.CurrentSimTime + PlanetRules.LaunchCooldown);
        colony.Launch(1, new Dictionary<int, long> { [TestCatalog.Water] = 5 });
        Assert.Equal(40, colony.Pins[1].Contents[TestCatalog.Water]);
    }

    [Fact]
    public void A_program_cannot_be_changed_or_stopped_inside_its_install_cooldown()
    {
        // Chain installs the program at the colony's start: a stop or a new program is refused until the cooldown ends (ecuPin.CanInstallProgram)
        var colony = Fixtures.Chain();
        Assert.Equal("CantInstallProgramNeedsCooldown",
            Refusal(() => colony.ApplyCommands([new InstallProgramCommand(3, null, 0.01)], Fixtures.Ctx())));
        Assert.Equal("CantInstallProgramNeedsCooldown",
            Refusal(() => colony.ApplyCommands([new InstallProgramCommand(3, TestCatalog.AqueousLiquids, 0.02)], Fixtures.Ctx())));
        Assert.Equal(TestCatalog.AqueousLiquids, colony.Pins[3].ProgramType);
        colony.RunTo(colony.CurrentSimTime + PlanetRules.ProgramInstallCooldown - 1);
        Assert.Equal("CantInstallProgramNeedsCooldown",
            Refusal(() => colony.ApplyCommands([new InstallProgramCommand(3, null, 0.01)], Fixtures.Ctx())));
        colony.RunTo(colony.CurrentSimTime + 1);
        colony.ApplyCommands([new InstallProgramCommand(3, null, 0.01)], Fixtures.Ctx());
        Assert.Null(colony.Pins[3].ProgramType);
    }

    [Fact]
    public void A_game_master_program_starts_at_once_with_its_lifetime_and_ignores_the_cooldown()
    {
        var colony = Fixtures.Chain();
        colony.ApplyCommands([new GmForceInstallProgramCommand(3, TestCatalog.AqueousLiquids, PlanetRules.Sec * 60, 2 * PlanetRules.Hour, 100, 1.0)], Fixtures.Ctx());
        Assert.Equal(TestCatalog.AqueousLiquids, colony.Pins[3].ProgramType);
        Assert.Equal(100, colony.Pins[3].QtyPerCycle);
        Assert.Equal(PlanetRules.Sec * 60, colony.Pins[3].CycleTime);
        Assert.Equal(colony.CurrentSimTime + 2 * PlanetRules.Hour, colony.Pins[3].ExpiryTime);
        Assert.Equal("InvalidCommand", Refusal(() => colony.ApplyCommands([new GmForceInstallProgramCommand(3, TestCatalog.AqueousLiquids, 0, PlanetRules.Hour, 1, 1.0)], Fixtures.Ctx())));
    }

    [Fact]
    public void An_expedited_transfer_takes_the_minimum_transfer_time_and_locks_the_source()
    {
        var colony = Fixtures.Chain();
        colony.Pins[2].Contents[TestCatalog.AqueousLiquids] = 3000;
        var (time, sourceRun) = colony.ExpeditedTransfer([2, 4], new Dictionary<int, long> { [TestCatalog.AqueousLiquids] = 3000 });
        Assert.Equal(Fixtures.Now, time);
        Assert.Equal(Fixtures.Now + 5 * PlanetRules.Min, sourceRun);
        Assert.Equal(3000, colony.Pins[4].Contents[TestCatalog.AqueousLiquids]);
        colony.Pins[2].Contents[TestCatalog.AqueousLiquids] = 10;
        colony.Pins[4].Contents.Clear();
        Assert.Equal("RouteFailedValidationExpeditedSourceNotReady",
            Refusal(() => colony.ExpeditedTransfer([2, 4], new Dictionary<int, long> { [TestCatalog.AqueousLiquids] = 1 })));
    }
}
