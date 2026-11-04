using Eve.Services.Station;
using Xunit;

namespace Eve.Services.Station.Tests;

public sealed class RepairRulesTests
{
    [Fact]
    public void Structure_repair_quote_includes_tax_and_splits_it_to_cents()
    {
        Assert.Equal(0.115, RepairRules.UnitCostWithStructureTax(0.1, 0.15), 6);
        Assert.Equal(0.13m, RepairRules.StructureTaxCredit(1m, 0.15));
    }

    [Fact]
    public void A_zero_cost_target_is_repaired_without_a_wallet_charge()
    {
        var step = Assert.Single(RepairRules.Plan([new RepairRules.Target(77, false, 12.5, 0)], 0));

        Assert.Equal(new RepairRules.Step(77, 12.5, 0), step);
    }
}
