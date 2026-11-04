using Eve.Inventory.Mutation;

namespace Eve.Inventory.Tests;

/// <summary>Pure rules of the abyssal mutation. The numbers are the SDE's (mutaplasmid 47297 on 12052, output 47408).</summary>
public class MutationMathTests
{
    private static readonly AttributeRange[] Ranges47297 =
    [
        new(6, 0.6, 1.4, false),
        new(20, 0.9, 1.1, true),
        new(30, 0.8, 1.5, false),
        new(50, 0.8, 1.5, false),
        new(554, 0.7, 1.3, false),
    ];

    /// <summary>Source 50MN Microwarpdrive I (12052): the attributes the worked example uses.</summary>
    private static readonly Dictionary<int, double> Source12052 = new()
    {
        [6] = 180, [9] = 40, [20] = 500, [30] = 150, [50] = 50, [73] = 10000, [182] = 3454, [277] = 1, [554] = 500, [796] = 5000000,
    };

    /// <summary>Output 50MN Abyssal Microwarpdrive (47408): only its skill requirements are used.</summary>
    private static readonly Dictionary<int, double> Output47408 = new() { [182] = 3454, [277] = 4 };

    private static readonly HashSet<int> Skills = [182, 183, 184, 277, 278, 279, 1285, 1286, 1287, 1288, 1289, 1290];

    private static Dictionary<int, double> Rolls(params (int Attribute, double Multiplier)[] pairs) => pairs.ToDictionary(p => p.Attribute, p => p.Multiplier);

    [Fact]
    public void Multiplier_runs_from_the_printed_min_to_the_printed_max()
    {
        var range = new AttributeRange(6, 0.6, 1.4, false);
        Assert.Equal(0.6, MutationMath.MultiplierAt(range, 0), 12);
        Assert.Equal(1.0, MutationMath.MultiplierAt(range, 0.5), 12);
        Assert.Equal(1.4, MutationMath.MultiplierAt(range, 1), 12);
    }

    [Fact]
    public void Inverted_row_maps_fraction_zero_to_the_printed_min_and_stays_inside_the_interval()
    {
        var inverted = new AttributeRange(2346, 1.025, 0.95, false);
        Assert.Equal(1.025, MutationMath.MultiplierAt(inverted, 0), 12);
        Assert.Equal(0.95, MutationMath.MultiplierAt(inverted, 1), 12);
        Assert.Equal(0.9875, MutationMath.MultiplierAt(inverted, 0.5), 12);
    }

    [Fact]
    public void Interval_of_an_inverted_row_is_normalised()
    {
        var inverted = new AttributeRange(2346, 1.025, 0.95, false);
        Assert.Equal(0.95, inverted.Low);
        Assert.Equal(1.025, inverted.High);
        Assert.Equal(1.025, inverted.Min);
        Assert.Equal(0.95, inverted.Max);
    }

    [Theory]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    [InlineData(double.NaN)]
    public void A_fraction_outside_zero_one_is_a_caller_bug_and_throws(double fraction) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MutationMath.MultiplierAt(new AttributeRange(6, 0.6, 1.4, false), fraction));

    [Fact]
    public void Worked_example_gives_the_numbers_of_the_spec()
    {
        var rolls = Rolls((6, MutationMath.MultiplierAt(Ranges47297[0], 0.5)), (20, MutationMath.MultiplierAt(Ranges47297[1], 0.25)),
            (30, MutationMath.MultiplierAt(Ranges47297[2], 0.75)), (50, MutationMath.MultiplierAt(Ranges47297[3], 0)),
            (554, MutationMath.MultiplierAt(Ranges47297[4], 1)));

        var values = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, rolls)).Value;

        Assert.Equal(180.0, values[6], 9);
        Assert.Equal(475.0, values[20], 9);
        Assert.Equal(198.75, values[30], 9);
        Assert.Equal(40.0, values[50], 9);
        Assert.Equal(650.0, values[554], 9);
        Assert.Equal(5, values.Count);
    }

    [Fact]
    public void Mutated_value_is_the_source_base_times_the_roll_with_no_rounding()
    {
        var values = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(
            MutationMath.MutatedValues(new Dictionary<int, double> { [30] = 150 }, [new(30, 0.8, 1.5, false)], Rolls((30, 1.0 / 3.0 + 0.8)))).Value;
        Assert.Equal(150 * (1.0 / 3.0 + 0.8), values[30]);
    }

    [Fact]
    public void Effective_attributes_keep_the_source_and_take_only_skill_requirements_from_the_output()
    {
        var rolls = Rolls((6, 1.0), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3));
        var effective = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(
            MutationMath.EffectiveAttributes(Source12052, Output47408, Skills, Ranges47297, rolls)).Value;

        Assert.Equal(40.0, effective[9]);
        Assert.Equal(10000.0, effective[73]);
        Assert.Equal(3454.0, effective[182]);
        Assert.Equal(4.0, effective[277]);
        Assert.Equal(180.0, effective[6], 9);
        Assert.Equal(475.0, effective[20], 9);
        Assert.Equal(198.75, effective[30], 9);
        Assert.Equal(40.0, effective[50], 9);
        Assert.Equal(650.0, effective[554], 9);
    }

    [Fact]
    public void Output_attributes_other_than_skill_requirements_are_not_copied_and_the_output_is_not_a_base()
    {
        var output = new Dictionary<int, double>(Output47408) { [6] = 999, [50] = 1, [1255] = 7 };
        var effective = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(
            MutationMath.EffectiveAttributes(Source12052, output, Skills, Ranges47297, Rolls((6, 1.0), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3)))).Value;

        Assert.False(effective.ContainsKey(1255));
        Assert.Equal(180.0, effective[6], 9);
        Assert.Equal(40.0, effective[50], 9);
    }

    [Fact]
    public void A_skill_requirement_the_output_does_not_have_is_removed_from_the_source()
    {
        var source = new Dictionary<int, double> { [182] = 1, [183] = 5, [554] = 500 };
        var output = new Dictionary<int, double> { [182] = 3454 };
        var ranges = new AttributeRange[] { new(554, 0.7, 1.3, false) };
        var effective = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(
            MutationMath.EffectiveAttributes(source, output, Skills, ranges, Rolls((554, 1.0)))).Value;

        Assert.Equal(3454.0, effective[182]);
        Assert.False(effective.ContainsKey(183));
        Assert.Equal(500.0, effective[554]);
    }

    [Fact]
    public void A_mutated_attribute_missing_from_the_source_is_refused()
    {
        var source = new Dictionary<int, double>(Source12052);
        source.Remove(554);
        var refused = Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(
            MutationMath.MutatedValues(source, Ranges47297, Rolls((6, 1.0), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3))));
        Assert.Equal(MutationRefusal.AttributeMissingInSource, refused.Reason);
    }

    [Fact]
    public void A_mutated_attribute_without_a_roll_is_refused_and_no_default_is_drawn()
    {
        var refused = Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(
            MutationMath.MutatedValues(Source12052, Ranges47297, Rolls((6, 1.0), (20, 0.95), (50, 0.8), (554, 1.3))));
        Assert.Equal(MutationRefusal.RollMissing, refused.Reason);
    }

    [Fact]
    public void A_roll_for_an_attribute_the_mutaplasmid_does_not_move_is_refused()
    {
        var refused = Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(
            MutationMath.MutatedValues(Source12052, Ranges47297, Rolls((6, 1.0), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3), (9, 1.0))));
        Assert.Equal(MutationRefusal.RollNotMutated, refused.Reason);
    }

    [Fact]
    public void A_roll_outside_its_interval_is_refused()
    {
        var justAbove = Rolls((6, 1.4000001), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3));
        var justBelow = Rolls((6, 0.5999), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3));
        Assert.Equal(MutationRefusal.RollOutOfRange, Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, justAbove)).Reason);
        Assert.Equal(MutationRefusal.RollOutOfRange, Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, justBelow)).Reason);
    }

    [Fact]
    public void Both_ends_of_every_interval_are_accepted_inclusively()
    {
        var lows = Rolls((6, 0.6), (20, 0.9), (30, 0.8), (50, 0.8), (554, 0.7));
        var highs = Rolls((6, 1.4), (20, 1.1), (30, 1.5), (50, 1.5), (554, 1.3));

        var atLow = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, lows)).Value;
        var atHigh = Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, highs)).Value;

        Assert.Equal(180 * 0.6, atLow[6]);
        Assert.Equal(500 * 1.1, atHigh[20]);
    }

    [Fact]
    public void A_roll_one_ulp_outside_an_end_is_refused()
    {
        var below = Rolls((6, Math.BitDecrement(0.6)), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3));
        var above = Rolls((6, 1.0), (20, Math.BitIncrement(1.1)), (30, 1.325), (50, 0.8), (554, 1.3));
        Assert.Equal(MutationRefusal.RollOutOfRange, Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, below)).Reason);
        Assert.Equal(MutationRefusal.RollOutOfRange, Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(MutationMath.MutatedValues(Source12052, Ranges47297, above)).Reason);
    }

    [Fact]
    public void A_roll_that_is_not_a_number_is_refused()
    {
        var refused = Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(
            MutationMath.MutatedValues(Source12052, Ranges47297, Rolls((6, double.NaN), (20, 0.95), (30, 1.325), (50, 0.8), (554, 1.3))));
        Assert.Equal(MutationRefusal.RollOutOfRange, refused.Reason);
    }

    [Fact]
    public void Inverted_interval_accepts_both_printed_ends_and_refuses_outside_them()
    {
        var ranges = new AttributeRange[] { new(2346, 1.025, 0.95, false) };
        Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(MutationMath.ValidateRolls(ranges, Rolls((2346, 0.95))));
        Assert.IsType<Accepted<IReadOnlyDictionary<int, double>>>(MutationMath.ValidateRolls(ranges, Rolls((2346, 1.025))));
        Assert.Equal(MutationRefusal.RollOutOfRange, Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(MutationMath.ValidateRolls(ranges, Rolls((2346, 0.94)))).Reason);
        Assert.Equal(MutationRefusal.RollOutOfRange, Assert.IsType<Refused<IReadOnlyDictionary<int, double>>>(MutationMath.ValidateRolls(ranges, Rolls((2346, 1.03)))).Reason);
    }
}
