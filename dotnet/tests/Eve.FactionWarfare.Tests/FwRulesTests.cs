namespace Eve.FactionWarfare.Tests;

public sealed class FwRulesTests
{
    [Fact]
    public void Warzone_maps_match_the_client_map_and_do_not_overlap()
    {
        Assert.Equal(103, FwWarzones.SystemsOf(FwWarzones.AmarrMinmatar).Count);
        Assert.Equal(149, FwWarzones.SystemsOf(FwWarzones.CaldariGallente).Count);
        Assert.Empty(FwWarzones.SystemsOf(3));
        Assert.Equal(FwWarzones.AmarrMinmatar, FwWarzones.WarzoneOf(FwWarzones.SystemsOf(1)[0]));
        Assert.Equal(0, FwWarzones.WarzoneOf(30000001));
    }

    [Fact]
    public void Initial_occupier_is_the_sde_faction_only_when_it_is_one_of_the_warzone_factions()
    {
        Assert.Equal(FwFactions.AmarrEmpire, FwWarzones.InitialOccupier(FwWarzones.AmarrMinmatar, FwFactions.AmarrEmpire));
        Assert.Equal(0, FwWarzones.InitialOccupier(FwWarzones.AmarrMinmatar, FwFactions.GallenteFederation));
        Assert.Equal(0, FwWarzones.InitialOccupier(FwWarzones.CaldariGallente, 0));
        Assert.Equal(0, FwWarzones.InitialOccupier(FwWarzones.CaldariGallente, FwFactions.GuristasPirates));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(3, 0)]
    [InlineData(4, 0)]
    [InlineData(378, 0)]
    [InlineData(379, 1)]
    [InlineData(380, 2)]
    [InlineData(382, 4)]
    [InlineData(383, 5)]
    [InlineData(384, 5)]
    [InlineData(10_000, 5)]
    public void Upgrade_level_follows_the_client_thresholds_and_clamps_the_negative_case(long pool, int level) =>
        Assert.Equal(level, FwRules.UpgradeLevel(pool));

    [Fact]
    public void A_system_held_by_an_enemy_gives_no_upgrade_to_that_faction()
    {
        Assert.Equal(5, FwRules.EffectiveUpgradeLevel(384, FwFactions.AmarrEmpire, FwFactions.AmarrEmpire));
        Assert.Equal(0, FwRules.EffectiveUpgradeLevel(384, FwFactions.AmarrEmpire, FwFactions.MinmatarRepublic));
        Assert.Equal(5, FwRules.EffectiveUpgradeLevel(384, FwFactions.AmarrEmpire, 0));
    }

    [Fact]
    public void Broker_fee_and_industry_cost_drop_ten_percent_per_level()
    {
        Assert.Equal(3.0, FwRules.AdjustedFeePercentage(3.0, 0), 10);
        Assert.Equal(2.7, FwRules.AdjustedFeePercentage(3.0, 1), 10);
        Assert.Equal(1.5, FwRules.AdjustedFeePercentage(3.0, 5), 10);
        Assert.Equal(0.5, FwRules.IndustryCostFactor(5), 10);
    }

    [Theory]
    [InlineData(0L, 100L, 0.0)]
    [InlineData(100L, 100L, 0.83)]
    [InlineData(50L, 100L, 0.38)]
    [InlineData(100L, 0L, 0.0)]
    public void Donation_tax_is_the_client_formula_rounded_to_two_places(long points, long max, double tax) =>
        Assert.Equal(tax, FwRules.DonationTax(FwFactions.CaldariState, points, max), 10);

    [Fact]
    public void Donation_tax_is_zero_for_a_non_occupier()
    {
        Assert.Equal(0.0, FwRules.DonationTax(FwFactions.GuristasPirates, 100, 100));
    }

    [Theory]
    [InlineData(0.0, true)]
    [InlineData(-0.0001, true)]
    [InlineData(-0.0002, false)]
    [InlineData(-5.0, false)]
    public void Enlistment_needs_the_client_minimum_standing(double standing, bool allowed) =>
        Assert.Equal(allowed, FwRules.MeetsEnlistmentStanding(standing));

    [Theory]
    [InlineData(0.9, 0)]
    [InlineData(9.7, 9)]
    [InlineData(12.0, 9)]
    [InlineData(-3.0, 0)]
    public void Rank_is_the_whole_part_of_the_standing_clamped_to_nine(double standing, int rank) =>
        Assert.Equal(rank, FwRules.RankOf(standing));

    [Theory]
    [InlineData(0L, ContestState.Stable, 0)]
    [InlineData(1L, ContestState.Contested, 0)]
    [InlineData(37_500L, ContestState.Contested, 50)]
    [InlineData(74_999L, ContestState.Contested, 99)]
    [InlineData(75_000L, ContestState.Vulnerable, 100)]
    [InlineData(200_000L, ContestState.Vulnerable, 100)]
    public void Contest_state_follows_the_attacker_points(long points, ContestState state, int percent)
    {
        Assert.Equal(state, FwRules.Contest(points));
        Assert.Equal(percent, FwRules.ContestedPercent(points));
    }

    [Fact]
    public void Downtime_is_the_first_1100_utc_strictly_after_the_request()
    {
        const long hour = 3_600_000L;
        const long day = 24 * hour;
        var midnight = 20_000L * day;
        Assert.Equal(midnight + 11 * hour, FwRules.NextDowntimeMs(midnight + 10 * hour));
        Assert.Equal(midnight + day + 11 * hour, FwRules.NextDowntimeMs(midnight + 11 * hour));
        Assert.Equal(midnight + day + 11 * hour, FwRules.NextDowntimeMs(midnight + 23 * hour));
    }

    [Theory]
    [InlineData(FwFactions.AmarrEmpire, FwFactions.MinmatarRepublic, true)]
    [InlineData(FwFactions.MinmatarRepublic, FwFactions.AmarrEmpire, true)]
    [InlineData(FwFactions.CaldariState, FwFactions.GallenteFederation, true)]
    [InlineData(FwFactions.AngelCartel, FwFactions.AmarrEmpire, true)]
    [InlineData(FwFactions.GuristasPirates, FwFactions.GallenteFederation, true)]
    [InlineData(FwFactions.AmarrEmpire, FwFactions.CaldariState, false)]
    [InlineData(FwFactions.AngelCartel, FwFactions.GuristasPirates, false)]
    [InlineData(FwFactions.AmarrEmpire, FwFactions.AmarrEmpire, false)]
    public void Combat_enemies_are_the_client_pairs_in_both_directions(long a, long b, bool enemies)
    {
        Assert.Equal(enemies, FwFactions.IsCombatEnemy(a, b));
        Assert.Equal(enemies, FwFactions.IsCombatEnemy(b, a));
    }

    [Fact]
    public void Militia_corporations_are_the_sde_values()
    {
        Assert.Equal(1000180, FwFactions.MilitiaCorporationOf(FwFactions.CaldariState));
        Assert.Equal(1000179, FwFactions.MilitiaCorporationOf(FwFactions.AmarrEmpire));
        Assert.Equal(0, FwFactions.MilitiaCorporationOf(500018));
    }
}
