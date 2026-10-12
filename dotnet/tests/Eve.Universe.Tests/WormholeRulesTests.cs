namespace Eve.Universe.Tests;

/// <summary>The wormhole rules on synthetic numbers: the boundaries of the states, the limits and the J-space ids.</summary>
public sealed class WormholeRulesTests
{
    private static readonly WormholeType C1Type = new(30661, 2, 960, 1_000_000_000, 0, 62_000_000, 297);

    [Theory]
    [InlineData(31_000_000, true)]
    [InlineData(31_000_005, true)]
    [InlineData(31_999_999, true)]
    [InlineData(32_000_000, false)]
    [InlineData(30_000_142, false)]
    public void J_space_is_the_system_ids_from_31000000_to_31999999(int solarSystemId, bool expected)
    {
        Assert.Equal(expected, WormholeRules.IsJSpaceSystem(solarSystemId));
    }

    [Fact]
    public void A_ship_passes_only_up_to_the_individual_limit()
    {
        Assert.True(WormholeRules.MassAllowsTransit(C1Type, 62_000_000));
        Assert.False(WormholeRules.MassAllowsTransit(C1Type, 62_000_001));
    }

    [Fact]
    public void Each_transit_takes_the_ship_mass_and_a_wormhole_with_nothing_left_collapses()
    {
        Assert.Equal(900_000_000, WormholeRules.MassAfterTransit(1_000_000_000, 100_000_000));
        Assert.True(WormholeRules.MassAfterTransit(50_000_000, 62_000_000) <= 0);
    }

    [Theory]
    [InlineData(1000000000.0, WormholeMassState.Stable)]
    [InlineData(500000000.0, WormholeMassState.Stable)]
    [InlineData(499999999.0, WormholeMassState.Shrunk)]
    [InlineData(100000000.0, WormholeMassState.Shrunk)]
    [InlineData(99999999.0, WormholeMassState.Critical)]
    [InlineData(1.0, WormholeMassState.Critical)]
    [InlineData(0.0, WormholeMassState.Collapsed)]
    public void The_mass_states_change_at_half_and_a_tenth_of_the_stable_mass(double remaining, WormholeMassState expected)
    {
        Assert.Equal(expected, WormholeRules.MassStateOf(remaining, 1_000_000_000));
    }

    [Theory]
    [InlineData(24.0, WormholeLifetimeState.Stable)]
    [InlineData(23.9, WormholeLifetimeState.UnderOneDay)]
    [InlineData(4.0, WormholeLifetimeState.UnderOneDay)]
    [InlineData(3.9, WormholeLifetimeState.UnderFourHours)]
    [InlineData(1.0, WormholeLifetimeState.UnderFourHours)]
    [InlineData(0.99, WormholeLifetimeState.UnderOneHour)]
    [InlineData(0.0, WormholeLifetimeState.Expired)]
    public void The_lifetime_states_change_at_one_day_four_hours_and_one_hour(double hours, WormholeLifetimeState expected)
    {
        Assert.Equal(expected, WormholeRules.LifetimeStateOf(TimeSpan.FromHours(hours)));
    }

    [Fact]
    public void Polarization_lasts_five_minutes()
    {
        Assert.Equal(TimeSpan.FromMinutes(5), WormholeRules.Polarization);
    }
}
