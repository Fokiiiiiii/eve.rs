using Eve.Dungeons;
using Eve.Dungeons.Incursions;

namespace Eve.Dungeons.Tests;

public sealed class IncursionRulesTests
{
    [Theory]
    [InlineData(1.0, IncursionBand.HighSec)]
    [InlineData(0.5, IncursionBand.HighSec)]
    [InlineData(0.45, IncursionBand.HighSec)]
    [InlineData(0.44, IncursionBand.LowSec)]
    [InlineData(0.3, IncursionBand.LowSec)]
    [InlineData(0.05, IncursionBand.LowSec)]
    [InlineData(0.04, IncursionBand.NullSec)]
    [InlineData(0.0, IncursionBand.NullSec)]
    [InlineData(-0.9, IncursionBand.NullSec)]
    public void A_system_is_in_the_band_its_security_shows(double security, IncursionBand band) =>
        Assert.Equal(band, IncursionRules.BandOf(security));

    [Fact]
    public void Each_band_keeps_one_highsec_one_lowsec_and_three_nullsec_incursions()
    {
        Assert.Equal(1, IncursionRules.Capacity(IncursionBand.HighSec));
        Assert.Equal(1, IncursionRules.Capacity(IncursionBand.LowSec));
        Assert.Equal(3, IncursionRules.Capacity(IncursionBand.NullSec));
    }

    [Fact]
    public void The_phases_are_48_hours_mobilized_a_day_established_and_a_day_withdrawing()
    {
        Assert.Equal(TimeSpan.FromHours(48), IncursionRules.MobilizedLength);
        Assert.Equal(TimeSpan.FromDays(1), IncursionRules.EstablishedLength);
        Assert.Equal(TimeSpan.FromHours(24), IncursionRules.WithdrawingLength);
        Assert.Equal(TimeSpan.FromDays(4), IncursionRules.MaximumLifetime);
        Assert.Null(IncursionRules.LengthOf(IncursionPhase.Ended));
    }

    [Theory]
    [InlineData(0.0, 12)]
    [InlineData(0.5, 24)]
    [InlineData(1.0, 36)]
    [InlineData(-3.0, 12)]
    [InlineData(7.0, 36)]
    public void A_respawn_is_12_to_36_hours_after_the_end(double unit, double hours) =>
        Assert.Equal(TimeSpan.FromHours(hours), IncursionRules.RespawnDelay(unit));

    [Fact]
    public void The_report_states_are_the_client_numbers()
    {
        Assert.Equal(0, IncursionRules.StateOf(IncursionPhase.Withdrawing));
        Assert.Equal(1, IncursionRules.StateOf(IncursionPhase.Mobilized));
        Assert.Equal(2, IncursionRules.StateOf(IncursionPhase.Established));
    }

    [Fact]
    public void Every_staging_site_plans_on_the_wave_runtime_without_a_rule_of_its_own()
    {
        var catalog = DungeonCatalog.LoadEmbedded();
        var rules = WaveRules.LoadEmbedded();
        foreach (var id in IncursionRules.StagingDungeonIds)
        {
            var found = Assert.IsType<DungeonFound>(catalog.GetDungeon(id));
            Assert.Equal(60, found.Dungeon.ArchetypeID);
            var answer = WavePlanner.Plan(found.Dungeon, rules);
            Assert.True(answer.Plan is not null, $"dungeon {id}: {answer.Refusal}");
            Assert.All(answer.Plan!.Waves, wave => Assert.Equal("sanshas_deadspace_pve", wave.SpawnQuery));
        }
    }

    [Fact]
    public void The_headquarters_is_the_farthest_system_and_the_vanguards_and_assaults_are_the_nearest_of_the_rest()
    {
        // others (id, distance): the farthest is the headquarters (100 at 300); of the rest, three are vanguards (20, 30, 10, nearest first) and 40 is the assault
        var layout = IncursionRules.LayoutOf([(10, 50), (20, 10), (30, 20), (40, 60), (100, 300)]);
        Assert.Equal(100, layout.Headquarters);
        Assert.Equal([20, 30, 10], layout.Vanguard);
        Assert.Equal([40], layout.Assault);
    }

    [Fact]
    public void Ties_on_distance_go_to_the_lower_id_and_a_three_system_constellation_has_one_vanguard_and_no_assault()
    {
        var layout = IncursionRules.LayoutOf([(7, 5), (3, 5)]);
        Assert.Equal(3, layout.Headquarters);
        Assert.Equal([7], layout.Vanguard);
        Assert.Empty(layout.Assault);
    }

    [Fact]
    public void Every_other_system_of_a_constellation_gets_a_role()
    {
        for (var size = 3; size <= 12; size++)
        {
            var others = Enumerable.Range(1, size - 1).Select(i => (Id: i, Distance: (double)i)).ToList();
            var layout = IncursionRules.LayoutOf(others);
            Assert.Equal(size - 2, layout.Vanguard.Count + layout.Assault.Count);
        }
    }

    [Fact]
    public void The_mothership_protection_is_three_days_in_highsec_one_in_lowsec_and_none_in_nullsec()
    {
        Assert.Equal(TimeSpan.FromDays(3), IncursionRules.MothershipProtection(IncursionBand.HighSec));
        Assert.Equal(TimeSpan.FromDays(1), IncursionRules.MothershipProtection(IncursionBand.LowSec));
        Assert.Equal(TimeSpan.Zero, IncursionRules.MothershipProtection(IncursionBand.NullSec));
        Assert.Equal(5158, IncursionRules.MothershipDungeonId(IncursionBand.NullSec));
        Assert.Equal(5201, IncursionRules.MothershipDungeonId(IncursionBand.LowSec));
    }

    [Fact]
    public void Every_role_template_is_in_the_owner_catalog_with_its_archetype_and_a_wave_plan()
    {
        var catalog = DungeonCatalog.LoadEmbedded();
        var rules = WaveRules.LoadEmbedded();
        var roles = new (IReadOnlyList<long> Ids, int Archetype)[]
        {
            (IncursionRules.VanguardDungeonIds, 61),
            (IncursionRules.AssaultDungeonIds, 62),
            (IncursionRules.HeadquartersDungeonIds, 63),
            ([5158L, 5201L], 64),
        };
        foreach (var (ids, archetype) in roles)
        {
            foreach (var id in ids)
            {
                var found = Assert.IsType<DungeonFound>(catalog.GetDungeon(id));
                Assert.Equal(archetype, found.Dungeon.ArchetypeID);
                Assert.True(WavePlanner.Plan(found.Dungeon, rules).Plan is not null, $"dungeon {id} has no wave plan");
            }
        }
    }
}
