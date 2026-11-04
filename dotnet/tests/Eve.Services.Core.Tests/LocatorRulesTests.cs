using Eve.Services.Core;
using Xunit;

namespace Eve.Services.Core.Tests;

public class LocatorRulesTests
{
    [Theory]
    [InlineData(1, LocateDistance.SameSystem, 1_000L)]
    [InlineData(1, LocateDistance.SameConstellation, 5_000L)]
    [InlineData(2, LocateDistance.SameRegion, 25_000L)]
    [InlineData(3, LocateDistance.OtherRegion, 100_000L)]
    [InlineData(4, LocateDistance.SameConstellation, 50_000L)]
    [InlineData(5, LocateDistance.OtherRegion, 500_000L)]
    public void The_fee_is_the_wiki_amount_for_the_level_and_the_distance(int level, LocateDistance distance, long fee)
    {
        Assert.Equal(fee, LocatorRules.Fee(level, distance));
    }

    [Theory]
    [InlineData(1, LocateDistance.OtherRegion)]
    [InlineData(1, LocateDistance.SameRegion)]
    [InlineData(2, LocateDistance.OtherRegion)]
    public void A_locator_that_cannot_reach_that_distance_has_no_fee(int level, LocateDistance distance)
    {
        Assert.Null(LocatorRules.Fee(level, distance));
    }

    [Fact]
    public void The_cooldowns_and_the_standings_rise_with_the_level()
    {
        Assert.Equal(new[] { 5, 5, 15, 30, 60 }, Enumerable.Range(1, 5).Select(LocatorRules.CooldownMinutesFor).ToArray());
        Assert.Null(LocatorRules.RequiredStandingFor(1));
        Assert.Equal(new double?[] { 1.0, 3.0, 5.0, 7.0 }, Enumerable.Range(2, 4).Select(level => LocatorRules.RequiredStandingFor(level)).ToArray());
    }

    [Fact]
    public void The_distance_is_the_first_tier_the_two_systems_share()
    {
        Assert.Equal(LocateDistance.SameSystem, LocatorRules.Classify(1, 10, 100, 1, 11, 101));
        Assert.Equal(LocateDistance.SameConstellation, LocatorRules.Classify(1, 10, 100, 2, 10, 101));
        Assert.Equal(LocateDistance.SameRegion, LocatorRules.Classify(1, 10, 100, 2, 11, 100));
        Assert.Equal(LocateDistance.OtherRegion, LocatorRules.Classify(1, 10, 100, 2, 11, 101));
    }

    [Fact]
    public void A_level_outside_one_to_five_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => LocatorRules.Fee(0, LocateDistance.SameSystem));
        Assert.Throws<ArgumentOutOfRangeException>(() => LocatorRules.CooldownMinutesFor(6));
    }
}
