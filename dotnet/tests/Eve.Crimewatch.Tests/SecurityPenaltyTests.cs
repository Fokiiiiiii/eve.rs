namespace Eve.Crimewatch.Tests;

public class SecurityPenaltyTests
{
    [Fact]
    public void Shooting_a_ship_in_a_safe_system_costs_a_quarter_point_from_zero_as_the_reference_took_it()
    {
        // recorded: a pilot at 0.0 attacked a ship in a 1.0 system and was told −0.25
        Assert.Equal(-0.25, SecurityPenalty.Next(0, 0, 1.0, false));
    }

    [Theory]
    [InlineData(0, 0, 1.0, true)]
    [InlineData(0, 0, 0.5, false)]
    [InlineData(-5, 0, 1.0, false)]
    [InlineData(5, 3, 0.3, true)]
    public void The_penalty_follows_the_formula_of_the_reference(double attacker, double target, double security, bool capsule)
    {
        var next = SecurityPenalty.Next(attacker, target, security, capsule);
        var modification = (capsule ? -0.25 : -0.025) * security * (1 + (target - attacker) / 100);
        Assert.Equal(Math.Round(Math.Clamp(attacker + (10 - attacker) * modification, -10, 10), 4, MidpointRounding.AwayFromZero), next);
        Assert.True(next < attacker);
    }

    [Fact]
    public void A_target_with_a_worse_standing_than_the_attacker_costs_less_and_a_better_one_costs_more()
    {
        Assert.True(SecurityPenalty.Next(0, -10, 1.0, false) > SecurityPenalty.Next(0, 0, 1.0, false));
        Assert.True(SecurityPenalty.Next(0, 10, 1.0, false) < SecurityPenalty.Next(0, 0, 1.0, false));
    }

    [Fact]
    public void Nothing_is_lost_in_null_security_and_the_floor_holds()
    {
        Assert.Equal(0, SecurityPenalty.Next(0, 0, 0, false));
        Assert.Equal(-10, SecurityPenalty.Next(-10, 0, 1.0, true));
        Assert.Equal(-10, SecurityPenalty.Clamp(-37));
        Assert.Equal(10, SecurityPenalty.Clamp(11.2));
        Assert.Equal(0, SecurityPenalty.Clamp(double.NaN));
    }

    [Fact]
    public void A_low_security_system_counts_for_at_least_five_hundredths()
    {
        Assert.Equal(0.05, SecurityPenalty.PseudoSecurity(0.01));
        Assert.Equal(0.3, SecurityPenalty.PseudoSecurity(0.3));
        Assert.Equal(1, SecurityPenalty.PseudoSecurity(1.7));
        Assert.Equal(0, SecurityPenalty.PseudoSecurity(-0.4));
    }

    [Theory]
    [InlineData(-0.12345, -0.1235)]
    [InlineData(0.00004, 0)]
    [InlineData(-3.00005, -3.0001)]
    public void A_status_is_kept_to_four_places(double value, double kept) => Assert.Equal(kept, SecurityPenalty.Clamp(value));
}
