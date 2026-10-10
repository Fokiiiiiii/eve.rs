using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

/// <summary>
/// The jump drive's rules as the wiki states them (range, fuel, the activation cooldown and the fatigue, the field kinds). The wiki's carrier example is the check of
/// the fatigue order.
/// </summary>
public class JumpDriveRulesTests
{
    private const long Minute = 60_000;

    [Fact]
    public void Calibration_adds_twenty_percent_of_the_base_range_a_level_so_level_five_doubles_it()
    {
        Assert.Equal(3.5, JumpDriveRules.Range(3.5, 0));
        Assert.Equal(7.0, JumpDriveRules.Range(3.5, 5));
    }

    [Fact]
    public void Fuel_is_light_years_times_the_fuel_per_light_year_less_ten_percent_a_level_of_conservation_rounded_up()
    {
        Assert.Equal(9_000, JumpDriveRules.FuelQuantity(3, 3_000, 0, 0));
        Assert.Equal(4_500, JumpDriveRules.FuelQuantity(3, 3_000, 5, 0));
        // a jump freighter's own skill takes the same share again
        Assert.Equal(23_500, JumpDriveRules.FuelQuantity(5, 9_400, 0, 5));
        Assert.Equal(4, JumpDriveRules.FuelQuantity(1.0001, 3, 0, 0));
    }

    [Fact]
    public void The_wiki_carrier_example_gives_the_cooldowns_and_the_fatigue_it_states()
    {
        // first jump, five light years: a cooldown of six minutes and fatigue of an hour
        var first = JumpDriveRules.AfterJump(0, 5);
        Assert.Equal((60 * Minute, 6 * Minute), first);

        // six minutes on, the second jump: the fatigue is five hours (the cap), the cooldown six minutes
        var second = JumpDriveRules.AfterJump(54 * Minute, 5);
        Assert.Equal((JumpDriveRules.FatigueCapMs, 6 * Minute), second);

        // the third, from four hours fifty-four minutes: a tenth of that fatigue is 29 min 24 s, which the rule gives (the wiki's example says 30 min)
        var third = JumpDriveRules.AfterJump(4 * 60 * Minute + 54 * Minute, 5);
        Assert.Equal((JumpDriveRules.FatigueCapMs, 1_764_000), third);
    }

    [Fact]
    public void The_cooldown_and_the_fatigue_are_capped_at_thirty_minutes_and_five_hours()
    {
        Assert.Equal((JumpDriveRules.FatigueCapMs, JumpDriveRules.CooldownCapMs), JumpDriveRules.AfterJump(0, 100));
    }

    [Fact]
    public void A_black_ops_hull_counts_a_quarter_of_the_distance_for_its_fatigue()
    {
        // the attribute 1971 multiplier of 0.25 is applied by the host before the rule; here the rule gets 1.25 light years
        Assert.Equal((1_350_000, 135_000), JumpDriveRules.AfterJump(0, 5 * 0.25));
    }

    [Fact]
    public void A_covert_field_takes_only_black_ops_and_an_industrial_field_takes_freighters_capital_industrials_and_black_ops()
    {
        const int Dreadnought = 485, BlackOps = 898, JumpFreighter = 902, CapitalIndustrial = 883;
        Assert.True(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Covert, BlackOps));
        Assert.False(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Covert, Dreadnought));
        Assert.True(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Industrial, JumpFreighter));
        Assert.True(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Industrial, CapitalIndustrial));
        Assert.True(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Industrial, BlackOps));
        Assert.False(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Industrial, Dreadnought));
        Assert.True(JumpDriveRules.MayJumpTo(JumpDriveRules.FieldKind.Standard, Dreadnought));
    }

    [Fact]
    public void The_field_type_of_the_sde_gives_its_kind()
    {
        Assert.Equal(JumpDriveRules.FieldKind.Standard, JumpDriveRules.KindOf(21_094));
        Assert.Equal(JumpDriveRules.FieldKind.Covert, JumpDriveRules.KindOf(28_650));
        Assert.Equal(JumpDriveRules.FieldKind.Industrial, JumpDriveRules.KindOf(52_696));
    }
}
