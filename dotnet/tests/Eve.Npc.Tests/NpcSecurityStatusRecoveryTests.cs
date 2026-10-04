using Eve.Npc;

namespace Eve.Npc.Tests;

public sealed class NpcSecurityStatusRecoveryTests
{
    [Fact]
    public void A_bounty_and_Fast_Talk_raise_status_by_the_reference_formula()
    {
        var next = NpcSecurityStatusRecovery.TryNext(100_000m, -2.0, 1.25);

        Assert.Equal(-1.985, next);
    }

    [Fact]
    public void The_security_status_modifier_is_capped_at_point_0025()
    {
        var next = NpcSecurityStatusRecovery.TryNext(250_000m, 0, 1.0);

        Assert.Equal(0.025, next);
    }

    [Fact]
    public void A_pirate_kill_can_raise_status_above_five_as_in_the_pinned_EveJS_reference()
    {
        var next = NpcSecurityStatusRecovery.TryNext(250_000m, 5.0, 1.0);

        Assert.Equal(5.0125, next);
        Assert.True(next > 5.0);
    }

    [Fact]
    public void Each_fresh_kill_in_the_same_five_minute_period_gets_a_separate_reference_recovery()
    {
        var first = NpcSecurityStatusRecovery.TryNext(250_000m, 0, 1.0);
        var second = NpcSecurityStatusRecovery.TryNext(250_000m, first!.Value, 1.0);

        Assert.Equal(0.025, first);
        Assert.InRange(Math.Abs(second.GetValueOrDefault() - 0.0499375), 0, 1e-12);
    }

    [Fact]
    public void The_formula_keeps_the_unrounded_status_when_four_digit_rounding_changes()
    {
        var next = NpcSecurityStatusRecovery.TryNext(100_000m, 0.12345, 1.0);

        Assert.InRange(Math.Abs(next.GetValueOrDefault() - 0.13332655), 0, 1e-8);
    }

    [Fact]
    public void No_change_is_written_when_the_reference_four_digit_value_does_not_change()
    {
        var next = NpcSecurityStatusRecovery.TryNext(1m, 9.9999, 1.0);

        Assert.Null(next);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_bounty_does_not_recover_security_status(decimal bounty)
    {
        Assert.Null(NpcSecurityStatusRecovery.TryNext(bounty, -1.0, 1.0));
    }

    [Fact]
    public void Status_does_not_rise_past_ten()
    {
        Assert.Null(NpcSecurityStatusRecovery.TryNext(250_000m, 10.0, 1.25));
    }

    [Fact]
    public void The_Fast_Talk_attribute_id_matches_the_reference()
    {
        Assert.Equal(359, NpcSecurityStatusRecovery.FastTalkPercentageAttributeId);
    }
}
