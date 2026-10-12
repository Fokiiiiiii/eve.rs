using static Eve.Structures.StructureTimes;

namespace Eve.Structures.Tests;

/// <summary>Every arrow of the structure timeline, on the pure state machine and a clock the test moves (EVE University wiki "Vulnerability", client structures/const.py).</summary>
public class StateMachineTests
{
    private const long T0 = 1_700_006_400_000; // a Wednesday, 00:00 UTC
    private static readonly StructureRules Medium = StructureRules.Of(StructureSize.Medium, SecurityBand.HighSec);
    private static readonly StructureRules Large = StructureRules.Of(StructureSize.Large, SecurityBand.NullSec);

    private static StructureSnapshot Fresh() => new() { StructureId = 1 };

    private static StructureSnapshot Do(StructureSnapshot s, StructureRules r, StructureEvent e, long now, double roll = 0.5)
    {
        var t = StructureStateMachine.Apply(s, r, e, now, roll);
        Assert.Null(t.Refusal);
        return t.Next;
    }

    /// <summary>A working structure: shield stage, full power, one service online.</summary>
    private static StructureSnapshot Working() => Fresh() with { State = StructureState.ShieldVulnerable, Upkeep = UpkeepState.FullPower, CoreInstalled = true, OnlineServices = 1 };

    [Fact]
    public void Anchor_to_a_working_structure_follows_the_wiki_timeline()
    {
        var s = Do(Fresh(), Medium, new AnchorPlaced(), T0);
        Assert.Equal(StructureState.AnchorVulnerable, s.State);
        Assert.Equal(T0 + RepairMs, s.StateEndsMs);
        Assert.Equal(UpkeepState.LowPower, s.Upkeep);

        s = Do(s, Medium, new TimerDue(), T0 + RepairMs);
        Assert.Equal(StructureState.Anchoring, s.State);
        Assert.Equal(T0 + RepairMs + AnchoringMs, s.StateEndsMs);

        // before the deadline nothing happens
        var early = StructureStateMachine.Apply(s, Medium, new TimerDue(), T0 + RepairMs + AnchoringMs - 1);
        Assert.False(early.Changed);

        s = Do(s, Medium, new TimerDue(), T0 + RepairMs + AnchoringMs);
        Assert.Equal(StructureState.OnliningVulnerable, s.State);
        Assert.Equal(0, s.StateEndsMs); // no core: the hull alone, Low Power
        Assert.Equal(UpkeepState.LowPower, s.Upkeep);

        var coreAt = T0 + 2 * Day;
        s = Do(s, Medium, new CoreInserted(), coreAt);
        Assert.Equal(coreAt + RepairMs, s.StateEndsMs);

        var t = StructureStateMachine.Apply(s, Medium, new TimerDue(), coreAt + RepairMs);
        Assert.True(t.Repaired); // the onlining repair heals fully
        Assert.Equal(StructureState.OnliningVulnerable, t.Next.State); // and waits for a service
        Assert.Equal(0, t.Next.StateEndsMs);

        s = t.Next with { OnlineServices = 1 };
        s = Do(s, Medium, new ServiceOnline(), coreAt + RepairMs + 1000);
        Assert.Equal(StructureState.ShieldVulnerable, s.State);
        Assert.Equal(UpkeepState.FullPower, s.Upkeep);
        Assert.Equal(0, s.LowPowerSinceMs);
    }

    [Fact]
    public void A_service_online_during_the_onlining_repair_ends_the_wait_when_the_repair_is_done()
    {
        var s = Fresh() with { State = StructureState.OnliningVulnerable, CoreInstalled = true, StateEndsMs = T0 + RepairMs, OnlineServices = 1, LowPowerSinceMs = T0 - Day };
        s = Do(s, Medium, new ServiceOnline(), T0);
        Assert.Equal(StructureState.OnliningVulnerable, s.State); // the repair still runs
        Assert.Equal(UpkeepState.FullPower, s.Upkeep);
        s = Do(s, Medium, new TimerDue(), T0 + RepairMs);
        Assert.Equal(StructureState.ShieldVulnerable, s.State);
    }

    [Fact]
    public void A_late_timer_catches_up_from_each_deadline_not_from_now()
    {
        var anchored = Do(Fresh(), Medium, new AnchorPlaced(), T0);
        var t = StructureStateMachine.Apply(anchored, Medium, new TimerDue(), T0 + 3 * Day);
        Assert.Equal(StructureState.OnliningVulnerable, t.Next.State);
        Assert.Equal(T0 + RepairMs + AnchoringMs, t.Next.StateStartedMs);
    }

    [Fact]
    public void Damage_of_a_tenth_of_the_cap_pauses_the_anchor_countdown_and_a_light_hit_does_not()
    {
        var s = Do(Fresh(), Medium, new AnchorPlaced(), T0);
        var ends = s.StateEndsMs;
        s = Do(s, Medium, new Damaged(StructureLayer.Hull, false, false), T0 + 1000);
        Assert.Equal(ends, s.StateEndsMs);

        s = Do(s, Medium, new Damaged(StructureLayer.Hull, false, true), T0 + 2000);
        Assert.Equal(ends + RepairResumeMs, s.StateEndsMs);
        Assert.Equal(T0 + 2000, s.TimerPausedMs);
        Assert.True(s.IsPaused(T0 + 5000));
        Assert.False(s.IsPaused(T0 + 2000 + RepairResumeMs));

        // a second hard hit 4 s later holds it 4 s longer, not another 10
        s = Do(s, Medium, new Damaged(StructureLayer.Hull, false, true), T0 + 6000);
        Assert.Equal(ends + RepairResumeMs + 4000, s.StateEndsMs);

        // a hit after the hold has run out holds for the full delay again
        var later = T0 + 6000 + RepairResumeMs + 1000;
        s = Do(s, Medium, new Damaged(StructureLayer.Hull, false, true), later);
        Assert.Equal(ends + RepairResumeMs + 4000 + RepairResumeMs, s.StateEndsMs);
    }

    [Fact]
    public void An_anchoring_structure_is_destroyable_only_while_it_is_vulnerable()
    {
        var s = Do(Fresh(), Medium, new AnchorPlaced(), T0);
        var t = StructureStateMachine.Apply(s, Medium, new Damaged(StructureLayer.Hull, true, true), T0 + 1000);
        Assert.Equal(StructureRemoval.Destroyed, t.Removal);
        Assert.Equal(T0 + 1000, t.Next.DestroyedMs);
        Assert.Equal(0, StructureStateMachine.NextDue(t.Next, Medium));

        var anchoring = Do(s, Medium, new TimerDue(), T0 + RepairMs);
        var refused = StructureStateMachine.Apply(anchoring, Medium, new Damaged(StructureLayer.Hull, true, true), T0 + RepairMs + 1000);
        Assert.Equal("NOT_VULNERABLE", refused.Refusal);
        Assert.Equal(anchoring, refused.Next);
        Assert.Equal("STRUCTURE_GONE", StructureStateMachine.Apply(t.Next, Medium, new TimerDue(), T0 + Day).Refusal);
    }

    [Theory]
    [InlineData(StructureSize.Medium, SecurityBand.Wormhole, 216_000)]
    [InlineData(StructureSize.Medium, SecurityBand.NullSec, 302_400)]
    [InlineData(StructureSize.Medium, SecurityBand.LowSec, 302_400)]
    [InlineData(StructureSize.Medium, SecurityBand.HighSec, 388_800)]
    [InlineData(StructureSize.Large, SecurityBand.HighSec, 86_400)]
    [InlineData(StructureSize.ExtraLarge, SecurityBand.NullSec, 86_400)]
    public void Armor_reinforcement_lasts_by_size_and_space_with_the_exit_inside_the_jitter(StructureSize size, SecurityBand band, long seconds)
    {
        var rules = StructureRules.Of(size, band);
        var jitter = rules.ExitJitterMs;
        foreach (var roll in new[] { 0.0, 0.5, 0.999 })
        {
            var s = Do(Working(), rules, new Damaged(StructureLayer.Shield, true, false), T0, roll);
            Assert.Equal(StructureState.ArmorReinforce, s.State);
            var baseMs = T0 + seconds * Second;
            Assert.True(s.StateEndsMs >= baseMs, "an exit never comes before the duration has run");
            var day = Math.DivRem(s.StateEndsMs, Day, out var inDay);
            // the owner's hour 18, kept inside its UTC day
            var center = 18 * Hour;
            Assert.InRange(inDay, Math.Max(0, center - jitter), Math.Min(Day - 1, center + jitter));
            Assert.Equal(5, StructureStateMachine.WeekdayOf(day)); // the default reinforcement day is Saturday
        }
    }

    [Fact]
    public void The_exit_never_rolls_into_the_next_day_and_a_chosen_weekday_is_honoured()
    {
        // 23:00 with 3 hours of jitter: the latest exit is the last millisecond of the day
        var exit = StructureStateMachine.ExitTime(T0, StructureTimes.NoReinforceWeekday, 23, 3 * Hour, 0.999999);
        Assert.Equal(Math.DivRem(T0, Day, out _), Math.DivRem(exit, Day, out _));
        Assert.True(exit < (Math.DivRem(T0, Day, out _) + 1) * Day);

        for (var weekday = 0; weekday < 7; weekday++)
        {
            var at = StructureStateMachine.ExitTime(T0 + 3 * Hour, weekday, 6, Hour, 0.5);
            Assert.Equal(weekday, StructureStateMachine.WeekdayOf(at / Day));
            Assert.True(at >= T0 + 3 * Hour && at < T0 + 3 * Hour + 8 * Day);
        }
        // the day of T0 is a Wednesday (2)
        Assert.Equal(2, StructureStateMachine.WeekdayOf(T0 / Day));
    }

    [Fact]
    public void After_the_armor_reinforcement_the_repair_timer_heals_back_to_the_shield_stage()
    {
        var s = Do(Working(), Medium, new Damaged(StructureLayer.Shield, true, false), T0);
        var exit = s.StateEndsMs;
        s = Do(s, Medium, new TimerDue(), exit);
        Assert.Equal(StructureState.ArmorVulnerable, s.State);
        Assert.Equal(exit + RepairMs, s.StateEndsMs);

        var done = StructureStateMachine.Apply(s, Medium, new TimerDue(), exit + RepairMs);
        Assert.Equal(StructureState.ShieldVulnerable, done.Next.State);
        Assert.True(done.Repaired);
        Assert.Equal(0, done.Next.StateEndsMs);
        Assert.Equal(-1, done.Next.FightHour);
    }

    [Fact]
    public void Damage_in_the_armor_stage_holds_the_repair_timer()
    {
        var s = Do(Working(), Medium, new Damaged(StructureLayer.Shield, true, false), T0);
        s = Do(s, Medium, new TimerDue(), s.StateEndsMs);
        var ends = s.StateEndsMs;
        s = Do(s, Medium, new Damaged(StructureLayer.Armor, false, true), s.StateStartedMs + 60_000);
        Assert.Equal(ends + RepairResumeMs, s.StateEndsMs);
        var due = StructureStateMachine.Apply(s, Medium, new TimerDue(), ends);
        Assert.False(due.Repaired); // the hold pushed the repair out
    }

    [Fact]
    public void The_first_hit_in_the_shield_stage_starts_the_fifteen_minute_repair_and_it_heals()
    {
        var s = Working();
        Assert.Equal(0, StructureStateMachine.NextDue(s, Medium));
        s = Do(s, Medium, new Damaged(StructureLayer.Shield, false, false), T0);
        Assert.Equal(T0 + RepairMs, s.StateEndsMs);
        var done = StructureStateMachine.Apply(s, Medium, new TimerDue(), T0 + RepairMs);
        Assert.True(done.Repaired);
        Assert.Equal(StructureState.ShieldVulnerable, done.Next.State);
        Assert.Equal(0, done.Next.StateEndsMs);
    }

    [Fact]
    public void Medium_armor_loss_goes_straight_to_the_hull_stage_with_a_thirty_minute_repair_and_hull_loss_destroys()
    {
        var s = Working() with { State = StructureState.ArmorVulnerable, StateEndsMs = T0 + RepairMs };
        s = Do(s, Medium, new Damaged(StructureLayer.Armor, true, false), T0);
        Assert.Equal(StructureState.HullVulnerable, s.State);
        Assert.Equal(T0 + HullRepairMs, s.StateEndsMs);

        var done = StructureStateMachine.Apply(s, Medium, new TimerDue(), T0 + HullRepairMs);
        Assert.Equal(StructureState.ShieldVulnerable, done.Next.State);
        Assert.True(done.Repaired);

        var dead = StructureStateMachine.Apply(s, Medium, new Damaged(StructureLayer.Hull, true, false), T0 + 5000);
        Assert.Equal(StructureRemoval.Destroyed, dead.Removal);
    }

    [Fact]
    public void Large_armor_loss_reinforces_the_hull_on_the_hour_the_shield_fight_fixed()
    {
        var s = Do(Working() with { ReinforceWeekday = 255, ReinforceHour = 10 }, Large, new Damaged(StructureLayer.Shield, true, false), T0);
        Assert.Equal(10, s.FightHour);
        s = Do(s, Large, new TimerDue(), s.StateEndsMs);
        Assert.Equal(StructureState.ArmorVulnerable, s.State);

        // the owner's hour changed during the fight (and its 30 days passed): the fight keeps its hour
        s = s with { ReinforceHour = 20, PendingEffectiveMs = 0 };
        var armorGone = s.StateStartedMs + 120_000;
        var hull = Do(s, Large, new Damaged(StructureLayer.Armor, true, false), armorGone);
        Assert.Equal(StructureState.HullReinforce, hull.State);
        Assert.True(hull.StateEndsMs >= armorGone + 216_000 * Second);
        var inDay = hull.StateEndsMs % Day;
        Assert.InRange(inDay, 10 * Hour - Large.ExitJitterMs, 10 * Hour + Large.ExitJitterMs);

        var out1 = Do(hull, Large, new TimerDue(), hull.StateEndsMs);
        Assert.Equal(StructureState.HullVulnerable, out1.State);
        Assert.Equal(out1.StateStartedMs + HullRepairMs, out1.StateEndsMs);
    }

    [Theory]
    [InlineData(SecurityBand.Wormhole, 129_600)]
    [InlineData(SecurityBand.Pochven, 129_600)]
    [InlineData(SecurityBand.NullSec, 216_000)]
    [InlineData(SecurityBand.LowSec, 216_000)]
    [InlineData(SecurityBand.HighSec, 388_800)]
    public void Hull_reinforcement_lasts_by_space(SecurityBand band, long seconds)
    {
        var rules = StructureRules.Of(StructureSize.ExtraLarge, band);
        var s = Working() with { State = StructureState.ArmorVulnerable, FightWeekday = 255, FightHour = 12 };
        s = Do(s, rules, new Damaged(StructureLayer.Armor, true, false), T0, 0);
        Assert.Equal(StructureState.HullReinforce, s.State);
        Assert.True(s.StateEndsMs >= T0 + seconds * Second);
        Assert.True(s.StateEndsMs < T0 + seconds * Second + 2 * Day);
    }

    [Fact]
    public void A_flex_structure_skips_armor_reinforcement_and_waits_half_an_hour_for_the_hull()
    {
        var flex = StructureRules.Of(StructureSize.Flex, SecurityBand.NullSec);
        var s = Do(Working(), flex, new Damaged(StructureLayer.Shield, true, false), T0);
        Assert.Equal(StructureState.ArmorVulnerable, s.State);
        s = Do(s, flex, new Damaged(StructureLayer.Armor, true, false), T0 + 1000, 0);
        Assert.Equal(StructureState.HullReinforce, s.State);
        Assert.True(s.StateEndsMs >= T0 + 1000 + 1_800 * Second);
        Assert.InRange(s.StateEndsMs % Day, 18 * Hour - flex.ExitJitterMs, 18 * Hour + flex.ExitJitterMs);
    }

    [Fact]
    public void Low_power_skips_the_reinforcements_of_large_structures_but_not_of_medium_ones()
    {
        var low = Working() with { Upkeep = UpkeepState.LowPower, OnlineServices = 0, LowPowerSinceMs = T0 };

        var large = Do(low, Large, new Damaged(StructureLayer.Shield, true, false), T0 + 1000);
        Assert.Equal(StructureState.ArmorVulnerable, large.State);
        var hull = Do(large, Large, new Damaged(StructureLayer.Armor, true, false), T0 + 2000);
        Assert.Equal(StructureState.HullVulnerable, hull.State); // the hull falls vulnerable together with the armor

        var medium = Do(low, Medium, new Damaged(StructureLayer.Shield, true, false), T0 + 1000);
        Assert.Equal(StructureState.ArmorReinforce, medium.State);
    }

    [Fact]
    public void An_abandoned_structure_has_no_reinforcement_cycles()
    {
        var abandoned = Working() with { Upkeep = UpkeepState.Abandoned, OnlineServices = 0 };
        var s = Do(abandoned, Large, new Damaged(StructureLayer.Shield, true, false), T0);
        Assert.Equal(StructureState.ArmorVulnerable, s.State);
        s = Do(s, Large, new Damaged(StructureLayer.Armor, true, false), T0 + 1000);
        Assert.Equal(StructureState.HullVulnerable, s.State);
        Assert.Equal(StructureRemoval.Destroyed, StructureStateMachine.Apply(s, Large, new Damaged(StructureLayer.Hull, true, false), T0 + 2000).Removal);
        // the repair timers still run
        Assert.Equal(T0 + 1000 + HullRepairMs, s.StateEndsMs);
    }

    [Fact]
    public void Fuel_out_starts_low_power_and_seven_days_of_it_abandon_the_structure()
    {
        var s = Do(Working() with { OnlineServices = 0 }, Medium, new FuelOut(), T0);
        Assert.Equal(UpkeepState.LowPower, s.Upkeep);
        Assert.Equal(T0, s.LowPowerSinceMs);
        Assert.Equal(T0 + AbandonAfterMs, StructureStateMachine.NextDue(s, Medium));

        // a service online again ends Low Power and the clock
        var back = Do(s with { OnlineServices = 1 }, Medium, new ServiceOnline(), T0 + Day);
        Assert.Equal(UpkeepState.FullPower, back.Upkeep);
        Assert.Equal(0, back.LowPowerSinceMs);
        Assert.Equal(0, StructureStateMachine.NextDue(back, Medium));

        var early = StructureStateMachine.Apply(s, Medium, new TimerDue(), T0 + AbandonAfterMs - 1);
        Assert.Equal(UpkeepState.LowPower, early.Next.Upkeep);
        var abandoned = Do(s, Medium, new TimerDue(), T0 + AbandonAfterMs);
        Assert.Equal(UpkeepState.Abandoned, abandoned.Upkeep);
        Assert.Equal(0, StructureStateMachine.NextDue(abandoned, Medium));

        // any service online brings an abandoned structure back
        var revived = Do(abandoned with { OnlineServices = 1 }, Medium, new ServiceOnline(), T0 + 8 * Day);
        Assert.Equal(UpkeepState.FullPower, revived.Upkeep);

        var never = StructureRules.Of(StructureSize.Large, SecurityBand.NullSec, canBeAbandoned: false);
        Assert.Equal(0, StructureStateMachine.NextDue(s, never));
    }

    [Fact]
    public void Losing_one_of_several_services_keeps_full_power()
    {
        var s = Working() with { OnlineServices = 1 };
        var t = StructureStateMachine.Apply(s, Medium, new ServiceOffline(), T0);
        Assert.False(t.Changed);
    }

    [Fact]
    public void Unanchoring_takes_seven_days_can_be_cancelled_and_ends_with_the_structure_removed()
    {
        var s = Do(Working(), Large, new UnanchorStarted(), T0);
        Assert.Equal(T0 + UnanchoringMs, s.UnanchorEndsMs);
        Assert.Equal("ALREADY_UNANCHORING", StructureStateMachine.Apply(s, Large, new UnanchorStarted(), T0 + 1).Refusal);

        var cancelled = Do(s, Large, new UnanchorCancelled(), T0 + Day);
        Assert.Equal(0, cancelled.UnanchorEndsMs);
        Assert.Equal("NOT_UNANCHORING", StructureStateMachine.Apply(cancelled, Large, new UnanchorCancelled(), T0 + Day).Refusal);

        var done = StructureStateMachine.Apply(s, Large, new TimerDue(), T0 + UnanchoringMs);
        Assert.Equal(StructureRemoval.Unanchored, done.Removal);
        Assert.Equal(StructureState.Unanchored, done.Next.State);
        Assert.Equal(T0 + UnanchoringMs, done.Next.DestroyedMs);
        Assert.Equal(0, StructureStateMachine.NextDue(done.Next, Large));
    }

    [Fact]
    public void A_flex_structure_unanchors_in_forty_five_minutes()
    {
        var flex = StructureRules.Of(StructureSize.Flex, SecurityBand.NullSec);
        var s = Do(Working(), flex, new UnanchorStarted(), T0);
        Assert.Equal(T0 + 2_700 * Second, s.UnanchorEndsMs);
    }

    [Theory]
    [InlineData(StructureState.ArmorReinforce)]
    [InlineData(StructureState.ArmorVulnerable)]
    [InlineData(StructureState.HullReinforce)]
    [InlineData(StructureState.HullVulnerable)]
    [InlineData(StructureState.Anchoring)]
    [InlineData(StructureState.AnchorVulnerable)]
    public void Unanchoring_is_refused_in_the_states_that_cancel_it(int state)
    {
        var s = Working() with { State = state };
        Assert.Equal("UNANCHOR_NOT_ALLOWED", StructureStateMachine.Apply(s, Large, new UnanchorStarted(), T0).Refusal);
    }

    [Fact]
    public void Losing_the_shield_calls_off_an_unanchoring_in_progress()
    {
        var s = Do(Working(), Large, new UnanchorStarted(), T0);
        s = Do(s, Large, new Damaged(StructureLayer.Shield, true, false), T0 + Hour);
        Assert.Equal(StructureState.ArmorReinforce, s.State);
        Assert.Equal(0, s.UnanchorEndsMs);
    }

    [Fact]
    public void A_new_reinforcement_time_is_pending_for_thirty_days_then_takes_effect()
    {
        var s = Working();
        s = Do(s, Medium, new ReinforceTimeRequested(2, 9), T0);
        Assert.Equal((5, 18), (s.ReinforceWeekday, s.ReinforceHour));
        Assert.Equal((2, 9, T0 + ReinforceChangeDelayMs), (s.PendingWeekday, s.PendingHour, s.PendingEffectiveMs));
        Assert.Equal(T0 + ReinforceChangeDelayMs, StructureStateMachine.NextDue(s, Medium));

        // asking for the same again keeps the original effective time
        var again = Do(s, Medium, new ReinforceTimeRequested(2, 9), T0 + Day);
        Assert.Equal(s.PendingEffectiveMs, again.PendingEffectiveMs);

        // a fight before it takes effect still uses the old hour
        var fight = Do(s, Medium, new Damaged(StructureLayer.Shield, true, false), T0 + 29 * Day);
        Assert.Equal(18, fight.FightHour);

        var applied = Do(s, Medium, new TimerDue(), T0 + ReinforceChangeDelayMs);
        Assert.Equal((2, 9), (applied.ReinforceWeekday, applied.ReinforceHour));
        Assert.Equal(0, applied.PendingEffectiveMs);

        // asking for the current time again withdraws the pending one
        var withdrawn = Do(s, Medium, new ReinforceTimeRequested(5, 18), T0 + Day);
        Assert.Equal(0, withdrawn.PendingEffectiveMs);
    }

    [Theory]
    [InlineData(7, 5)]
    [InlineData(1, 24)]
    [InlineData(-1, 3)]
    public void An_invalid_reinforcement_time_is_refused(int weekday, int hour)
    {
        Assert.Equal("INVALID_REINFORCE_TIME", StructureStateMachine.Apply(Working(), Medium, new ReinforceTimeRequested(weekday, hour), T0).Refusal);
    }

    [Fact]
    public void Removing_the_core_stops_the_onlining_repair()
    {
        var s = Fresh() with { State = StructureState.OnliningVulnerable, CoreInstalled = true, StateEndsMs = T0 + RepairMs };
        s = Do(s, Medium, new CoreRemoved(), T0 + 1000);
        Assert.False(s.CoreInstalled);
        Assert.Equal(0, s.StateEndsMs);
    }

    [Fact]
    public void An_anchored_structure_cannot_be_anchored_again()
    {
        var s = Do(Fresh(), Medium, new AnchorPlaced(), T0);
        Assert.Equal("ALREADY_ANCHORED", StructureStateMachine.Apply(s, Medium, new AnchorPlaced(), T0 + 1).Refusal);
    }

    [Theory]
    [InlineData(1.0, StructureSize.Flex)]
    [InlineData(2.0, StructureSize.Medium)]
    [InlineData(3.0, StructureSize.Large)]
    [InlineData(4.0, StructureSize.ExtraLarge)]
    [InlineData(0.0, StructureSize.Flex)]
    public void The_size_class_comes_from_the_rig_size_attribute(double rigSize, StructureSize size)
    {
        Assert.Equal(size, StructureRules.SizeOfRigSize(rigSize));
    }

    private static readonly StructureRules Fob = StructureRules.Of(StructureSize.Fob, SecurityBand.LowSec, canBeAbandoned: false);

    [Fact]
    public void A_fob_is_invulnerable_until_exposed_then_one_reinforcement_two_to_four_hours_and_no_repair_before_the_kill()
    {
        var s = Do(Fresh(), Fob, new FobDeployed(), T0);
        Assert.Equal(StructureState.FobInvulnerable, s.State);
        Assert.Equal(UpkeepState.FullPower, s.Upkeep);
        Assert.Equal("NOT_VULNERABLE", StructureStateMachine.Apply(s, Fob, new Damaged(StructureLayer.Shield, true, false), T0).Refusal);

        s = Do(s, Fob, new FobExposed(), T0 + Hour);
        Assert.Equal(StructureState.ShieldVulnerable, s.State);

        var earliest = StructureStateMachine.Apply(s, Fob, new Damaged(StructureLayer.Shield, true, false), T0 + 2 * Hour, roll: 0).Next;
        Assert.Equal(StructureState.ArmorReinforce, earliest.State);
        Assert.Equal(T0 + 4 * Hour, earliest.StateEndsMs);
        var latest = StructureStateMachine.Apply(s, Fob, new Damaged(StructureLayer.Shield, true, false), T0 + 2 * Hour, roll: 0.999999).Next;
        Assert.InRange(latest.StateEndsMs - (T0 + 2 * Hour), 3 * Hour, 4 * Hour - 1);

        var armored = Do(earliest, Fob, new TimerDue(), earliest.StateEndsMs);
        Assert.Equal(StructureState.ArmorVulnerable, armored.State);
        Assert.Equal(0, armored.StateEndsMs);
        Assert.False(StructureStateMachine.Apply(armored, Fob, new TimerDue(), T0 + 100 * Hour).Changed);

        var hull = Do(armored, Fob, new Damaged(StructureLayer.Armor, true, false), T0 + 100 * Hour);
        Assert.Equal(StructureState.HullVulnerable, hull.State);
        Assert.Equal(0, hull.StateEndsMs);
        var gone = StructureStateMachine.Apply(hull, Fob, new Damaged(StructureLayer.Hull, true, false), T0 + 101 * Hour);
        Assert.Equal(StructureRemoval.Destroyed, gone.Removal);
        Assert.True(gone.Next.DestroyedMs > 0);
    }

    [Fact]
    public void A_fob_despawns_only_as_a_fob_and_a_structure_that_is_not_one_refuses_the_fob_events()
    {
        var s = Do(Fresh(), Fob, new FobDeployed(), T0);
        var despawned = StructureStateMachine.Apply(s, Fob, new FobDespawned(), T0 + Hour);
        Assert.Equal(StructureRemoval.Destroyed, despawned.Removal);
        Assert.Equal(T0 + Hour, despawned.Next.DestroyedMs);

        var medium = StructureStateMachine.Apply(Working(), Medium, new FobExposed(), T0);
        Assert.Equal("NOT_INVULNERABLE", medium.Refusal);
        Assert.Equal("NOT_A_FOB", StructureStateMachine.Apply(Fresh(), Medium, new FobDeployed(), T0).Refusal);
        Assert.Equal("NOT_A_FOB", StructureStateMachine.Apply(Working(), Medium, new FobDespawned(), T0).Refusal);
    }
}
