namespace Eve.Ships.Tests;

/// <summary>
/// The pure mining yield, with explicit rolls. The expected numbers are the EveJS reference's <c>computeMiningResult</c> (miningMath.js)
/// run with the same rolls, except the drone cases, which are the floor rule worked out by hand.
/// </summary>
public class MiningYieldTests
{
    private static MiningCycle Module(double volume, double unitVolume, long asteroid = 1_000_000, double wasteMultiplier = 0, double wasteProbability = 0,
        double critMultiplier = 0, double critProbability = 0, double clamp = 1, double efficiency = 1) =>
        new(MiningSource.Module, volume, unitVolume, asteroid, wasteMultiplier, wasteProbability, critMultiplier, critProbability, clamp, efficiency);

    private static MiningCycle Drone(double volume, double unitVolume, long asteroid = 1_000_000, double wasteMultiplier = 0, double wasteProbability = 0,
        double critMultiplier = 0, double critProbability = 0, double clamp = 1, double efficiency = 1) =>
        new(MiningSource.Drone, volume, unitVolume, asteroid, wasteMultiplier, wasteProbability, critMultiplier, critProbability, clamp, efficiency);

    private static MiningRolls Rolls(double waste = 0, double normal = 0, double wasteRounding = 0, double crit = 0, double critRounding = 0) =>
        new(waste, normal, wasteRounding, crit, critRounding);

    [Fact]
    public void A_waste_does_not_happen_when_the_roll_is_exactly_the_probability()
    {
        // 50 is not greater than the whole percent 50 of the roll 0.5.
        var result = MiningYield.ComputeMiningResult(Module(1, 0.01, wasteMultiplier: 0.5, wasteProbability: 50), Rolls(waste: 0.5));

        Assert.Equal((100L, 0L, 0d), (result.NormalQuantity, result.WastedQuantity, result.WastedVolume));
    }

    [Fact]
    public void A_waste_happens_when_the_roll_is_just_below_the_probability()
    {
        var result = MiningYield.ComputeMiningResult(Module(1, 0.01, wasteMultiplier: 0.5, wasteProbability: 50), Rolls(waste: 0.49));

        Assert.Equal((100L, 50L, 0.5), (result.NormalQuantity, result.WastedQuantity, result.WastedVolume));
    }

    [Fact]
    public void A_zero_probability_never_wastes_not_even_at_a_zero_roll()
    {
        var result = MiningYield.ComputeMiningResult(Module(1, 0.01, wasteMultiplier: 0.5, wasteProbability: 0), Rolls(waste: 0));

        Assert.Equal((100L, 0L, 0d), (result.NormalQuantity, result.WastedQuantity, result.WastedVolume));
    }

    [Theory]
    [InlineData(0.999999d)]
    [InlineData(1d)]
    public void A_full_probability_wastes_at_the_top_roll_and_a_roll_of_one_is_read_as_the_top_percent(double roll)
    {
        var result = MiningYield.ComputeMiningResult(Module(1, 0.01, wasteMultiplier: 0.5, wasteProbability: 100), Rolls(waste: roll));

        Assert.Equal((100L, 50L), (result.NormalQuantity, result.WastedQuantity));
    }

    [Theory]
    [InlineData(0.5d, 2L)]
    [InlineData(0.49d, 3L)]
    [InlineData(0d, 3L)]
    [InlineData(1d, 2L)]
    public void A_normal_yield_rounds_up_only_when_the_remainder_is_greater_than_the_roll(double roll, long expected)
    {
        // 1.25 m3 of 0.5 m3 units is 2.5 units: the remainder is 0.5.
        var result = MiningYield.ComputeMiningResult(Module(1.25, 0.5), Rolls(normal: roll));

        Assert.Equal(expected, result.NormalQuantity);
    }

    [Theory]
    [InlineData(0.98d, 1L)]
    [InlineData(0.995d, 0L)]
    public void A_volume_just_under_one_unit_rounds_to_one_or_zero_by_the_roll(double roll, long expected)
    {
        // 0.0099 m3 of 0.01 m3 units is 0.99 units.
        var result = MiningYield.ComputeMiningResult(Module(0.0099, 0.01), Rolls(normal: roll));

        Assert.Equal(expected, result.NormalQuantity);
    }

    [Theory]
    [InlineData(0.4d, 2L)]
    [InlineData(0.5d, 1L)]
    public void A_waste_rounds_by_its_own_roll(double wasteRounding, long expected)
    {
        // 2 m3 mined at 0.375 waste is 0.75 m3, which is 1.5 units of 0.5 m3.
        var result = MiningYield.ComputeMiningResult(Module(2, 0.5, wasteMultiplier: 0.375, wasteProbability: 100), Rolls(wasteRounding: wasteRounding));

        Assert.Equal((4L, expected, 0.75), (result.NormalQuantity, result.WastedQuantity, result.WastedVolume));
    }

    [Theory]
    [InlineData(0.4d, 2L)]
    [InlineData(0.5d, 1L)]
    public void A_critical_bonus_is_rounded_by_its_own_roll(double critRounding, long expected)
    {
        // 3 units times 0.5 is 1.5 units of bonus.
        var result = MiningYield.ComputeMiningResult(Module(1.5, 0.5, critMultiplier: 0.5, critProbability: 0.25), Rolls(crit: 0.24, critRounding: critRounding));

        Assert.Equal((true, expected, expected * 0.5), (result.Critical, result.CriticalQuantity, result.CriticalVolume));
    }

    [Fact]
    public void A_critical_yield_does_not_fire_when_the_roll_equals_the_chance()
    {
        var result = MiningYield.ComputeMiningResult(Module(1.5, 0.5, critMultiplier: 0.5, critProbability: 0.25), Rolls(crit: 0.25));

        Assert.Equal((3L, false, 0L), (result.NormalQuantity, result.Critical, result.CriticalQuantity));
    }

    [Fact]
    public void A_chance_of_one_fires_below_the_top_roll_and_misses_at_a_roll_of_one()
    {
        var fires = MiningYield.ComputeMiningResult(Module(4, 0.5, critMultiplier: 1, critProbability: 1), Rolls(crit: 0.999999));
        var misses = MiningYield.ComputeMiningResult(Module(4, 0.5, critMultiplier: 1, critProbability: 1), Rolls(crit: 1));

        Assert.Equal((true, 8L), (fires.Critical, fires.CriticalQuantity));
        Assert.Equal((false, 0L), (misses.Critical, misses.CriticalQuantity));
    }

    [Fact]
    public void A_critical_yield_needs_something_mined_first()
    {
        var result = MiningYield.ComputeMiningResult(Module(0, 0.5, critMultiplier: 2, critProbability: 1), Rolls(crit: 0));

        Assert.Equal((0L, false, 0L), (result.NormalQuantity, result.Critical, result.CriticalQuantity));
    }

    [Fact]
    public void A_critical_bonus_is_extra_to_the_hold_and_is_not_taken_from_the_rock()
    {
        var result = MiningYield.ComputeMiningResult(Module(5, 0.5, asteroid: 10, critMultiplier: 1, critProbability: 1), Rolls(crit: 0));

        Assert.Equal((10L, 0L, 10L, 10L, 20L), (result.NormalQuantity, result.WastedQuantity, result.CriticalQuantity, result.DepletedQuantity, result.TransferredQuantity));
    }

    [Fact]
    public void A_cargo_clamp_halves_the_volume_mined()
    {
        var result = MiningYield.ComputeMiningResult(Module(2, 0.5, clamp: 0.5), Rolls());

        Assert.Equal((0.5d, 1d, 2L), (result.ClampFactor, result.NormalVolume, result.NormalQuantity));
    }

    [Fact]
    public void A_half_efficiency_halves_the_normal_yield_and_scales_the_waste()
    {
        // 2 m3 at efficiency 0.5 is 2 units; the waste is 2 * 0.5 * 0.5 = 0.5 m3, which is 1 unit.
        var result = MiningYield.ComputeMiningResult(Module(2, 0.5, wasteMultiplier: 0.5, wasteProbability: 100, efficiency: 0.5), Rolls());

        Assert.Equal((2L, 1L, 0.5), (result.NormalQuantity, result.WastedQuantity, result.WastedVolume));
    }

    [Fact]
    public void A_rock_with_fewer_units_than_the_yield_gives_what_it_has_and_no_waste()
    {
        var result = MiningYield.ComputeMiningResult(Module(5, 0.5, asteroid: 3, wasteMultiplier: 1, wasteProbability: 100), Rolls());

        Assert.Equal((3L, 0L, 3L), (result.NormalQuantity, result.WastedQuantity, result.DepletedQuantity));
    }

    [Fact]
    public void A_waste_takes_only_what_the_rock_has_left_after_the_normal_yield()
    {
        var result = MiningYield.ComputeMiningResult(Module(5, 0.5, asteroid: 12, wasteMultiplier: 1, wasteProbability: 100), Rolls());

        Assert.Equal((10L, 2L, 12L), (result.NormalQuantity, result.WastedQuantity, result.DepletedQuantity));
    }

    [Fact]
    public void A_depleted_rock_gives_nothing_even_with_waste_and_critical_chances()
    {
        var result = MiningYield.ComputeMiningResult(Module(5, 0.5, asteroid: 0, wasteMultiplier: 1, wasteProbability: 100, critMultiplier: 1, critProbability: 1), Rolls());

        Assert.Equal((0L, 0L, false), (result.NormalQuantity, result.WastedQuantity, result.Critical));
    }

    [Fact]
    public void A_zero_volume_and_zero_clamp_give_no_units_and_no_waste()
    {
        var noVolume = MiningYield.ComputeMiningResult(Module(0, 0.5, wasteMultiplier: 1, wasteProbability: 100), Rolls());
        var noClamp = MiningYield.ComputeMiningResult(Module(5, 0.5, clamp: 0, wasteMultiplier: 1, wasteProbability: 100), Rolls());

        Assert.Equal((0L, 0L, 0d, 0d), (noVolume.NormalQuantity, noVolume.WastedQuantity, noVolume.NormalVolume, noVolume.WastedVolume));
        Assert.Equal((0L, 0L, 0d), (noClamp.NormalQuantity, noClamp.WastedQuantity, noClamp.NormalVolume));
    }

    [Fact]
    public void A_zero_waste_multiplier_mines_normally_and_wastes_nothing()
    {
        var result = MiningYield.ComputeMiningResult(Module(5, 0.5, wasteMultiplier: 0, wasteProbability: 100), Rolls());

        Assert.Equal((10L, 0L), (result.NormalQuantity, result.WastedQuantity));
    }

    [Fact]
    public void A_clamp_above_one_and_probability_above_100_are_clamped_as_the_reference_does()
    {
        // The clamp is 1, so 1 m3 is 2 units; the probability is 100, so the roll 0.99 (whole percent 99) wastes 1 m3 = 2 units.
        var result = MiningYield.ComputeMiningResult(Module(1, 0.5, clamp: 1.5, wasteMultiplier: 1, wasteProbability: 150), Rolls(waste: 0.99));

        Assert.Equal((1d, 2L, 2L), (result.ClampFactor, result.NormalQuantity, result.WastedQuantity));
    }

    [Fact]
    public void A_negative_efficiency_is_clamped_to_zero_and_gives_nothing()
    {
        var result = MiningYield.ComputeMiningResult(Module(5, 0.5, efficiency: -1, wasteMultiplier: 1, wasteProbability: 100, critMultiplier: 1, critProbability: 1), Rolls(crit: 0));

        Assert.Equal((0L, 0L, false), (result.NormalQuantity, result.WastedQuantity, result.Critical));
    }

    [Fact]
    public void A_drone_rounds_down_and_never_gets_a_critical_yield_even_at_the_lowest_rolls()
    {
        // 2.9 m3 of 0.5 m3 units is 5.8 units, and the waste is 2.9 units; the rolls would round up for a module.
        var result = MiningYield.ComputeMiningResult(Drone(2.9, 0.5, wasteMultiplier: 0.5, wasteProbability: 100, critMultiplier: 0.5, critProbability: 1), Rolls());

        Assert.Equal((5L, 2L, false, 0L), (result.NormalQuantity, result.WastedQuantity, result.Critical, result.CriticalQuantity));
    }

    [Fact]
    public void A_drone_ignores_its_rounding_and_crit_rolls_at_the_top()
    {
        var result = MiningYield.ComputeMiningResult(Drone(2.9, 0.5, wasteMultiplier: 0.5, wasteProbability: 100, critMultiplier: 0.5, critProbability: 1),
            Rolls(waste: 0.999999, normal: 0.999999, wasteRounding: 0.999999, crit: 0, critRounding: 0.999999));

        Assert.Equal((5L, 2L, false), (result.NormalQuantity, result.WastedQuantity, result.Critical));
    }

    [Fact]
    public void A_module_with_the_same_inputs_as_a_drone_rounds_up_and_gets_the_critical_yield()
    {
        var result = MiningYield.ComputeMiningResult(Module(2.9, 0.5, wasteMultiplier: 0.5, wasteProbability: 100, critMultiplier: 0.5, critProbability: 1), Rolls());

        Assert.Equal((6L, 3L, true, 3L), (result.NormalQuantity, result.WastedQuantity, result.Critical, result.CriticalQuantity));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-0.1d)]
    [InlineData(1.0000001d)]
    public void A_roll_outside_zero_to_one_is_refused(double roll)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MiningYield.ComputeMiningResult(Module(1, 0.5), Rolls(waste: roll)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MiningYield.ComputeMiningResult(Module(1, 0.5), Rolls(critRounding: roll)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1d)]
    [InlineData(double.PositiveInfinity)]
    public void A_volume_that_is_not_a_finite_non_negative_number_is_refused(double volume)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MiningYield.ComputeMiningResult(Module(volume, 0.5), Rolls()));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-0.01d)]
    [InlineData(double.NaN)]
    public void A_unit_volume_that_is_not_positive_is_refused(double unitVolume)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MiningYield.ComputeMiningResult(Module(1, unitVolume), Rolls()));
    }

    [Fact]
    public void A_negative_asteroid_quantity_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MiningYield.ComputeMiningResult(Module(1, 0.5, asteroid: -1), Rolls()));
    }
}
