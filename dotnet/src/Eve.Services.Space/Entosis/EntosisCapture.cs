namespace Eve.Services.Space.Entosis;

public enum EntosisSide : byte
{
    Attacker = 0,
    Defender = 1,
}

/// <summary>
/// </summary>
public enum EntosisPhase : byte
{
    Idle = 0,
    Capturing = 1,
    Captured = 2,
    Reinforced = 3,
    Destroyed = 4,
}

/// <summary>
/// A link of a hub: a module that has settled a cycle for it. The first cycle ends the warm-up (the link is recorded then), so only the cycles after it count.
/// A link is active at a time when its last settled cycle is within two cycles of that time.
/// </summary>
public sealed record EntosisLink(long ModuleId, long CharacterId, long CorporationId, long AllianceId, EntosisSide Side, long LinkedMs, long LastCycleMs);

/// <summary>
/// A hub's capture at a checkpoint: the attacker's progress (0 to 1), the time it was checked, the activity multiplier that scales the capture time (fixed when
/// </summary>
public sealed record EntosisCaptureState(
    long HubId, int SolarSystemId, double Progress, long CheckpointMs, double ActivityDefenseMultiplier, long? DefenderAllianceId, EntosisPhase Phase, int PrimeHour = 0);

public sealed record EntosisWindowSetting(int CentreHour, int LengthHours)
{
    public bool IsValid => CentreHour is >= 0 and <= 23 && LengthHours is >= 1 and <= 18;
}

public sealed record EntosisWindowLock(long CentreMs, long DurationMs);

public static class EntosisRules
{
    /// <summary>The link's cycle (SDE 34593, duration 73): 300 000 ms.</summary>
    public const long CycleMs = 300_000;

    public const double BaseCaptureMs = 595_000;

    public const double RegenMs = 3_000_000;

    /// <summary>Whether a link counts at a time: its last settled cycle is within two cycles before it (a link that stopped without a word stops counting).</summary>
    public static bool IsActive(EntosisLink link, long at) => link.LastCycleMs > at - 2 * CycleMs;

    public static double CaptureMs(double activityDefenseMultiplier) => BaseCaptureMs * activityDefenseMultiplier;

    /// <summary>A completed capture: a neutral hub captured, an owned hub reinforced, or a hub destroyed by its command node stage. Nothing more moves it.</summary>
    public static bool IsDone(EntosisPhase phase) => phase is EntosisPhase.Captured or EntosisPhase.Reinforced or EntosisPhase.Destroyed;

    /// <summary>
    /// </summary>
    public static bool IsVulnerable(EntosisHubReading reading, EntosisCaptureState? stored, long nowMs) =>
        IsVulnerable(reading, stored, EntosisWindow.IsOpen(reading.PrimeHour, reading.ActivityDefenseMultiplier, nowMs));

    public static bool IsVulnerable(EntosisHubReading reading, EntosisCaptureState? stored, bool windowOpen) =>
        reading.DefenderAllianceId is null
        || stored is { Phase: EntosisPhase.Capturing }
        || windowOpen;

    /// <summary>
    /// Moves a hub's progress from its checkpoint to <paramref name="nowMs"/>, with the links that were active at the checkpoint. An attacker alone scores (one
    /// link is enough, and more links do not speed it up). An attacker with a defender paused: nothing moves. Without an attacker the hub regenerates toward
    /// </summary>
    public static EntosisCaptureState Advance(EntosisCaptureState state, IReadOnlyList<EntosisLink> links, long nowMs)
    {
        if (nowMs <= state.CheckpointMs || IsDone(state.Phase))
        {
            return nowMs > state.CheckpointMs ? state with { CheckpointMs = nowMs } : state;
        }
        var elapsed = nowMs - state.CheckpointMs;
        var attackUntil = ActiveUntil(links, EntosisSide.Attacker, state.CheckpointMs);
        var defending = links.Any(link => link.Side == EntosisSide.Defender && IsActive(link, state.CheckpointMs));
        var progress = state.Progress;
        if (attackUntil is { } until)
        {
            if (!defending)
            {
                // a link scores to the end of its in-progress cycle; a module that stopped without its end report scores no further
                var scoredMs = Math.Min(elapsed, until - state.CheckpointMs);
                progress += scoredMs / CaptureMs(state.ActivityDefenseMultiplier);
                progress -= (elapsed - scoredMs) / RegenMs;
            }
        }
        else
        {
            progress -= elapsed / RegenMs;
        }
        // the rounding of a long sum of small steps must not leave a capture a nanoscale short of its end
        progress = progress >= 1 - 1e-9 ? 1 : Math.Clamp(progress, 0, 1);
        var phase = progress >= 1 ? EntosisPhase.Captured : progress > 0 ? EntosisPhase.Capturing : EntosisPhase.Idle;
        return state with { Progress = progress, CheckpointMs = nowMs, Phase = phase };
    }

    /// <summary>The end of the scoring of the attacker links active at <paramref name="at"/>: the end of their in-progress cycle (null when none is active).</summary>
    internal static long? ActiveUntil(IReadOnlyList<EntosisLink> links, EntosisSide side, long at)
    {
        long? until = null;
        foreach (var link in links)
        {
            if (link.Side == side && IsActive(link, at))
            {
                var end = link.LastCycleMs + CycleMs;
                until = until is { } known ? Math.Max(known, end) : end;
            }
        }
        return until;
    }

    /// <summary>
    /// When the capture completes if the links stay as they are at <paramref name="nowMs"/> (the state is at that time): only an attacker alone scores. Null when
    /// it does not score or is already captured.
    /// </summary>
    public static long? CompletionAt(EntosisCaptureState state, IReadOnlyList<EntosisLink> links, long nowMs)
    {
        if (IsDone(state.Phase))
        {
            return null;
        }
        var attacking = links.Any(link => link.Side == EntosisSide.Attacker && IsActive(link, nowMs));
        var defending = links.Any(link => link.Side == EntosisSide.Defender && IsActive(link, nowMs));
        if (!attacking || defending)
        {
            return null;
        }
        return nowMs + (long)Math.Ceiling((1 - state.Progress) * CaptureMs(state.ActivityDefenseMultiplier));
    }
}

/// <summary>
/// </summary>
public static class EntosisWindow
{
    public const long HourMs = 3_600_000;
    public const long DayMs = 86_400_000;

    public const long ReinforcementMs = 48 * HourMs;

    public static long DefaultDurationMs(double activityDefenseMultiplier) =>
        Math.Max(HourMs, (long)Math.Round(18.0 * HourMs / Math.Clamp(activityDefenseMultiplier, 1, 6)));

    /// <summary>The window of the day that starts at <paramref name="dayStartMs"/> (UTC midnight), as a start and an end (both in milliseconds), at the prime hour.</summary>
    public static (long StartMs, long EndMs) Of(int primeHour, double activityDefenseMultiplier, long dayStartMs) =>
        OfCentre(dayStartMs + primeHour * HourMs, DefaultDurationMs(activityDefenseMultiplier));

    /// <summary>A window centred on <paramref name="centreMs"/> with the given length.</summary>
    public static (long StartMs, long EndMs) OfCentre(long centreMs, long durationMs)
    {
        var startMs = centreMs - durationMs / 2;
        return (startMs, startMs + durationMs);
    }

    /// <summary>
    /// taken at the first evaluation at or after the previous window's centre, from the setting and the multiplier then (EVE.js <c>lockNextWindowIfDue</c>). A
    /// window whose previous centre is still ahead is not locked: its length is what the setting and the multiplier give now, and nothing is stored for it.
    /// </summary>
    public static long DurationAt(
        long centreMs, EntosisWindowSetting? setting, double activityDefenseMultiplier, long nowMs, IReadOnlyDictionary<long, long> locks,
        ICollection<EntosisWindowLock> newLocks)
    {
        if (locks.TryGetValue(centreMs, out var locked))
        {
            return locked;
        }
        var durationMs = setting is null ? DefaultDurationMs(activityDefenseMultiplier) : setting.LengthHours * HourMs;
        if (centreMs - DayMs <= nowMs)
        {
            newLocks.Add(new EntosisWindowLock(centreMs, durationMs));
        }
        return durationMs;
    }

    private static int CentreHourOf(int primeHour, EntosisWindowSetting? setting) => setting?.CentreHour ?? primeHour;

    public static bool IsOpen(int primeHour, double activityDefenseMultiplier, long nowMs) =>
        IsOpen(primeHour, activityDefenseMultiplier, nowMs, null, new Dictionary<long, long>(), new List<EntosisWindowLock>());

    public static bool IsOpen(
        int primeHour, double activityDefenseMultiplier, long nowMs, EntosisWindowSetting? setting, IReadOnlyDictionary<long, long> locks,
        ICollection<EntosisWindowLock> newLocks)
    {
        var centreHour = CentreHourOf(primeHour, setting);
        var today = nowMs / DayMs * DayMs;
        var open = false;
        // every candidate is evaluated, not only until one is open: the window of tomorrow is locked at today's midpoint whether or not today's is open
        for (var day = -1; day <= 1; day++)
        {
            var centreMs = today + day * DayMs + centreHour * HourMs;
            var (startMs, endMs) = OfCentre(centreMs, DurationAt(centreMs, setting, activityDefenseMultiplier, nowMs, locks, newLocks));
            open |= nowMs >= startMs && nowMs <= endMs;
        }
        return open;
    }

    /// <summary>
    /// immediately: it takes effect after the next vulnerability window").
    /// </summary>
    public static long NextWindowEnd(
        int primeHour, double activityDefenseMultiplier, long nowMs, EntosisWindowSetting? setting, IReadOnlyDictionary<long, long> locks,
        ICollection<EntosisWindowLock> newLocks)
    {
        var centreHour = CentreHourOf(primeHour, setting);
        var today = nowMs / DayMs * DayMs;
        long? next = null;
        for (var day = -1; day <= 2; day++)
        {
            var centreMs = today + day * DayMs + centreHour * HourMs;
            var (_, endMs) = OfCentre(centreMs, DurationAt(centreMs, setting, activityDefenseMultiplier, nowMs, locks, newLocks));
            if (endMs > nowMs && (next is null || endMs < next))
            {
                next = endMs;
            }
        }
        return next ?? throw new InvalidOperationException("no vulnerability window ends after the time");
    }

    public static long NextWindowStart(int primeHour, double activityDefenseMultiplier, long nowMs, EntosisWindowSetting? setting)
    {
        var centreHour = CentreHourOf(primeHour, setting);
        var durationMs = setting is null ? DefaultDurationMs(activityDefenseMultiplier) : setting.LengthHours * HourMs;
        var today = nowMs / DayMs * DayMs;
        long? next = null;
        for (var day = -1; day <= 2; day++)
        {
            var (startMs, _) = OfCentre(today + day * DayMs + centreHour * HourMs, durationMs);
            if (startMs > nowMs && (next is null || startMs < next))
            {
                next = startMs;
            }
        }
        return next ?? throw new InvalidOperationException("no vulnerability window starts after the time");
    }

    /// <summary>
    /// When an owned hub that completed its capture at <paramref name="completedMs"/> comes out of reinforcement: not before 48 hours later, at a point of the
    /// The point is drawn from <see cref="Draw"/>, so a replayed completion gives the same time.
    /// </summary>
    public static long ReinforcementExit(int primeHour, double activityDefenseMultiplier, long completedMs, long hubId) =>
        ReinforcementExit(primeHour, activityDefenseMultiplier, completedMs, hubId, null);

    public static long ReinforcementExit(int primeHour, double activityDefenseMultiplier, long completedMs, long hubId, EntosisWindowSetting? setting)
    {
        var centreHour = CentreHourOf(primeHour, setting);
        var durationMs = setting is null ? DefaultDurationMs(activityDefenseMultiplier) : setting.LengthHours * HourMs;
        var earliestMs = completedMs + ReinforcementMs;
        var today = earliestMs / DayMs * DayMs;
        for (var day = -1; day <= 2; day++)
        {
            var (startMs, endMs) = OfCentre(today + day * DayMs + centreHour * HourMs, durationMs);
            if (endMs <= earliestMs)
            {
                continue;
            }
            var fromMs = Math.Max(startMs, earliestMs);
            var fraction = Draw(hubId, completedMs) / 18446744073709551616.0;
            return Math.Min(endMs - 1, fromMs + (long)(fraction * (endMs - fromMs)));
        }
        throw new InvalidOperationException("no vulnerability window follows the reinforcement");
    }

    /// <summary>A deterministic draw of 64 bits from the hub and the completion time (the SplitMix64 finaliser).</summary>
    public static ulong Draw(long hubId, long completedMs)
    {
        unchecked
        {
            var x = (ulong)hubId * 0x9E3779B97F4A7C15UL + (ulong)completedMs;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
