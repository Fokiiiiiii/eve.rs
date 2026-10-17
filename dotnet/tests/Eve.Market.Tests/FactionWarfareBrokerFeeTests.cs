namespace Eve.Market.Tests;

public class FactionWarfareBrokerFeeTests
{
    private static readonly MarketSkillLevels NoSkills = new();

    [Theory]
    [InlineData(0, 0.03)]
    [InlineData(1, 0.027)]
    [InlineData(2, 0.024)]
    [InlineData(5, 0.015)]
    public void Each_factional_warfare_upgrade_level_cuts_the_station_commission_ten_percent(int level, double commission) =>
        Assert.Equal(commission, MarketRules.Context(NoSkills, isStation: true, factionWarfareUpgradeLevel: level).BrokerCommissionRate, 12);

    [Fact]
    public void The_upgrade_is_applied_before_the_standing_discounts_as_the_client_does()
    {
        // Client order (marketutil/brokerFee.py): the base after the skill, the upgrade, then the station owner's standing.
        var context = MarketRules.Context(NoSkills, isStation: true, factionStanding: 10, corpStanding: 10, factionWarfareUpgradeLevel: 1);
        Assert.Equal(0.027 - 10 * 0.0003 - 10 * 0.0002, context.BrokerCommissionRate, 12);
    }

    [Fact]
    public void Without_a_factional_warfare_level_the_commission_is_unchanged()
    {
        Assert.Equal(MarketRules.Context(NoSkills, isStation: true).BrokerCommissionRate,
            MarketRules.Context(NoSkills, isStation: true, factionWarfareUpgradeLevel: 0).BrokerCommissionRate, 12);
    }
}
