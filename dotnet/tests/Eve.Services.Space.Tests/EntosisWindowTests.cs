using Eve.Services.Space.Entosis;

namespace Eve.Services.Space.Tests;

/// <summary>
/// ST-9 f, the window rules (pure, no database): a custom window replaces the prime hour and the length, a day's length is locked at the midpoint of the window
/// the day before, a change takes effect after the next window, and a link without an end report scores only to the end of its in-progress cycle.
/// </summary>
public sealed class EntosisWindowTests
{
    private const long H = EntosisWindow.HourMs;
    private const long Day = 20_000L * EntosisWindow.DayMs;
    private const long Start = 1_700_000_000_000;
    private const long Hub = 1_040_000_000_903;
    private const int System = 30_100_032;

    private static readonly IReadOnlyDictionary<long, long> NoLocks = new Dictionary<long, long>();

    [Fact]
    public void A_setting_is_valid_only_inside_its_ranges()
    {
        Assert.True(new EntosisWindowSetting(0, 1).IsValid);
        Assert.True(new EntosisWindowSetting(23, 18).IsValid);
        Assert.False(new EntosisWindowSetting(24, 6).IsValid);
        Assert.False(new EntosisWindowSetting(3, 0).IsValid);
        Assert.False(new EntosisWindowSetting(3, 19).IsValid);
    }

    [Fact]
    public void A_custom_window_replaces_the_prime_hour_and_the_length()
    {
        var setting = new EntosisWindowSetting(10, 2);
        var newLocks = new List<EntosisWindowLock>();
        // 09:00 to 11:00, centred on 10:00; the default window of prime hour 22 (13:00 to 07:00) is not in force
        Assert.True(EntosisWindow.IsOpen(22, 1, Day + 9 * H, setting, NoLocks, newLocks));
        Assert.True(EntosisWindow.IsOpen(22, 1, Day + 10 * H, setting, NoLocks, newLocks));
        Assert.False(EntosisWindow.IsOpen(22, 1, Day + 11 * H + 1, setting, NoLocks, newLocks));
        Assert.False(EntosisWindow.IsOpen(22, 1, Day + 2 * H, setting, NoLocks, newLocks));
        Assert.True(EntosisWindow.IsOpen(22, 1, Day + 2 * H), "the default window, 02:00 inside it");
    }

    [Fact]
    public void A_day_length_is_locked_at_the_midpoint_of_the_window_the_day_before()
    {
        // today's window is centred on 12:00; tomorrow's (centred on Day+36h) is first evaluated after that midpoint, at ADM 1: 18 hours, locked
        var newLocks = new List<EntosisWindowLock>();
        Assert.True(EntosisWindow.IsOpen(12, 1, Day + 12 * H + 1, null, NoLocks, newLocks));
        var tomorrow = Day + 36 * H;
        Assert.Equal(18 * H, Assert.Single(newLocks, lockedWindow => lockedWindow.CentreMs == tomorrow).DurationMs);

        // the ADM is 6 now (a 3-hour default): tomorrow keeps the locked 18 hours, so 17:00 of Day+1 is inside it; unlocked it is not
        var locks = new Dictionary<long, long> { [tomorrow] = 18 * H };
        Assert.True(EntosisWindow.IsOpen(12, 6, Day + 41 * H, null, locks, new List<EntosisWindowLock>()));
        Assert.False(EntosisWindow.IsOpen(12, 6, Day + 41 * H));
    }

    [Fact]
    public void A_lock_is_not_taken_for_a_window_whose_previous_midpoint_is_still_ahead()
    {
        var newLocks = new List<EntosisWindowLock>();
        EntosisWindow.IsOpen(12, 1, Day + 1 * H, null, NoLocks, newLocks);
        Assert.DoesNotContain(newLocks, lockedWindow => lockedWindow.CentreMs == Day + 36 * H);
    }

    [Fact]
    public void A_change_takes_effect_after_the_window_that_is_open_or_next()
    {
        // the default window of 22:00 runs from 13:00 to 07:00 the next day: a change made at 22:00 takes effect at 07:00 the next day
        Assert.Equal(Day + 31 * H, EntosisWindow.NextWindowEnd(22, 1, Day + 22 * H, null, NoLocks, new List<EntosisWindowLock>()));
    }

    [Fact]
    public void A_reinforcement_exit_follows_the_owners_custom_window()
    {
        var setting = new EntosisWindowSetting(3, 2); // 02:00 to 04:00
        var completed = Day + 10 * H;
        var exit = EntosisWindow.ReinforcementExit(22, 1, completed, Hub, setting);
        Assert.True(exit >= completed + EntosisWindow.ReinforcementMs, "not before 48 hours");
        Assert.True(EntosisWindow.IsOpen(22, 1, exit, setting, NoLocks, new List<EntosisWindowLock>()), "inside the custom window");
        Assert.Equal(exit, EntosisWindow.ReinforcementExit(22, 1, completed, Hub, setting));
    }

    [Fact]
    public void A_link_without_an_end_report_scores_only_to_the_end_of_its_in_progress_cycle()
    {
        // the warm-up at Start is the link's last cycle; the next event is 8 minutes later: 5 minutes score, then the hub regenerates for 3 minutes
        var links = new[] { new EntosisLink(1, 9001, 7001, 5, EntosisSide.Attacker, Start, Start) };
        var state = new EntosisCaptureState(Hub, System, 0, Start, 1, null, EntosisPhase.Idle);
        var moved = EntosisRules.Advance(state, links, Start + 480_000);
        var expected = 300_000.0 / EntosisRules.BaseCaptureMs - 180_000.0 / EntosisRules.RegenMs;
        Assert.Equal(expected, moved.Progress, 9);
    }
}
