using Eve.Services.Core;

namespace Eve.Services.Core.Tests;

public sealed class ApproximateMissionRewardRulesTests
{
    [Fact]
    public void Mission_lp_uses_configured_security_division_and_security_connections_factors()
    {
        Assert.Equal(1_625.472m, ApproximateMissionRewardRules.LoyaltyPoints(1_000m, 0.5m, 0.2m, 2));
    }

    [Fact]
    public void Mission_lp_factors_can_be_replaced_by_configuration()
    {
        var configuration = new ApproximateMissionRewardConfiguration(1.7m, 0.05m);

        Assert.Equal(1_584m, ApproximateMissionRewardRules.LoyaltyPoints(1_000m, 0.5m, 0.2m, 2, configuration));
    }

    [Theory]
    [InlineData("encounter", 1, 0.9, 0, 77)]
    [InlineData("encounter", 4, 0.7, 5, 2_020)]
    [InlineData("courier", 4, 0.9, 0, 532)]
    [InlineData("mining", 5, 0.5, 2, 2_370)]
    public void Mission_lp_uses_configured_kind_level_security_and_division_skill_values(
        string kind, int level, decimal security, int skillLevel, long expected)
    {
        Assert.Equal(expected, ApproximateMissionRewardRules.LoyaltyPoints(kind, level, security, skillLevel));
    }

    [Fact]
    public void Mission_lp_base_table_can_be_replaced()
    {
        var configuration = new ApproximateMissionRewardConfiguration(
            BaseLoyaltyPoints: new Dictionary<(string Kind, int Level), decimal> { [("encounter", 1)] = 100 });

        Assert.Equal(113, ApproximateMissionRewardRules.LoyaltyPoints("encounter", 1, 0.5m, 0, configuration));
        Assert.Equal(0, ApproximateMissionRewardRules.LoyaltyPoints("courier", 1, 0.5m, 0, configuration));
    }

    [Fact]
    public void Mission_lp_uses_displayed_security_precision_and_floor()
    {
        Assert.Equal(77, ApproximateMissionRewardRules.LoyaltyPoints("encounter", 1, 0.94m, 0));
        Assert.Equal(11, ApproximateMissionRewardRules.LoyaltyPoints("encounter", 1, 2.0m, 0));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 4)]
    [InlineData(5, 20)]
    public void Advanced_contracting_capacity_uses_the_configured_per_level_limit(int level, int additional)
    {
        Assert.Equal(additional, ApproximateContractLimitRules.AdditionalOutstandingContracts(level));
    }

    [Fact]
    public void Advanced_contracting_limit_can_be_replaced_by_configuration()
    {
        Assert.Equal(15, ApproximateContractLimitRules.AdditionalOutstandingContracts(5, new ApproximateContractLimitConfiguration(3)));
    }
}
