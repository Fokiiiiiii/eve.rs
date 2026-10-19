using System.Text.Json;

namespace Eve.Market.Tests;

public class RulesTests
{
    [Fact]
    public void Fees_and_skill_limits_replay_the_reference()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "rules.json")));
        Assert.Equal("828823a75f211fdecb945b087a26c225f111491f", fixture.RootElement.GetProperty("sourceCommit").GetString());
        var count = 0;
        foreach (var test in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            var level = test.GetProperty("level").GetInt32();
            var skills = new MarketSkillLevels(level, level, level, level, level, level, level, level, level, level, level);
            var context = MarketRules.Context(skills, test.GetProperty("isStation").GetBoolean(),
                test.GetProperty("factionStanding").GetDouble(), test.GetProperty("corpStanding").GetDouble(),
                test.GetProperty("warFaction").GetInt32(), test.GetProperty("solarSystem").GetInt32());
            var expected = test.GetProperty("context");
            Assert.Equal(expected.GetProperty("brokerCommissionRate").GetDouble(), context.BrokerCommissionRate);
            Assert.Equal(expected.GetProperty("sccSurchargeRate").GetDouble(), context.SccSurchargeRate);
            Assert.Equal(expected.GetProperty("modificationFeeDiscount").GetDouble(), context.ModificationFeeDiscount);
            var limits = expected.GetProperty("limits");
            Assert.Equal(limits.GetProperty("cnt").GetInt32(), context.Limits.Count);
            Assert.Equal(limits.GetProperty("acc").GetDouble(), context.Limits.TransactionTaxRate);
            Assert.Equal(limits.GetProperty("ask").GetInt32(), context.Limits.SellRange);
            Assert.Equal(limits.GetProperty("bid").GetInt32(), context.Limits.BuyRange);
            Assert.Equal(limits.GetProperty("vis").GetInt32(), context.Limits.Visibility);
            Assert.Equal(limits.GetProperty("mod").GetInt32(), context.Limits.ModifyRange);
            foreach (var fees in test.GetProperty("fees").EnumerateArray())
            {
                double? old = fees.GetProperty("oldAmount").ValueKind == JsonValueKind.Null ? null : fees.GetProperty("oldAmount").GetDouble();
                var amount = fees.GetProperty("newAmount").GetDouble();
                Check(fees.GetProperty("broker"), MarketRules.BrokerFee(context, old, amount));
                Check(fees.GetProperty("scc"), MarketRules.SccSurcharge(context, old, amount));
                Assert.Equal(fees.GetProperty("tax").GetDouble(), MarketRules.SalesTax(context, amount));
                count++;
            }
        }
        Assert.Equal(504, count);
    }

    [Fact]
    public void Structure_market_tax_is_a_broker_rate_separate_from_accounting_sales_tax()
    {
        var skills = new MarketSkillLevels(Accounting: 5);
        var context = MarketRules.StructureContext(skills, 1.0);

        Assert.Equal(0.01, context.BrokerCommissionRate);
        Assert.Equal(0.005, context.SccSurchargeRate);
        Assert.Equal(0.03375, context.Limits.TransactionTaxRate, 10);
        Assert.Equal(1_000, MarketRules.BrokerFee(context, null, 100_000).Amount);
        Assert.Equal(500, MarketRules.SccSurcharge(context, null, 100_000).Amount);
        Assert.Equal(3_375, MarketRules.SalesTax(context, 100_000));

        var defaultRate = MarketRules.StructureContext(skills, null);
        Assert.Equal(0.01, defaultRate.BrokerCommissionRate);
        Assert.Equal(0.115, MarketRules.StructureContext(skills, 20).BrokerCommissionRate);
    }

    private static void Check(JsonElement expected, FeeInfo actual)
    {
        Assert.Equal(expected.GetProperty("amount").GetDouble(), actual.Amount);
        Assert.Equal(expected.GetProperty("rawPercentage").GetDouble(), actual.RawPercentage);
        Assert.Equal(expected.GetProperty("usingMinimumValue").GetBoolean(), actual.UsingMinimumValue);
    }

    [Fact]
    public void Only_the_references_durations_and_ranges_are_accepted()
    {
        Assert.Equal(new[] { 0, 1, 3, 7, 14, 30, 90 }, Enumerable.Range(-2, 95).Where(MarketRules.IsAllowedDuration));
        Assert.Equal(new[] { -1, 0, 1, 2, 3, 4, 5, 10, 20, 30, 40 }, Enumerable.Range(-2, 45).Where(MarketRules.IsAllowedBuyRange));
        Assert.True(MarketRules.IsAllowedBuyRange(32767));
        Assert.False(MarketRules.IsAllowedBuyRange(32766));
    }

    [Fact]
    public void Rounding_uses_the_reference_half_toward_positive_infinity_rule()
    {
        Assert.Equal(1.13, MarketRules.RoundIsk(1.125));
        Assert.Equal(-1.12, MarketRules.RoundIsk(-1.125));
        Assert.Equal(0, MarketRules.RoundIsk(double.NaN));
        Assert.Equal(0, MarketRules.RoundIsk(double.PositiveInfinity));
    }

    [Fact]
    public void Prices_have_at_most_four_significant_figures_and_two_decimals()
    {
        // The client's ticks.is_tick (marketutil/ticks.py): 1234.00 is a tick; 1234.50 (five figures) and 1.005 (three decimals) are not.
        foreach (var price in new[] { 1m, 0.01m, 0.12m, 123.4m, 100m, 1234m, 12340m, 9999m })
            Assert.True(MarketRules.IsTick(price), price.ToString());
        foreach (var price in new[] { 0m, -1m, 1234.5m, 12345m, 9999.9m, 1.005m, 0.001m, 1234.56m })
            Assert.False(MarketRules.IsTick(price), price.ToString());
    }
}
