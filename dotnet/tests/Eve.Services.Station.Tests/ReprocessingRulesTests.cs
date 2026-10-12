using Eve.Services.Station;

namespace Eve.Services.Station.Tests;

/// <summary>The reprocessing rules: the standing tax, the efficiency, the skill multipliers and what a stack yields (the wiki's and the SDE's numbers).</summary>
public sealed class ReprocessingRulesTests
{
    [Theory]
    [InlineData(0.0, 0.05)]
    // the reference rounds the reduced rate to two decimals (round2 in reprocessingRuntime.js): 2.5% becomes 3%
    [InlineData(3.335, 0.03)]
    [InlineData(6.67, 0.0)]
    [InlineData(9.0, 0.0)]
    public void The_station_tax_falls_from_five_percent_to_zero_at_standing_6_67(double standing, double expected)
    {
        Assert.Equal(expected, ReprocessingRules.StationTax(0.05, standing), 6);
    }

    [Fact]
    public void The_efficiency_multiplies_the_station_by_the_skills_and_never_exceeds_one()
    {
        Assert.Equal(0.5, ReprocessingRules.Efficiency(0.5, 1.0), 9);
        Assert.Equal(0.5 * 1.15 * 1.10 * 1.10, ReprocessingRules.Efficiency(0.5, 1.15 * 1.10 * 1.10), 9);
        Assert.Equal(1.0, ReprocessingRules.Efficiency(0.5, 3.0));
    }

    [Theory]
    [InlineData(5, 3.0, 1.15)]
    [InlineData(0, 3.0, 1.0)]
    [InlineData(4, 2.0, 1.08)]
    public void A_skill_gives_its_percentage_per_level(int level, double percentage, double expected)
    {
        Assert.Equal(expected, ReprocessingRules.SkillMultiplier(level, percentage), 9);
    }

    [Fact]
    public void A_stack_yields_whole_portions_and_keeps_the_leftovers()
    {
        var materials = new[] { new ReprocessingRules.Material(34, 415), new ReprocessingRules.Material(35, 83) };
        var quote = ReprocessingRules.Build(7, 1230, stackQuantity: 250, portionSize: 100, efficiency: 0.5, taxRate: 0.05,
            materials, new Dictionary<int, double> { [34] = 5.0, [35] = 10.0 });

        Assert.Equal(200, quote.QuantityToProcess);
        Assert.Equal(50, quote.LeftOvers);
        Assert.Equal(2, quote.Portions);
        // per portion 415 and 83; two portions give 830 and 166; at 50% the yield is 415 and 83
        Assert.Equal(new long[] { 415, 83 }, quote.Recoverables.Select(r => r.Client).ToArray());
        Assert.Equal(new long[] { 415, 83 }, quote.Recoverables.Select(r => r.Unrecoverable).ToArray());
    }

    [Fact]
    public void Each_yield_is_rounded_down_after_the_efficiency_and_the_tax_is_charged_on_it()
    {
        // 200 units at a 0.575 yield arrive as 114.99999999999999; the yield is 115 (the reference's rule)
        var quote = ReprocessingRules.Build(1, 1, stackQuantity: 200, portionSize: 1, efficiency: 0.575, taxRate: 0.05,
            [new ReprocessingRules.Material(34, 1)], new Dictionary<int, double> { [34] = 4.0 });

        var yield = quote.Recoverables.Single();
        Assert.Equal(115, yield.Client);
        Assert.Equal(200 - 115, yield.Unrecoverable);
        Assert.Equal(ReprocessingRules.Round2(4.0 * 115 * 0.05), yield.IskCost);
        Assert.Equal(yield.IskCost, quote.TotalIskCost);
    }

    [Fact]
    public void A_stack_smaller_than_one_portion_yields_nothing()
    {
        var quote = ReprocessingRules.Build(1, 1, stackQuantity: 40, portionSize: 100, efficiency: 0.5, taxRate: 0.05,
            [new ReprocessingRules.Material(34, 415)], new Dictionary<int, double>());

        Assert.Equal(0, quote.Portions);
        Assert.Equal(0, quote.QuantityToProcess);
        Assert.Equal(40, quote.LeftOvers);
        Assert.Empty(quote.Recoverables);
        Assert.Equal(0.0, quote.TotalIskCost);
    }

    [Fact]
    public void A_material_without_a_price_is_taxed_at_zero()
    {
        var quote = ReprocessingRules.Build(1, 1, stackQuantity: 100, portionSize: 1, efficiency: 1.0, taxRate: 0.05,
            [new ReprocessingRules.Material(34, 1)], new Dictionary<int, double>());

        Assert.Equal(100, quote.Recoverables.Single().Client);
        Assert.Equal(0.0, quote.TotalIskCost);
    }

    [Fact]
    public void A_stack_is_split_into_aligned_chunks_that_add_up_to_the_stack()
    {
        // every unit yields one Tritanium at efficiency 1: a chunk is safe while it yields at most 2^31 - 1
        var chunks = ReprocessingRules.SplitChunks(3_000_000_000, 100,
            q => ReprocessingRules.IsClientSafe(ReprocessingRules.Build(1, 1, q, 100, 1.0, 0, [new ReprocessingRules.Material(34, 100)], new Dictionary<int, double>())));

        Assert.NotNull(chunks);
        Assert.Equal(3_000_000_000L, chunks!.Sum());
        Assert.All(chunks.Take(chunks.Count - 1), c => Assert.Equal(0L, c % 100));
        Assert.True(chunks[0] <= ReprocessingRules.ClientInventoryStackLimit);
    }

    [Fact]
    public void The_root_of_a_split_keeps_the_leftovers_of_the_stack()
    {
        var chunks = ReprocessingRules.SplitChunks(3_000_000_037, 100,
            q => ReprocessingRules.IsClientSafe(ReprocessingRules.Build(1, 1, q, 100, 1.0, 0, [new ReprocessingRules.Material(34, 100)], new Dictionary<int, double>())));

        Assert.NotNull(chunks);
        Assert.Equal(3_000_000_037L, chunks!.Sum());
        Assert.Equal(37L, chunks[0] % 100);
    }

    [Fact]
    public void A_stack_with_no_safe_quantity_has_no_split()
    {
        Assert.Null(ReprocessingRules.SplitChunks(100, 1, _ => false));
    }

    [Fact]
    public void The_sum_of_quotes_adds_the_yields_and_the_taxes_of_the_chunks()
    {
        var one = ReprocessingRules.Build(7, 1, 100, 100, 0.5, 0.05, [new ReprocessingRules.Material(34, 415)], new Dictionary<int, double> { [34] = 5.0 });
        var total = ReprocessingRules.Sum(7, 1, [one, one]);

        Assert.Equal(200, total.QuantityToProcess);
        Assert.Equal(2 * one.TotalIskCost, total.TotalIskCost, 6);
        Assert.Equal(2 * one.Recoverables.Single().Client, total.Recoverables.Single().Client);
    }
}
