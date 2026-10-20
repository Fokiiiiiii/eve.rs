using System.Collections.Generic;

namespace Eve.Services.Space.Entosis;

/// <summary>The end of a command node stage: still open, won by the attackers (the bar reached 0), or won by the defenders (the bar reached 100%).</summary>
public enum CommandNodeOutcome : byte
{
    Open = 0,
    AttackersWon = 1,
    DefendersWon = 2,
}

/// <summary>
/// (captured by the attackers), and the stage's bar starts at 60% and moves 7% for each node captured. Nine attacker captures take the bar to 0 (the attackers
/// win); six defender captures take it to 100% (the defenders win).
/// </summary>
public static class CommandNodeRules
{

    public const double CaptureMs = 240_000;

    public const double RegenMs = 98 * 60_000;

    public const double InitialScore = 0.6;
    public const double ScoreStep = 0.07;
    public const int InitialNodes = 4;
    public const int MaxNodesPerSystem = 5;
    public const int MaxNodesPerConstellation = 20;

    /// <summary>The spawn of a further node: about 30 minutes, with a jitter of half that either way (EVE.js EXTRA_COMMAND_NODE_SPAWN_JITTER).</summary>
    public const long ExtraSpawnIntervalMs = 30 * 60_000;

    /// <summary>The stage's outcome from the captures so far (counted, so no rounding decides it).</summary>
    public static CommandNodeOutcome Outcome(int attackerCaptures, int defenderCaptures)
    {
        if (InitialScore - ScoreStep * attackerCaptures <= 1e-9)
        {
            return CommandNodeOutcome.AttackersWon;
        }
        return InitialScore + ScoreStep * defenderCaptures >= 1 - 1e-9 ? CommandNodeOutcome.DefendersWon : CommandNodeOutcome.Open;
    }

    /// <summary>
    /// A node's progress from its checkpoint to <paramref name="nowMs"/>, with the links that were settled at the checkpoint. An attacker alone moves it toward
    /// +1 at the ADM-scaled time, a defender alone toward -1 at the base time; both together pause it. Without a link it regenerates toward 0.
    /// </summary>
    public static double Advance(double progress, long checkpointMs, long nowMs, IReadOnlyList<EntosisLink> links, double activityDefenseMultiplier)
    {
        var elapsed = nowMs - checkpointMs;
        if (elapsed <= 0)
        {
            return progress;
        }
        var attackUntil = EntosisRules.ActiveUntil(links, EntosisSide.Attacker, checkpointMs);
        var defenseUntil = EntosisRules.ActiveUntil(links, EntosisSide.Defender, checkpointMs);
        if (attackUntil is not null && defenseUntil is not null)
        {
            return progress;
        }
        if (attackUntil is { } attack)
        {
            var scored = Math.Min(elapsed, attack - checkpointMs);
            var moved = progress + scored / (CaptureMs * activityDefenseMultiplier);
            return moved >= 1 ? 1 : Regenerate(moved, elapsed - scored);
        }
        if (defenseUntil is { } defence)
        {
            var scored = Math.Min(elapsed, defence - checkpointMs);
            var moved = progress - scored / CaptureMs;
            return moved <= -1 ? -1 : Regenerate(moved, elapsed - scored);
        }
        return Regenerate(progress, elapsed);
    }

    /// <summary>
    /// When the node is captured if the links stay as they are at <paramref name="nowMs"/>; null when no side is alone with a link, or the node is captured.
    /// </summary>
    public static long? CompletionAt(double progress, long nowMs, IReadOnlyList<EntosisLink> links, double activityDefenseMultiplier)
    {
        var attacking = links.Any(link => link.Side == EntosisSide.Attacker && EntosisRules.IsActive(link, nowMs));
        var defending = links.Any(link => link.Side == EntosisSide.Defender && EntosisRules.IsActive(link, nowMs));
        if (attacking == defending || Math.Abs(progress) >= 1)
        {
            return null;
        }
        return attacking
            ? nowMs + (long)Math.Ceiling((1 - progress) * CaptureMs * activityDefenseMultiplier)
            : nowMs + (long)Math.Ceiling((progress + 1) * CaptureMs);
    }

    private static double Regenerate(double progress, long elapsedMs)
    {
        var step = elapsedMs / RegenMs;
        return progress > 0 ? Math.Max(0, progress - step) : Math.Min(0, progress + step);
    }
}
