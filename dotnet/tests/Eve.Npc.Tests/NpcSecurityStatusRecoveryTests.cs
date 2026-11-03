using Eve.Npc;

namespace Eve.Npc.Tests;

public sealed class NpcSecurityStatusRecoveryTests
{
    [Fact]
    public void An_NPC_security_status_bonus_and_Fast_Talk_raise_status_by_the_reference_formula()
    {
        var next = NpcSecurityStatusRecovery.TryNext(0.24, -2.0, 1.25);

        Assert.InRange(Math.Abs(next.GetValueOrDefault() + 1.964), 0, 1e-12);
    }

    [Fact]
    public void The_SDE_bonus_is_used_without_a_bounty_based_modifier_cap()
    {
        var next = NpcSecurityStatusRecovery.TryNext(0.24, 0, 1.25);

        Assert.InRange(Math.Abs(next.GetValueOrDefault() - 0.03), 0, 1e-12);
    }

    [Fact]
    public void An_NPC_recovery_is_capped_at_five_while_using_ten_as_the_formula_target()
    {
        var next = NpcSecurityStatusRecovery.TryNext(0.24, 4.99, 1.25);

        Assert.Equal(5.0, next);
    }

    [Fact]
    public void Security_status_does_not_gain_from_NPC_kills_at_or_above_five()
    {
        Assert.Null(NpcSecurityStatusRecovery.TryNext(0.24, 5.0, 1.0));
        Assert.Null(NpcSecurityStatusRecovery.TryNext(0.24, 5.2, 1.0));
    }

    [Fact]
    public void The_formula_keeps_the_unrounded_status_when_four_digit_rounding_changes()
    {
        var next = NpcSecurityStatusRecovery.TryNext(0.1, 0.12345, 1.0);

        Assert.InRange(Math.Abs(next.GetValueOrDefault() - 0.13332655), 0, 1e-8);
    }

    [Fact]
    public void No_change_is_written_when_the_reference_four_digit_value_does_not_change()
    {
        var next = NpcSecurityStatusRecovery.TryNext(0.000001, 4.9999, 1.0);

        Assert.Null(next);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_security_status_bonus_does_not_recover_status(double bonusPercent)
    {
        Assert.Null(NpcSecurityStatusRecovery.TryNext(bonusPercent, -1.0, 1.0));
    }

    [Fact]
    public void Status_does_not_rise_past_five_from_NPC_kills()
    {
        Assert.Null(NpcSecurityStatusRecovery.TryNext(0.24, 5.0, 1.25));
    }

    [Fact]
    public void The_Fast_Talk_attribute_id_matches_the_reference()
    {
        Assert.Equal(359, NpcSecurityStatusRecovery.FastTalkPercentageAttributeId);
    }

    [Theory]
    [InlineData(500010, true)]
    [InlineData(500011, true)]
    [InlineData(500012, true)]
    [InlineData(500019, true)]
    [InlineData(500020, true)]
    [InlineData(0, false)]
    [InlineData(500002, false)]
    [InlineData(500003, false)]
    [InlineData(500025, false)]
    public void Only_pirate_factions_award_NPC_security_status_recovery(int factionId, bool eligible)
    {
        Assert.Equal(eligible, NpcSecurityStatusRecovery.IsEligibleFaction(factionId));
    }

    [Fact]
    public void A_generic_named_mission_NPC_with_a_bounty_is_not_a_pirate_status_target()
    {
        var profile = NpcAuthority.Load().Profile("parity_mission_generic_29917_captain_amiette_barcier");

        Assert.NotNull(profile);
        Assert.Equal(2_000_000, profile.Bounty);
        Assert.False(NpcSecurityStatusRecovery.IsEligibleFaction(profile.FactionId));
    }
}
