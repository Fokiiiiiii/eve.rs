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
}
