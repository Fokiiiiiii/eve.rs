using Eve.Services.Space.Entosis;

namespace Eve.Services.Space.Tests;

/// <summary>
/// attacker pauses the capture, a defender alone regenerates it, and a link stops counting two cycles after its last cycle.
/// </summary>
public sealed class EntosisCaptureTests
{
    private const long Hub = 1_040_000_000_901;
    private const long Start = 1_700_000_000_000;
    private const int System = 30_100_032;

    private static EntosisLink Link(long module, EntosisSide side, long lastCycleMs, long alliance = 5) =>
        new(module, 9001, 7001, alliance, side, Start, lastCycleMs);

    private static EntosisCaptureState State(double progress = 0, double adm = 1, long? owner = null, long checkpoint = Start) =>
        new(Hub, System, progress, checkpoint, adm, owner,
            progress >= 1 ? EntosisPhase.Captured : progress > 0 ? EntosisPhase.Capturing : EntosisPhase.Idle);

    [Fact]
    public void An_attacker_alone_captures_a_neutral_hub_in_the_base_time_of_595_seconds()
    {
        var links = new[] { Link(1, EntosisSide.Attacker, Start + EntosisRules.CycleMs) };

        var half = EntosisRules.Advance(State(), links, Start + 297_500);
        Assert.Equal(0.5, half.Progress, 9);
        Assert.Equal(EntosisPhase.Capturing, half.Phase);

        var captured = EntosisRules.Advance(State(), links, Start + 595_000);
        Assert.Equal(1, captured.Progress);
        Assert.Equal(EntosisPhase.Captured, captured.Phase);
    }

    [Fact]
    public void The_activity_multiplier_scales_the_capture_time_and_more_links_do_not_speed_it_up()
    {
        var one = new[] { Link(1, EntosisSide.Attacker, Start + EntosisRules.CycleMs) };
        var two = one.Append(Link(2, EntosisSide.Attacker, Start + EntosisRules.CycleMs, alliance: 6)).ToArray();

        Assert.Equal(0.5, EntosisRules.Advance(State(adm: 2), one, Start + 595_000).Progress, 9);
        Assert.Equal(EntosisRules.Advance(State(adm: 2), one, Start + 595_000).Progress, EntosisRules.Advance(State(adm: 2), two, Start + 595_000).Progress, 12);
    }

    [Fact]
    public void A_defender_with_the_attacker_pauses_the_capture()
    {
        var links = new[]
        {
            Link(1, EntosisSide.Attacker, Start + 3_600_000),
            Link(2, EntosisSide.Defender, Start + 3_600_000, alliance: 7),
        };

        var paused = EntosisRules.Advance(State(progress: 0.25, owner: 7), links, Start + 3_600_000);

        Assert.Equal(0.25, paused.Progress, 12);
    }

    [Fact]
    public void A_defender_alone_regenerates_a_capture_over_50_minutes()
    {
        var links = new[] { Link(2, EntosisSide.Defender, Start + 750_000, alliance: 7) };

        var regenerated = EntosisRules.Advance(State(progress: 0.5, owner: 7), links, Start + 750_000);

        Assert.Equal(0.25, regenerated.Progress, 9);
        Assert.Equal(EntosisPhase.Capturing, regenerated.Phase);
    }

    [Fact]
    public void A_link_whose_last_cycle_is_two_cycles_old_does_not_count()
    {
        // at the checkpoint the attacker's last cycle is more than two cycles back: it does not score
        var stale = new[] { Link(1, EntosisSide.Attacker, Start) };
        var checkpoint = Start + 2 * EntosisRules.CycleMs + 1;

        var moved = EntosisRules.Advance(State(progress: 0.5, checkpoint: checkpoint), stale, checkpoint + 100_000);

        Assert.True(moved.Progress < 0.5, "a stale attacker does not score; the hub regenerates");
    }

    [Fact]
    public void The_completion_is_the_remaining_time_of_an_attacker_alone_and_none_when_defended_or_captured()
    {
        var attacker = new[] { Link(1, EntosisSide.Attacker, Start) };
        Assert.Equal(Start + 595_000, EntosisRules.CompletionAt(State(), attacker, Start));
        Assert.Equal(Start + 595_000, EntosisRules.CompletionAt(State(progress: 0.5, adm: 2), attacker, Start));

        var defended = attacker.Append(Link(2, EntosisSide.Defender, Start, alliance: 7)).ToArray();
        Assert.Null(EntosisRules.CompletionAt(State(), defended, Start));
        Assert.Null(EntosisRules.CompletionAt(State(progress: 1), attacker, Start));
    }

    private const long Day = 1_699_920_000_000;

    [Fact]
    public void The_owners_window_is_18_hours_divided_by_the_adm_and_centred_on_the_prime_hour()
    {
        // ADM 1: 18 hours, 13:00 to 07:00 around a 22:00 prime hour; ADM 6: 3 hours, 20:30 to 23:30
        Assert.Equal((Day + 13 * EntosisWindow.HourMs, Day + 31 * EntosisWindow.HourMs), EntosisWindow.Of(22, 1, Day));
        Assert.Equal((Day + 20 * EntosisWindow.HourMs + 1_800_000, Day + 23 * EntosisWindow.HourMs + 1_800_000), EntosisWindow.Of(22, 6, Day));

        Assert.True(EntosisWindow.IsOpen(22, 1, Day + 13 * EntosisWindow.HourMs));
        Assert.False(EntosisWindow.IsOpen(22, 1, Day + 13 * EntosisWindow.HourMs - 1));
        // the window of the day before covers the early hours of the day (a window crosses midnight)
        Assert.True(EntosisWindow.IsOpen(22, 1, Day + 3 * EntosisWindow.HourMs));
        Assert.False(EntosisWindow.IsOpen(22, 6, Day + 12 * EntosisWindow.HourMs));
    }

    [Fact]
    public void A_neutral_hub_has_no_window_and_a_partly_captured_owned_hub_stays_vulnerable_after_its_window()
    {
        var closed = Day + 23 * EntosisWindow.HourMs; // 23:00: outside a 12:00 prime hour's window (03:00 to 21:00)
        var neutral = new EntosisHubReading(Hub, System, null, 1, 0);
        var owned = new EntosisHubReading(Hub, System, 7, 1, 12);

        Assert.True(EntosisRules.IsVulnerable(neutral, null, closed));
        Assert.False(EntosisRules.IsVulnerable(owned, null, closed));
        Assert.True(EntosisRules.IsVulnerable(owned, null, Day + 12 * EntosisWindow.HourMs));
        Assert.True(EntosisRules.IsVulnerable(owned, State(progress: 0.4, owner: 7), closed));
        Assert.False(EntosisRules.IsVulnerable(owned, State(progress: 0, owner: 7), closed));
    }

    [Fact]
    public void A_reinforcement_ends_no_earlier_than_48_hours_at_a_point_of_the_next_window_and_is_replay_stable()
    {
        // completed at 10:00 on day 0 with a 12:00 prime hour (window 03:00 to 21:00): the 48 hours end at 10:00 on day 2, inside that day's window
        var completed = Day + 10 * EntosisWindow.HourMs;
        var earliest = completed + EntosisWindow.ReinforcementMs;

        var exit = EntosisWindow.ReinforcementExit(12, 1, completed, Hub);

        Assert.InRange(exit, earliest, Day + 2 * EntosisWindow.DayMs + 21 * EntosisWindow.HourMs - 1);
        Assert.Equal(exit, EntosisWindow.ReinforcementExit(12, 1, completed, Hub));
        Assert.True(EntosisWindow.IsOpen(12, 1, exit));
    }
}
