namespace Eve.Structures;

/// <summary>
/// The timeline of an Upwell structure as a pure function: (snapshot, rules, event, clock) gives the next snapshot and its next deadline. It reads no database and no clock;
/// <see cref="StructureTimers"/> loads the snapshot, applies an event, saves, announces and schedules the deadline in one transaction.
/// Rules: EVE University wiki "Vulnerability" and "Anchoring structures"; ids and repair times: client <c>structures/const.py</c>; see DEVIATIONS "Structure timeline (ST-0)".
/// </summary>
public static class StructureStateMachine
{
    private const int MaxSteps = 64;

    /// <summary>
    /// Applies <paramref name="e"/> at <paramref name="now"/>. <paramref name="roll"/> (0 to below 1) places an exit time inside its jitter window; the caller supplies the randomness.
    /// </summary>
    public static StructureTransition Apply(StructureSnapshot s, StructureRules rules, StructureEvent e, long now, double roll = 0.5)
    {
        if (s.DestroyedMs > 0)
        {
            return Refuse(s, "STRUCTURE_GONE");
        }
        var next = ApplyPendingReinforceTime(s, now);
        var transition = e switch
        {
            AnchorPlaced => s.State == StructureState.Unanchored
                ? Moved(next, Enter(next, StructureState.AnchorVulnerable, now, now + StructureTimes.RepairMs) with { Upkeep = UpkeepState.LowPower, LowPowerSinceMs = now })
                : Refuse(s, "ALREADY_ANCHORED"),
            Damaged damaged => OnDamaged(next, rules, damaged, now, roll),
            TimerDue => Advance(next, rules, now, roll),
            CoreInserted => Moved(next, OnCoreInserted(next, now)),
            CoreRemoved => Moved(next, next with
            {
                CoreInstalled = false,
                StateEndsMs = next.State == StructureState.OnliningVulnerable ? 0 : next.StateEndsMs,
            }),
            ServiceOnline => Moved(next, OnServiceOnline(next, now)),
            ServiceOffline or FuelOut => Moved(next, OnServicesLost(next, now)),
            UnanchorStarted => OnUnanchorStarted(next, rules, now),
            UnanchorCancelled => next.UnanchorEndsMs > 0 ? Moved(next, next with { UnanchorEndsMs = 0 }) : Refuse(s, "NOT_UNANCHORING"),
            ReinforceTimeRequested request => OnReinforceTimeRequested(next, request, now),
            _ => throw new ArgumentException($"unknown structure event {e.GetType().Name}", nameof(e)),
        };
        // a refusal returns the snapshot as it came in; the lazily applied reinforcement time is part of a successful event only
        return transition.Refusal is null ? transition with { Changed = !transition.Next.Equals(s) } : transition;
    }

    /// <summary>The next time something is due for the structure, or 0 when nothing is (a destroyed structure, or one that waits for a core or a service).</summary>
    public static long NextDue(StructureSnapshot s, StructureRules rules)
    {
        if (s.DestroyedMs > 0)
        {
            return 0;
        }
        var due = 0L;
        void Offer(long at) => due = at > 0 && (due == 0 || at < due) ? at : due;
        Offer(s.StateEndsMs);
        Offer(s.PendingEffectiveMs);
        Offer(s.UnanchorEndsMs);
        Offer(AbandonAt(s, rules));
        return due;
    }

    /// <summary>
    /// The first time at or after <paramref name="baseMs"/> that falls on the owner's reinforcement hour (and weekday, unless it is every day), moved by up to the jitter and kept
    /// inside that UTC day ("never rolls over to the next day"). An exit never comes before <paramref name="baseMs"/>.
    /// </summary>
    public static long ExitTime(long baseMs, int weekday, int hour, long jitterMs, double roll)
    {
        var firstDay = Math.DivRem(baseMs, StructureTimes.Day, out _);
        hour = Math.Clamp(hour, 0, 23);
        for (var k = 0; k < 15; k++)
        {
            var day = firstDay + k;
            if (weekday != StructureTimes.NoReinforceWeekday && WeekdayOf(day) != weekday)
            {
                continue;
            }
            var center = day * StructureTimes.Day + hour * StructureTimes.Hour;
            var high = Math.Min(center + jitterMs, (day + 1) * StructureTimes.Day - 1);
            var low = Math.Max(Math.Max(center - jitterMs, day * StructureTimes.Day), baseMs);
            if (high >= low)
            {
                return low + (long)(Math.Clamp(roll, 0, 0.999999) * (high - low));
            }
        }
        // a weekday outside 0-6 never matches; fall back to the plain hour
        return ExitTime(baseMs, StructureTimes.NoReinforceWeekday, hour, jitterMs, roll);
    }

    /// <summary>Monday is 0 (the client's <c>DAY_MONDAY</c>); 1 January 1970 was a Thursday.</summary>
    public static int WeekdayOf(long dayIndex) => (int)((dayIndex + 3) % 7);

    // ---- events ---------------------------------------------------------------------------------------------------------------------------------

    private static StructureTransition OnDamaged(StructureSnapshot s, StructureRules rules, Damaged hit, long now, double roll)
    {
        if (!StructureState.IsVulnerable(s.State))
        {
            return Refuse(s, "NOT_VULNERABLE");
        }
        var n = s;
        // a hit starts the repair countdown of the shield, armor and hull stages when none runs, and a hard hit holds it
        if (n.StateEndsMs == 0 && n.State is StructureState.ShieldVulnerable or StructureState.ArmorVulnerable or StructureState.HullVulnerable)
        {
            n = n with { StateEndsMs = now + RepairTime(n.State), StateStartedMs = now };
        }
        if (hit.PausesRepair && n.StateEndsMs > 0)
        {
            var oldHoldEnd = n.TimerPausedMs > 0 ? n.TimerPausedMs + StructureTimes.RepairResumeMs : 0;
            n = n with { StateEndsMs = n.StateEndsMs + now + StructureTimes.RepairResumeMs - Math.Max(oldHoldEnd, now), TimerPausedMs = now };
        }
        if (!hit.Depleted)
        {
            return Moved(s, n);
        }
        switch (hit.Layer)
        {
            case StructureLayer.Shield when n.State == StructureState.ShieldVulnerable:
                if (rules.CanArmorReinforce(n.Upkeep))
                {
                    var fighting = n with { FightWeekday = n.ReinforceWeekday, FightHour = n.ReinforceHour };
                    return Moved(s, Enter(fighting, StructureState.ArmorReinforce, now,
                        ExitTime(now + rules.ArmorReinforceMs, fighting.FightWeekday, fighting.FightHour, rules.ExitJitterMs, roll)));
                }
                return Moved(s, Enter(n, StructureState.ArmorVulnerable, now, now + StructureTimes.RepairMs));
            case StructureLayer.Armor when n.State == StructureState.ArmorVulnerable:
                if (rules.CanHullReinforce(n.Upkeep))
                {
                    var weekday = n.FightWeekday >= 0 ? n.FightWeekday : n.ReinforceWeekday;
                    var hour = n.FightHour >= 0 ? n.FightHour : n.ReinforceHour;
                    return Moved(s, Enter(n, StructureState.HullReinforce, now, ExitTime(now + rules.HullReinforceMs, weekday, hour, rules.ExitJitterMs, roll)));
                }
                return Moved(s, Enter(n, StructureState.HullVulnerable, now, now + StructureTimes.HullRepairMs));
            case StructureLayer.Hull when n.State is StructureState.AnchorVulnerable or StructureState.DeployVulnerable or StructureState.OnliningVulnerable
                    or StructureState.ArmorVulnerable or StructureState.HullVulnerable:
                return new StructureTransition(n with { DestroyedMs = now, StateEndsMs = 0, PendingEffectiveMs = 0, UnanchorEndsMs = 0 }, true, Removal: StructureRemoval.Destroyed);
            default:
                return Moved(s, n);
        }
    }

    /// <summary>Applies every deadline that has passed, in the order they fell due (a structure that was down when its deadline came catches up from the deadline, not from now).</summary>
    private static StructureTransition Advance(StructureSnapshot s, StructureRules rules, long now, double roll)
    {
        var n = s;
        var repaired = false;
        for (var step = 0; step < MaxSteps; step++)
        {
            var abandonAt = AbandonAt(n, rules);
            var at = new[] { n.PendingEffectiveMs, n.UnanchorEndsMs, abandonAt, n.StateEndsMs }.Where(t => t > 0 && t <= now).DefaultIfEmpty(0).Min();
            if (at == 0)
            {
                break;
            }
            if (n.PendingEffectiveMs == at)
            {
                n = ApplyPendingReinforceTime(n, at);
            }
            else if (n.UnanchorEndsMs == at)
            {
                return new StructureTransition(
                    n with { State = StructureState.Unanchored, StateStartedMs = at, StateEndsMs = 0, UnanchorEndsMs = 0, PendingEffectiveMs = 0, DestroyedMs = at },
                    true, Repaired: repaired, Removal: StructureRemoval.Unanchored);
            }
            else if (abandonAt == at)
            {
                n = n with { Upkeep = UpkeepState.Abandoned, LowPowerSinceMs = 0 };
            }
            else
            {
                (n, var healed) = OnStateTimer(n, rules, at);
                repaired |= healed;
            }
        }
        return new StructureTransition(n, !n.Equals(s), Repaired: repaired);
    }

    private static (StructureSnapshot Next, bool Repaired) OnStateTimer(StructureSnapshot s, StructureRules rules, long at)
    {
        switch (s.State)
        {
            case StructureState.AnchorVulnerable:
                return (Enter(s, StructureState.Anchoring, at, at + rules.AnchoringMs), false);
            case StructureState.Anchoring:
                return (Enter(s, StructureState.OnliningVulnerable, at, s.CoreInstalled ? at + StructureTimes.RepairMs : 0), false);
            case StructureState.OnliningVulnerable:
                // the onlining repair is done: with a service online the structure is a working one, otherwise it waits for one
                return s.OnlineServices > 0
                    ? (Enter(s, StructureState.ShieldVulnerable, at, 0) with { Upkeep = UpkeepState.FullPower, LowPowerSinceMs = 0, FightWeekday = -1, FightHour = -1 }, true)
                    : (s with { StateEndsMs = 0, TimerPausedMs = 0 }, true);
            case StructureState.ArmorReinforce:
                return (Enter(s, StructureState.ArmorVulnerable, at, at + StructureTimes.RepairMs), false);
            case StructureState.HullReinforce:
                return (Enter(s, StructureState.HullVulnerable, at, at + StructureTimes.HullRepairMs), false);
            default:
                // a repair countdown that ran out: the structure is whole again and vulnerable at its shield
                return (Enter(s, StructureState.ShieldVulnerable, at, 0) with { FightWeekday = -1, FightHour = -1 }, true);
        }
    }

    private static StructureSnapshot OnCoreInserted(StructureSnapshot s, long now) =>
        s.State == StructureState.OnliningVulnerable && s.StateEndsMs == 0
            ? s with { CoreInstalled = true, StateStartedMs = now, StateEndsMs = now + StructureTimes.RepairMs }
            : s with { CoreInstalled = true };

    private static StructureSnapshot OnServiceOnline(StructureSnapshot s, long now)
    {
        if (s.OnlineServices <= 0 || s.State is StructureState.Unanchored or StructureState.AnchorVulnerable or StructureState.Anchoring)
        {
            return s;
        }
        var n = s with { Upkeep = UpkeepState.FullPower, LowPowerSinceMs = 0 };
        // a structure whose onlining repair is done and that waited for a service becomes a working one
        return n.State == StructureState.OnliningVulnerable && n.CoreInstalled && n.StateEndsMs == 0
            ? Enter(n, StructureState.ShieldVulnerable, now, 0)
            : n;
    }

    private static StructureSnapshot OnServicesLost(StructureSnapshot s, long now) =>
        s.OnlineServices > 0 || s.Upkeep != UpkeepState.FullPower || s.State is StructureState.Unanchored or StructureState.AnchorVulnerable or StructureState.Anchoring
            ? s
            : s with { Upkeep = UpkeepState.LowPower, LowPowerSinceMs = now };

    private static StructureTransition OnUnanchorStarted(StructureSnapshot s, StructureRules rules, long now) =>
        s.UnanchorEndsMs > 0 ? Refuse(s, "ALREADY_UNANCHORING")
        : s.State is not (StructureState.OnliningVulnerable or StructureState.ShieldVulnerable) ? Refuse(s, "UNANCHOR_NOT_ALLOWED")
        : Moved(s, s with { UnanchorEndsMs = now + rules.UnanchoringMs });

    private static StructureTransition OnReinforceTimeRequested(StructureSnapshot s, ReinforceTimeRequested r, long now)
    {
        if (r.Hour is < 0 or > 23 || r.Weekday != StructureTimes.NoReinforceWeekday && r.Weekday is < 0 or > 6)
        {
            return Refuse(s, "INVALID_REINFORCE_TIME");
        }
        if (r.Weekday == s.ReinforceWeekday && r.Hour == s.ReinforceHour)
        {
            return Moved(s, s with { PendingWeekday = -1, PendingHour = -1, PendingEffectiveMs = 0 });
        }
        if (s.PendingEffectiveMs > 0 && r.Weekday == s.PendingWeekday && r.Hour == s.PendingHour)
        {
            return Moved(s, s);
        }
        return Moved(s, s with { PendingWeekday = r.Weekday, PendingHour = r.Hour, PendingEffectiveMs = now + StructureTimes.ReinforceChangeDelayMs });
    }

    // ---- helpers --------------------------------------------------------------------------------------------------------------------------------

    private static StructureSnapshot ApplyPendingReinforceTime(StructureSnapshot s, long now) =>
        s.PendingEffectiveMs > 0 && s.PendingEffectiveMs <= now
            ? s with { ReinforceWeekday = s.PendingWeekday, ReinforceHour = s.PendingHour, PendingWeekday = -1, PendingHour = -1, PendingEffectiveMs = 0 }
            : s;

    /// <summary>Enters a state: the timer starts at <paramref name="at"/> and ends at <paramref name="ends"/> (0 for none); an unanchoring in progress is called off in the states that cancel it.</summary>
    private static StructureSnapshot Enter(StructureSnapshot s, int state, long at, long ends) =>
        s with
        {
            State = state,
            StateStartedMs = at,
            StateEndsMs = ends,
            TimerPausedMs = 0,
            UnanchorEndsMs = StructureState.CancelsUnanchor(state) ? 0 : s.UnanchorEndsMs,
        };

    private static long AbandonAt(StructureSnapshot s, StructureRules rules) =>
        s.Upkeep == UpkeepState.LowPower && s.LowPowerSinceMs > 0 && rules.CanBeAbandoned ? s.LowPowerSinceMs + StructureTimes.AbandonAfterMs : 0;

    private static long RepairTime(int state) => state == StructureState.HullVulnerable ? StructureTimes.HullRepairMs : StructureTimes.RepairMs;

    private static StructureTransition Moved(StructureSnapshot before, StructureSnapshot after) => new(after, !after.Equals(before));

    private static StructureTransition Refuse(StructureSnapshot s, string refusal) => new(s, false, refusal);
}
