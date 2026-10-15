namespace Eve.Services.Space.Entosis;

/// <summary>The side of a link: the attacker's (an alliance other than the owner's), or the defender's (the owner's, which repairs the hub).</summary>
public enum EntosisSide : byte
{
    Attacker = 0,
    Defender = 1,
}

/// <summary>Where a hub's capture is: nothing yet, a capture in progress, or captured (its handover is ST-9 d).</summary>
public enum EntosisPhase : byte
{
    Idle = 0,
    Capturing = 1,
    Captured = 2,
}

/// <summary>
/// A link of a hub: a module that has settled a cycle for it. The first cycle ends the warm-up (the link is recorded then), so only the cycles after it count.
/// A link is active at a time when its last settled cycle is within two cycles of that time.
/// </summary>
public sealed record EntosisLink(long ModuleId, long CharacterId, long CorporationId, long AllianceId, EntosisSide Side, long LinkedMs, long LastCycleMs);

/// <summary>
/// A hub's capture at a checkpoint: the attacker's progress (0 to 1), the time it was checked, the activity multiplier that scales the capture time (fixed when
/// the capture starts), the owner's alliance (null for a neutral hub) and the phase.
/// </summary>
public sealed record EntosisCaptureState(
    long HubId, int SolarSystemId, double Progress, long CheckpointMs, double ActivityDefenseMultiplier, long? DefenderAllianceId, EntosisPhase Phase);

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

    /// <summary>
    /// Moves a hub's progress from its checkpoint to <paramref name="nowMs"/>, with the links that were active at the checkpoint. An attacker alone scores (one
    /// link is enough, and more links do not speed it up). An attacker with a defender paused: nothing moves. Without an attacker the hub regenerates toward
    /// uncaptured, which is how a defender alone reverses a capture (the wiki: a defender link counts the timer back up).
    /// </summary>
    public static EntosisCaptureState Advance(EntosisCaptureState state, IReadOnlyList<EntosisLink> links, long nowMs)
    {
        if (nowMs <= state.CheckpointMs || state.Phase == EntosisPhase.Captured)
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
        if (state.Phase == EntosisPhase.Captured)
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
