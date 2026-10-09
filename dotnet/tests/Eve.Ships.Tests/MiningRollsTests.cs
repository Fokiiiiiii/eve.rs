namespace Eve.Ships.Tests;

/// <summary>
/// A module's cycle carries three draws on the wire; the waste and the critical rounding reuse their branch's draw, rescaled to a fresh uniform value over the chance
/// that fired (a draw below a chance is uniform on [0, chance)).
/// </summary>
public class MiningRollsTests
{
    [Fact]
    public void A_module_reuses_its_waste_and_crit_draws_rescaled_to_the_share_of_the_chance_that_fired()
    {
        // a 34 % waste chance and a draw of 0.17 is half of it; a 5 % crit chance and a draw of 0.025 is half of it
        var rolls = MiningRolls.ForModule(0.17, 0.3, 0.025, 34, 0.05);

        Assert.Equal(0.17, rolls.Waste);
        Assert.Equal(0.3, rolls.NormalRounding);
        Assert.Equal(0.5, rolls.WasteRounding, 9);
        Assert.Equal(0.025, rolls.Crit);
        Assert.Equal(0.5, rolls.CritRounding, 9);
    }

    [Fact]
    public void A_chance_of_nothing_leaves_its_rounding_roll_at_zero_and_a_share_never_exceeds_one()
    {
        var rolls = MiningRolls.ForModule(0.9, 0.3, 0.9, 0, 0);

        Assert.Equal((0d, 0d), (rolls.WasteRounding, rolls.CritRounding));
        Assert.Equal(1d, MiningRolls.ForModule(0.9, 0.3, 0.9, 50, 0.5).WasteRounding, 9);
    }
}
