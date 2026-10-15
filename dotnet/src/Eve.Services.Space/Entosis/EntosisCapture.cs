namespace Eve.Services.Space.Entosis;

/// <summary>The side of a link: the attacker's (an alliance other than the owner's), or the defender's (the owner's, which repairs the hub).</summary>
public enum EntosisSide : byte
{
    Attacker = 0,
    Defender = 1,
}

/// <summary>
/// Where a hub's capture is: nothing yet, a capture in progress, or completed. A neutral hub's completion is captured (its handover is ST-9 d); an owned hub's is
/// reinforced, with its reinforcement timer (the wiki: about 48 hours, then the next vulnerability window).
/// </summary>
public enum EntosisPhase : byte
{
    Idle = 0,
    Capturing = 1,
    Captured = 2,
    Reinforced = 3,
}

/// <summary>
/// A link of a hub: a module that has settled a cycle for it. The first cycle ends the warm-up (the link is recorded then), so only the cycles after it count.
/// A link is active at a time when its last settled cycle is within two cycles of that time.
/// </summary>
public sealed record EntosisLink(long ModuleId, long CharacterId, long CorporationId, long AllianceId, EntosisSide Side, long LinkedMs, long LastCycleMs);

/// <summary>
/// A hub's capture at a checkpoint: the attacker's progress (0 to 1), the time it was checked, the activity multiplier that scales the capture time (fixed when
/// the capture starts), the owner's alliance (null for a neutral hub), the phase and the owner's prime hour (the centre of the owner's daily window).
/// </summary>
public sealed record EntosisCaptureState(
    long HubId, int SolarSystemId, double Progress, long CheckpointMs, double ActivityDefenseMultiplier, long? DefenderAllianceId, EntosisPhase Phase, int PrimeHour = 0);

/// <summary>The rules of the capture (ST-9 c): the times and the rates, and how the progress moves between two checkpoints.</summary>
public static class EntosisRules
{
    /// <summary>The link's cycle (SDE 34593, duration 73): 300 000 ms.</summary>
    public const long CycleMs = 300_000;

    /// <summary>The Sovereignty Hub's base capture time: 595 s (EVE.js <c>OWNED_HUB_CAPTURE_TIME_SECONDS</c>; the wiki says "about 10 minutes").</summary>
    public const double BaseCaptureMs = 595_000;

    /// <summary>The time a defended or attacked hub takes to regenerate to uncaptured: 50 minutes (the wiki's Regeneration table, Sov Hub).</summary>
    public const double RegenMs = 3_000_000;

    /// <summary>Whether a link counts at a time: its last settled cycle is within two cycles before it (a link that stopped without a word stops counting).</summary>
    public static bool IsActive(EntosisLink link, long at) => link.LastCycleMs > at - 2 * CycleMs;

    /// <summary>How long an attacker takes to capture a hub with this activity multiplier: the base time times the multiplier (the wiki's ADM rule).</summary>
    public static double CaptureMs(double activityDefenseMultiplier) => BaseCaptureMs * activityDefenseMultiplier;

    /// <summary>A completed capture: a neutral hub captured, or an owned hub reinforced. Nothing more moves it.</summary>
    public static bool IsDone(EntosisPhase phase) => phase is EntosisPhase.Captured or EntosisPhase.Reinforced;

    /// <summary>
    /// Whether a link may work on the hub now. The wiki: "attackers must first use an entosis link on the structure during its vulnerability window". A neutral
    /// hub has no owner and so no window. A partly captured hub stays vulnerable after its window closes, until it is reinforced or defended (the wiki).
    /// </summary>
    public static bool IsVulnerable(EntosisHubReading reading, EntosisCaptureState? stored, long nowMs) =>
        reading.DefenderAllianceId is null
        || stored is { Phase: EntosisPhase.Capturing }
        || EntosisWindow.IsOpen(reading.PrimeHour, reading.ActivityDefenseMultiplier, nowMs);

    /// <summary>
    /// Moves a hub's progress from its checkpoint to <paramref name="nowMs"/>, with the links that were active at the checkpoint. An attacker alone scores (one
    /// link is enough, and more links do not speed it up). An attacker with a defender paused: nothing moves. Without an attacker the hub regenerates toward
    /// uncaptured, which is how a defender alone reverses a capture (the wiki: a defender link counts the timer back up).
    /// </summary>
    public static EntosisCaptureState Advance(EntosisCaptureState state, IReadOnlyList<EntosisLink> links, long nowMs)
    {
        if (nowMs <= state.CheckpointMs || IsDone(state.Phase))
        {
            return nowMs > state.CheckpointMs ? state with { CheckpointMs = nowMs } : state;
        }
        var elapsed = nowMs - state.CheckpointMs;
        var attacking = links.Any(link => link.Side == EntosisSide.Attacker && IsActive(link, state.CheckpointMs));
        var defending = links.Any(link => link.Side == EntosisSide.Defender && IsActive(link, state.CheckpointMs));
        var progress = state.Progress;
        if (attacking && !defending)
        {
            progress += elapsed / CaptureMs(state.ActivityDefenseMultiplier);
        }
        else if (!attacking)
        {
            progress -= elapsed / RegenMs;
        }
        // the rounding of a long sum of small steps must not leave a capture a nanoscale short of its end
        progress = progress >= 1 - 1e-9 ? 1 : Math.Clamp(progress, 0, 1);
        var phase = progress >= 1 ? EntosisPhase.Captured : progress > 0 ? EntosisPhase.Capturing : EntosisPhase.Idle;
        return state with { Progress = progress, CheckpointMs = nowMs, Phase = phase };
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
/// The owner's daily vulnerability window (ST-9 c, e). The wiki: the window is 18 hours divided by the activity defense multiplier (1 to 6), so 18 hours for ADM 1
/// and 3 hours for ADM 6. It is centred on the owner's prime hour, which the wiki does not place: EVE.js <c>buildDailyVulnerabilityWindow</c> does, with a minimum
/// of one hour. The wiki locks a day's length at the midpoint of the day before; eve.rs uses the multiplier it has now (DEVIATIONS "Sovereignty vulnerability window").
/// </summary>
public static class EntosisWindow
{
    public const long HourMs = 3_600_000;
    public const long DayMs = 86_400_000;

    /// <summary>The reinforcement after an owned hub's capture: about 48 hours (the wiki, "Reinforced period").</summary>
    public const long ReinforcementMs = 48 * HourMs;

    /// <summary>The window of the day that starts at <paramref name="dayStartMs"/> (UTC midnight), as a start and an end (both in milliseconds).</summary>
    public static (long StartMs, long EndMs) Of(int primeHour, double activityDefenseMultiplier, long dayStartMs)
    {
        var durationMs = Math.Max(HourMs, (long)Math.Round(18.0 * HourMs / Math.Clamp(activityDefenseMultiplier, 1, 6)));
        var startMs = dayStartMs + primeHour * HourMs - durationMs / 2;
        return (startMs, startMs + durationMs);
    }

    /// <summary>Whether a time is inside one of the owner's windows (the day before, the day and the day after: a window can cross midnight).</summary>
    public static bool IsOpen(int primeHour, double activityDefenseMultiplier, long nowMs)
    {
        var today = nowMs / DayMs * DayMs;
        for (var day = -1; day <= 1; day++)
        {
            var (startMs, endMs) = Of(primeHour, activityDefenseMultiplier, today + day * DayMs);
            if (nowMs >= startMs && nowMs <= endMs)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// When an owned hub that completed its capture at <paramref name="completedMs"/> comes out of reinforcement: not before 48 hours later, at a point of the
    /// first window that ends after that (the wiki: "a randomly selected time during the next vulnerability window"; EVE.js <c>scheduleReinforcementExit</c>).
    /// The point is drawn from <see cref="Draw"/>, so a replayed completion gives the same time.
    /// </summary>
    public static long ReinforcementExit(int primeHour, double activityDefenseMultiplier, long completedMs, long hubId)
    {
        var earliestMs = completedMs + ReinforcementMs;
        var today = earliestMs / DayMs * DayMs;
        for (var day = -1; day <= 2; day++)
        {
            var (startMs, endMs) = Of(primeHour, activityDefenseMultiplier, today + day * DayMs);
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
