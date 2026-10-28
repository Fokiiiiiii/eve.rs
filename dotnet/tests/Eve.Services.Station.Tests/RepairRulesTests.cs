using Eve.Services.Station;
using Xunit;

namespace Eve.Services.Station.Tests;

public sealed class RepairRulesTests
{
    [Fact]
    public void A_zero_cost_target_is_repaired_without_a_wallet_charge()
    {
        var step = Assert.Single(RepairRules.Plan([new RepairRules.Target(77, false, 12.5, 0)], 0));

        Assert.Equal(new RepairRules.Step(77, 12.5, 0), step);
    }
}
