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
