using Eve.Fitting;

namespace Eve.Fitting.Tests;

/// <summary>The Strategic Cruiser's four subsystem classes: what it lacks, and when its fit is complete (SF-4; EveJS <c>liveFittingState.js</c>).</summary>
public class StrategicCruiserRulesTests
{
    [Fact]
    public void A_hull_with_one_subsystem_of_each_class_is_complete_and_any_other_fit_says_what_is_missing_or_duplicated()
    {
        Assert.Null(StrategicCruisers.IncompleteFit([125, 126, 127, 128]));
        Assert.Equal([127, 128], StrategicCruisers.Missing([125, 126]));
        Assert.Contains("Missing: Offensive, Propulsion.", StrategicCruisers.IncompleteFit([125, 126]));
        Assert.Contains("Remove duplicate", StrategicCruisers.IncompleteFit([125, 126, 127, 128, 126]));
        Assert.Contains("Missing: Defensive.", StrategicCruisers.IncompleteFit([125, 125, 127, 128]));
    }

    [Fact]
    public void A_ship_has_at_most_the_four_subsystem_classes_whatever_its_maximum_says()
    {
        Assert.Equal(new[] { 125, 126, 127, 128 }, StrategicCruisers.Flags);
    }
}
