namespace Eve.Structures;

/// <summary>
/// The system-wide cyno jammer of the structures (E-03): a system is jammed from five minutes after its Standup Cynosural System Jammer I service came online
/// (SDE 35914: "a 5 minute warmup period"). The state the client asks for (<c>GetCynoJammerState</c>) is the FILETIME the jammer starts at, 0 once it runs, or
/// </summary>
public static class StructureJammer
{
    public const long WarmupMs = 300_000;

    private const long FileTimeEpochOffset = 116_444_736_000_000_000;

    /// <summary>The state of the system's jammer at <paramref name="nowMs"/>: null without a jammer, 0 once it runs, else the FILETIME its warm-up ends at.</summary>
    public static long? StateAt(long? onlineSinceMs, long nowMs) => onlineSinceMs switch
    {
        null => null,
        // online before the record existed, so the warm-up is long over
        0 => 0,
        long since when since + WarmupMs <= nowMs => 0,
        long since => FileTimeOf(since + WarmupMs),
    };

    /// <summary>The FILETIME (100 ns ticks since 1601) of a time in milliseconds since 1970.</summary>
    public static long FileTimeOf(long unixMs) => unixMs * 10_000 + FileTimeEpochOffset;
}
