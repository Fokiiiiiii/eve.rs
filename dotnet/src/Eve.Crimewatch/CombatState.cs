namespace Eve.Crimewatch;

/// <summary>What a pilot is flagged as: a suspect (anyone may shoot it without a penalty) or a criminal (CONCORD answers it as well). One flag at a time, the stronger wins.</summary>
public enum Flag : byte
{
    None = 0,
    Suspect = 1,
    Criminal = 2,
}

/// <summary>One of the five combat timers as the client is told it: its state (the idle code, or the idle code plus two while it runs) and when it ends (a Unix time in ms) while it runs.</summary>
public readonly record struct TimerView(int State, long? ExpiresAtMs);

/// <summary>The five timers in the order the client's <c>GetClientStates</c> has them.</summary>
public sealed record CombatTimers(TimerView Weapons, TimerView Pvp, TimerView Npc, TimerView Criminal, TimerView Disapproval)
{
    /// <summary>The same timers when none runs.</summary>
    public static CombatTimers Idle { get; } = CombatState.Idle.TimersAt(0);
}

/// <summary>
/// What crimewatch keeps for a pilot besides its safety level: the weapons, PvP, NPC and disapproval timers, the one timer of the suspect or criminal flag, and the system the pilot was
/// last in when it did something that counts (where the others are told it is flagged). Times are Unix milliseconds; a timer that has passed is idle. A port of the state of
/// <c>crimewatchState.js</c> of EVE.js (ADR 0009), one flag in place of its penalty record. A new offence sets the kind of the flag and keeps the longer of the timer that runs and a fresh one, as the reference does (a criminal who then shoots a ship in low security is a suspect; EVE University does not say which wins, so nothing is invented here).
/// </summary>
public sealed record CombatState(long WeaponsUntilMs, long PvpUntilMs, long NpcUntilMs, long FlagUntilMs, Flag Flag, long DisapprovalUntilMs, long SolarSystemId)
{
    public const long WeaponsDurationMs = 60_000;
    public const long PvpDurationMs = 15 * 60_000;
    public const long NpcDurationMs = 5 * 60_000;
    public const long FlagDurationMs = 15 * 60_000;
    public const long DisapprovalDurationMs = 5 * 60_000;

    // the client's states of the timers (weaponsTimerState…): idle, and idle + 2 while it runs; the criminal timer is 303 for a criminal and 304 for a suspect
    public const int WeaponsIdle = 100;
    public const int PvpIdle = 200;
    public const int CriminalIdle = 300;
    public const int NpcIdle = 400;
    public const int DisapprovalIdle = 500;
    private const int CriminalRunning = 303;
    private const int SuspectRunning = 304;

    /// <summary>A pilot with no timer and no flag.</summary>
    public static CombatState Idle { get; } = new(0, 0, 0, 0, Flag.None, 0, 0);

    /// <summary>The flag the pilot has at this time (none once its timer is over).</summary>
    public Flag FlagAt(long nowMs) => FlagUntilMs > nowMs ? Flag : Flag.None;

    public bool IsCriminal(long nowMs) => FlagAt(nowMs) == Flag.Criminal;

    public bool IsSuspect(long nowMs) => FlagAt(nowMs) == Flag.Suspect;

    /// <summary>Whether the weapons timer runs: it keeps a pilot from docking.</summary>
    public bool HasWeaponsTimer(long nowMs) => WeaponsUntilMs > nowMs;

    private static TimerView Running(int idle, long until, long nowMs) => until > nowMs ? new TimerView(idle + 2, until) : new TimerView(idle, null);

    /// <summary>The five timers as the client is told them at this time.</summary>
    public CombatTimers TimersAt(long nowMs) => new(
        Running(WeaponsIdle, WeaponsUntilMs, nowMs),
        Running(PvpIdle, PvpUntilMs, nowMs),
        Running(NpcIdle, NpcUntilMs, nowMs),
        FlagAt(nowMs) switch
        {
            Flag.Criminal => new TimerView(CriminalRunning, FlagUntilMs),
            Flag.Suspect => new TimerView(SuspectRunning, FlagUntilMs),
            _ => new TimerView(CriminalIdle, null),
        },
        Running(DisapprovalIdle, DisapprovalUntilMs, nowMs));

    /// <summary>Whether anything runs at this time (a pilot with nothing running needs no row and no wake-up).</summary>
    public bool IsActive(long nowMs) => NextEndAfter(nowMs) is not null;

    /// <summary>The first moment after this time at which a timer or the flag ends, or null when none runs.</summary>
    public long? NextEndAfter(long nowMs)
    {
        long? first = null;
        foreach (var until in new[] { WeaponsUntilMs, PvpUntilMs, NpcUntilMs, DisapprovalUntilMs, Flag == Flag.None ? 0 : FlagUntilMs })
        {
            if (until > nowMs && (first is null || until < first))
            {
                first = until;
            }
        }
        return first;
    }

    /// <summary>Flags the pilot: the kind is the one of the new offence, the timer the longer of the one that runs and a fresh one (<c>setNonTheftPenalty</c> of the reference).</summary>
    internal CombatState Flagged(Flag flag, long nowMs)
    {
        var kept = FlagAt(nowMs) == Flag.None ? 0 : FlagUntilMs;
        return this with { Flag = flag, FlagUntilMs = Math.Max(kept, nowMs + FlagDurationMs) };
    }
}
