using Eve.Insurgency;

namespace Eve.Insurgency.Tests;

/// <summary>The state machine and the 24.01 numbers, with no database.</summary>
public sealed class CampaignMachineTests
{
    private const long Hour = 3_600_000;
    private const long T0 = 1_700_006_400_000;

    private static Campaign NewCampaign(int pirateRequired = 5, int antipirateRequired = 5) =>
        CampaignMachine.Start(500011, 1, 30000001, [30000001, 30000002], 0, T0, pirateRequired, antipirateRequired);

    [Fact]
    public void Numbers_are_the_patch_24_01_values()
    {
        Assert.Equal(7 * 24 * Hour, InsurgencyRules.MaxLengthMs);
        Assert.Equal(3 * Hour, InsurgencyRules.ReinforcementBaseMs);
        Assert.Equal(Hour, InsurgencyRules.ReinforcementJitterMs);
        Assert.Equal(5, InsurgencyRules.MaxAmbitionModifier);
        Assert.Equal(10, InsurgencyRules.RequiredSystems(InsurgencyRules.MaxAmbitionModifier));
        Assert.Equal(5, InsurgencyRules.RequiredSystems(0));
        Assert.Equal(3, InsurgencyRules.SpreadCorruptionLevel);
        Assert.Equal(1_500_000, InsurgencyRules.MaxWinningContributionLp);
        Assert.Equal(1.0, InsurgencyRules.WinningMultiplier);
        Assert.Equal(0.4, InsurgencyRules.LosingMultiplier);
        Assert.Equal(0.45, InsurgencyRules.MaxPayoutShare);
    }

    [Theory]
    [InlineData(0.0, 2 * 3_600_000L)]
    [InlineData(0.5, 3 * 3_600_000L)]
    [InlineData(1.0, 4 * 3_600_000L)]
    public void A_fob_reinforcement_rolls_between_2_and_4_hours(double roll, long expected) =>
        Assert.Equal(expected, InsurgencyRules.ReinforcementMs(roll));

    [Fact]
    public void Ambition_moves_one_level_per_result_within_the_bounds()
    {
        Assert.Equal(5, InsurgencyRules.WinnerModifier(5));
        Assert.Equal(0, InsurgencyRules.LoserModifier(0));
        Assert.Equal(2, InsurgencyRules.LoserModifier(3));
        Assert.Equal(4, InsurgencyRules.WinnerModifier(3));
    }

    [Fact]
    public void A_new_campaign_is_forecasting_until_the_forecast_ends()
    {
        var campaign = NewCampaign();
        Assert.Equal(CampaignStates.Forecasting, campaign.State);
        Assert.Equal(T0 + 48 * Hour, campaign.StateExpiresMs);
        Assert.Equal(T0 + 7 * 24 * Hour, campaign.MaxEndsMs);

        var early = CampaignMachine.Advance(campaign, T0 + 47 * Hour);
        Assert.Equal(CampaignStates.Forecasting, early.Campaign.State);
        Assert.Empty(early.Transitions);

        var due = CampaignMachine.Advance(campaign, T0 + 48 * Hour);
        Assert.Equal(CampaignStates.Active, due.Campaign.State);
        Assert.Equal(T0 + 7 * 24 * Hour, due.Campaign.StateExpiresMs);
        Assert.Equal([new CampaignTransition(CampaignStates.Forecasting, CampaignStates.Active, T0 + 48 * Hour)], due.Transitions);
    }

    [Fact]
    public void A_tie_at_the_maximum_length_ends_with_no_winner_and_no_victory()
    {
        var active = CampaignMachine.Advance(NewCampaign(), T0 + 48 * Hour).Campaign;
        var ended = CampaignMachine.Advance(active, T0 + 7 * 24 * Hour);
        Assert.Equal(CampaignStates.NoWinner, ended.Campaign.State);
        Assert.Equal(T0 + 7 * 24 * Hour + 48 * Hour, ended.Campaign.StateExpiresMs);
        Assert.Equal([new CampaignTransition(CampaignStates.Active, CampaignStates.NoWinner, T0 + 7 * 24 * Hour)], ended.Transitions);
        Assert.Equal(0, ended.Campaign.PiratePointsScored);
        Assert.Equal(0, ended.Campaign.AntipiratePointsScored);

        var terminal = CampaignMachine.Advance(ended.Campaign, T0 + 7 * 24 * Hour + 48 * Hour);
        Assert.Equal(CampaignStates.Terminal, terminal.Campaign.State);
    }

    [Fact]
    public void A_late_timer_takes_every_passed_deadline_in_order()
    {
        var late = CampaignMachine.Advance(NewCampaign(), T0 + 10 * 24 * Hour);
        Assert.Equal(CampaignStates.Terminal, late.Campaign.State);
        Assert.Equal(
        [
            new CampaignTransition(CampaignStates.Forecasting, CampaignStates.Active, T0 + 48 * Hour),
            new CampaignTransition(CampaignStates.Active, CampaignStates.NoWinner, T0 + 7 * 24 * Hour),
            new CampaignTransition(CampaignStates.NoWinner, CampaignStates.Terminal, T0 + 9 * 24 * Hour),
        ], late.Transitions);
    }

    [Fact]
    public void Points_count_only_while_active()
    {
        var forecasting = NewCampaign();
        var refused = CampaignMachine.Score(forecasting, Side.Pirate, T0 + Hour);
        Assert.False(refused.Accepted);
        Assert.Equal(0, refused.Campaign.PiratePointsScored);
    }

    [Fact]
    public void The_side_that_reaches_its_required_count_wins_and_the_wind_down_starts()
    {
        var campaign = CampaignMachine.Advance(NewCampaign(pirateRequired: 2), T0 + 48 * Hour).Campaign;
        var first = CampaignMachine.Score(campaign, Side.Pirate, T0 + 49 * Hour);
        Assert.True(first.Accepted);
        Assert.Equal(CampaignStates.Active, first.Campaign.State);

        var won = CampaignMachine.Score(first.Campaign, Side.Pirate, T0 + 50 * Hour);
        Assert.Equal(CampaignStates.PirateWin, won.Campaign.State);
        Assert.Equal(T0 + 50 * Hour + 48 * Hour, won.Campaign.StateExpiresMs);
        Assert.Equal([new CampaignTransition(CampaignStates.Active, CampaignStates.PirateWin, T0 + 50 * Hour)], won.Transitions);

        var after = CampaignMachine.Score(won.Campaign, Side.Antipirate, T0 + 51 * Hour);
        Assert.False(after.Accepted);
        Assert.Equal(0, after.Campaign.AntipiratePointsScored);
    }

    [Fact]
    public void The_empire_side_at_its_count_keeps_the_campaign_active_until_the_fob_is_destroyed()
    {
        var campaign = CampaignMachine.Advance(NewCampaign(antipirateRequired: 1), T0 + 48 * Hour).Campaign;
        var exposed = CampaignMachine.Score(campaign, Side.Antipirate, T0 + 49 * Hour);
        Assert.True(exposed.Accepted);
        Assert.Empty(exposed.Transitions);
        Assert.Equal(CampaignStates.Active, exposed.Campaign.State);
        Assert.Equal(1, exposed.Campaign.AntipiratePointsScored);

        var won = CampaignMachine.FobDestroyed(exposed.Campaign, T0 + 50 * Hour);
        Assert.Equal(CampaignStates.AntipirateWin, won.Campaign.State);
        Assert.Equal([new CampaignTransition(CampaignStates.Active, CampaignStates.AntipirateWin, T0 + 50 * Hour)], won.Transitions);
        Assert.False(CampaignMachine.FobDestroyed(won.Campaign, T0 + 51 * Hour).Accepted);
    }
}
