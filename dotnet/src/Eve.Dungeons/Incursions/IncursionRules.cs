namespace Eve.Dungeons.Incursions;

/// <summary>The security band an incursion is focused in. Its capacity and its respawn are per band.</summary>
public enum IncursionBand
{
    HighSec,
    LowSec,
    NullSec,
}

/// <summary>Where an incursion is in its life (the phases of the official mechanics; <c>Ended</c> is the incursion gone from the universe).</summary>
public enum IncursionPhase
{
    Mobilized,
    Established,
    Withdrawing,
    Ended,
}

/// <summary>
/// The rules of the Sansha incursions that the lifecycle and the staging sites follow. Every value names its source. Where CCP publishes no number the value is an eve.rs choice and
/// is named as one (see docs/reference/compat/DEVIATIONS.md, Incursions).
/// </summary>
public static class IncursionRules
{
    /// <summary>The report states of the client (<c>appConst.incursionStateWithdrawing</c>, <c>Mobilizing</c>, <c>Established</c>).</summary>
    public const int StateWithdrawing = 0;
    public const int StateMobilizing = 1;
    public const int StateEstablished = 2;

    /// <summary>The scene type of a staging system (the client's <c>taleConst.scenesTypes.staging</c>).</summary>
    public const int StagingSceneType = 4;

    /// <summary>The reward group of a staging incursion (the client's <c>REWARD_SCOUT</c>, the reward table of the scout sites).</summary>
    public const long StagingRewardGroupId = 8;

    /// <summary>The aggressor faction of Sansha incursions (EVE Online faction 500019, Sansha's Nation; the value EveJS sends).</summary>
    public const long AggressorFactionId = 500019;

    /// <summary>The template class the client expects on a report row (EveJS default for incursion tales).</summary>
    public const long TemplateClassId = 2;

    /// <summary>Influence of a new incursion: full (wiki: the penalties are at their highest at full influence).</summary>
    public const double FullInfluence = 1.0;

    /// <summary>The staging site dungeons of the owner's catalog (archetype 60, "scout"): the client's scout sites of <c>INCURSION_INFO_BY_DUNGEONID</c>.</summary>
    public static readonly IReadOnlyList<long> StagingDungeonIds = [5188, 5190, 5191, 5193];

    /// <summary>The staging sites an incursion has at once (wiki, Incursion sites: "up to five at once"). No official count per incursion is published.</summary>
    public const int StagingSiteCount = 5;

    /// <summary>Mobilized lasts 48 hours (wiki: "always lasts 48 hours unless someone runs the mothership site prematurely").</summary>
    public static readonly TimeSpan MobilizedLength = TimeSpan.FromHours(48);

    /// <summary>
    /// Established lasts up to five days and can be one (wiki). The lifecycle uses one day: a fixed length until the rule that lengthens it (influence) exists.
    /// </summary>
    public static readonly TimeSpan EstablishedLength = TimeSpan.FromDays(1);

    /// <summary>Withdrawing lasts one day (wiki: the incursion disappears with no LP when the mothership is not finished in that time).</summary>
    public static readonly TimeSpan WithdrawingLength = TimeSpan.FromHours(24);

    /// <summary>A new incursion of a band spawns 12 to 36 hours after the one it replaces ended (CCP launch article).</summary>
    public static readonly TimeSpan RespawnMin = TimeSpan.FromHours(12);
    public static readonly TimeSpan RespawnMax = TimeSpan.FromHours(36);

    /// <summary>How long a staging site lives at the most: the whole life of an incursion (the site is ended with its incursion before then).</summary>
    public static TimeSpan MaximumLifetime => MobilizedLength + EstablishedLength + WithdrawingLength;

    /// <summary>The concurrent incursions of a band (wiki: one highsec, one lowsec, three nullsec).</summary>
    public static int Capacity(IncursionBand band) => band == IncursionBand.NullSec ? 3 : 1;

    /// <summary>
    /// The band of a system from its security status: highsec from 0.45 (shown as 0.5), lowsec from 0.05 (shown as 0.1), nullsec below. The rounding is the one the
    /// client shows.
    /// </summary>
    public static IncursionBand BandOf(double security) =>
        security >= 0.45 ? IncursionBand.HighSec : security >= 0.05 ? IncursionBand.LowSec : IncursionBand.NullSec;

    /// <summary>The length of a phase, or null for the phase that ends the incursion.</summary>
    public static TimeSpan? LengthOf(IncursionPhase phase) => phase switch
    {
        IncursionPhase.Mobilized => MobilizedLength,
        IncursionPhase.Established => EstablishedLength,
        IncursionPhase.Withdrawing => WithdrawingLength,
        _ => null,
    };

    /// <summary>The delay before the next incursion of a band, from a uniform number in [0, 1).</summary>
    public static TimeSpan RespawnDelay(double unit)
    {
        var clamped = Math.Clamp(unit, 0d, 1d);
        return RespawnMin + (RespawnMax - RespawnMin) * clamped;
    }

    /// <summary>The report state of a phase (the client's numbers).</summary>
    public static int StateOf(IncursionPhase phase) => phase switch
    {
        IncursionPhase.Mobilized => StateMobilizing,
        IncursionPhase.Established => StateEstablished,
        _ => StateWithdrawing,
    };

    /// <summary>The stable name of a band, as it is stored.</summary>
    public static string NameOf(IncursionBand band) => band switch
    {
        IncursionBand.HighSec => "highsec",
        IncursionBand.LowSec => "lowsec",
        _ => "nullsec",
    };

    /// <summary>The band of a stored name, or null.</summary>
    public static IncursionBand? BandNamed(string name) => name switch
    {
        "highsec" => IncursionBand.HighSec,
        "lowsec" => IncursionBand.LowSec,
        "nullsec" => IncursionBand.NullSec,
        _ => null,
    };

    /// <summary>The stable name of a phase, as it is stored.</summary>
    public static string NameOf(IncursionPhase phase) => phase switch
    {
        IncursionPhase.Mobilized => "mobilized",
        IncursionPhase.Established => "established",
        IncursionPhase.Withdrawing => "withdrawing",
        _ => "ended",
    };

    /// <summary>The phase of a stored name, or null.</summary>
    public static IncursionPhase? PhaseNamed(string name) => name switch
    {
        "mobilized" => IncursionPhase.Mobilized,
        "established" => IncursionPhase.Established,
        "withdrawing" => IncursionPhase.Withdrawing,
        "ended" => IncursionPhase.Ended,
        _ => null,
    };
}
