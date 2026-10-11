using Eve.Npc;
using Eve.Services.Space;

namespace Eve.Services.Space.Tests;

public sealed class EssRulesTests
{
    [Fact]
    public void A_bounty_in_a_system_with_an_ess_splits_forty_percent_to_the_main_bank_to_the_cent()
    {
        Assert.Equal((600.60m, 400.40m), EssRules.SplitBounty(1001m, essOnline: true));
        Assert.Equal((600.00m, 400.00m), EssRules.SplitBounty(1000m, essOnline: true));
    }

    [Fact]
    public void A_bounty_without_an_ess_is_all_the_pilots()
    {
        Assert.Equal((1001m, 0m), EssRules.SplitBounty(1001m, essOnline: false));
    }

    [Fact]
    public void A_thief_takes_four_fifths_of_the_bank_and_an_empty_bank_pays_nothing()
    {
        Assert.Equal(800m, EssRules.ThiefPayout(1000m));
        Assert.Equal(320.32m, EssRules.ThiefPayout(400.40m));
        Assert.Equal(0m, EssRules.ThiefPayout(0m));
    }

    [Fact]
    public void The_reserve_payout_curve_rises_to_its_window_and_falls_after_it()
    {
        // 15 pulses: the window is at pulse 10 (15 / 1.5)
        Assert.Equal(0d, EssRules.ReservePulsePayout(5, 15, maxPayout: 0));
        Assert.Equal(25d, EssRules.ReservePulsePayout(5, 15, maxPayout: 100), 9);
        Assert.Equal(100d, EssRules.ReservePulsePayout(10, 15, maxPayout: 100), 9);
        Assert.Equal(100 * (10.0 / 15) * (10.0 / 15), EssRules.ReservePulsePayout(15, 15, maxPayout: 100), 9);
    }

    [Fact]
    public void A_pulse_outside_the_bank_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EssRules.ReservePulsePayout(0, 15));
        Assert.Throws<ArgumentOutOfRangeException>(() => EssRules.ReservePulsePayout(16, 15));
    }

    [Fact]
    public void The_window_and_autopay_states_are_the_client_ones()
    {
        Assert.Equal(EssRules.ReserveWindowEarly, EssRules.ReserveWindowState(15, 15));
        Assert.Equal(EssRules.ReserveWindowBest, EssRules.ReserveWindowState(6, 15));
        Assert.Equal(EssRules.ReserveWindowLate, EssRules.ReserveWindowState(5, 15));
        Assert.Equal(EssRules.ReserveWindowInactive, EssRules.ReserveWindowState(0, 15));
        Assert.Equal(EssRules.AutopayStateLong, EssRules.AutopayWindowState(0m, linked: true));
        Assert.Equal(EssRules.AutopayStateShort, EssRules.AutopayWindowState(10m, linked: true));
        Assert.Equal(EssRules.AutopayStateMedium, EssRules.AutopayWindowState(10m, linked: false));
    }

    [Fact]
    public void The_sde_reserve_keys_open_fifteen_or_forty_five_pulses_and_nothing_else()
    {
        Assert.Equal(8, EssReserveKeys.All.Count);
        Assert.True(EssReserveKeys.TryGetPulses(57714, out var fifteen));
        Assert.Equal(15, fifteen);
        Assert.True(EssReserveKeys.TryGetPulses(57715, out var fortyFive));
        Assert.Equal(45, fortyFive);
        Assert.False(EssReserveKeys.TryGetPulses(1, out _));
    }
}
