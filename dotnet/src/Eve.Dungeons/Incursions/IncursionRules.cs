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

public enum IncursionSiteRole
{
    Staging,
    Vanguard,
    Assault,
    Headquarters,
    Mothership,
}

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

    public const long AggressorFactionId = 500019;

    public const long TemplateClassId = 2;

    public const double FullInfluence = 1.0;

    public static readonly IReadOnlyList<long> StagingDungeonIds = [5188, 5190, 5191, 5193];

    public const int StagingSiteCount = 5;

    public static readonly TimeSpan MobilizedLength = TimeSpan.FromHours(48);

    public static readonly TimeSpan EstablishedLength = TimeSpan.FromDays(1);

    public static readonly TimeSpan WithdrawingLength = TimeSpan.FromHours(24);

    /// <summary>A new incursion of a band spawns 12 to 36 hours after the one it replaces ended (CCP launch article).</summary>
    public static readonly TimeSpan RespawnMin = TimeSpan.FromHours(12);
    public static readonly TimeSpan RespawnMax = TimeSpan.FromHours(36);

    /// <summary>How long a staging site lives at the most: the whole life of an incursion (the site is ended with its incursion before then).</summary>
    public static TimeSpan MaximumLifetime => MobilizedLength + EstablishedLength + WithdrawingLength;

    public static readonly IReadOnlyList<long> VanguardDungeonIds = [5164, 5165, 5166];
    public static readonly IReadOnlyList<long> AssaultDungeonIds = [5167, 5168, 5172];
    public static readonly IReadOnlyList<long> HeadquartersDungeonIds = [5114, 5171, 5175];

    public static long MothershipDungeonId(IncursionBand band) => band == IncursionBand.LowSec ? 5201 : 5158;

    public static TimeSpan MothershipProtection(IncursionBand band) => band switch
    {
        IncursionBand.HighSec => TimeSpan.FromDays(3),
        IncursionBand.LowSec => TimeSpan.FromDays(1),
        _ => TimeSpan.Zero,
    };

    public const int MinimumLayoutSystems = 3;

    /// <summary>
    /// id); the vanguard and assault systems are the nearest of the rest, in counts that grow with the constellation's size. <paramref name="others"/> carries each
    /// system other than the staging one with its distance from it.
    /// </summary>
    public static (int Headquarters, IReadOnlyList<int> Vanguard, IReadOnlyList<int> Assault) LayoutOf(IReadOnlyList<(int Id, double Distance)> others)
    {
        var byFarthest = others.OrderByDescending(o => o.Distance).ThenBy(o => o.Id).ToList();
        int headquarters = byFarthest[0].Id;
        var nearest = others.Where(o => o.Id != headquarters).OrderBy(o => o.Distance).ThenBy(o => o.Id).Select(o => o.Id).ToList();
        int vanguards = 1, assaults = 1;
        for (var index = 0; index < nearest.Count - 2; index++)
        {
            if (vanguards < 3) vanguards++;
            else if (assaults < 2) assaults++;
            else if (vanguards < 4) vanguards++;
            else if (assaults < 3) assaults++;
            else vanguards++;
        }
        var vanguard = nearest.Take(vanguards).ToList();
        var assault = nearest.Skip(vanguard.Count).Take(assaults).ToList();
        return (headquarters, vanguard, assault);
    }

    public static int Capacity(IncursionBand band) => band == IncursionBand.NullSec ? 3 : 1;

    /// <summary>
    /// The band of a system from its security status: highsec from 0.45 (shown as 0.5), lowsec from 0.05 (shown as 0.1), nullsec below. The rounding is the one the
    /// client shows.
    /// </summary>
    public static IncursionBand BandOf(double security) =>
        security >= 0.45 ? IncursionBand.HighSec : security >= 0.05 ? IncursionBand.LowSec : IncursionBand.NullSec;

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

    /// <summary>The stable name of a site role, as it is stored.</summary>
    public static string NameOf(IncursionSiteRole role) => role switch
    {
        IncursionSiteRole.Staging => "staging",
        IncursionSiteRole.Vanguard => "vanguard",
        IncursionSiteRole.Assault => "assault",
        IncursionSiteRole.Headquarters => "headquarters",
        _ => "mothership",
    };

    /// <summary>The band of a stored name, or null.</summary>
    public static IncursionBand? BandNamed(string name) => name switch
    {
        "highsec" => IncursionBand.HighSec,
        "lowsec" => IncursionBand.LowSec,
        "nullsec" => IncursionBand.NullSec,
        _ => null,
    };

    public static string NameOf(IncursionPhase phase) => phase switch
    {
        IncursionPhase.Mobilized => "mobilized",
        IncursionPhase.Established => "established",
        IncursionPhase.Withdrawing => "withdrawing",
        _ => "ended",
    };

    public static IncursionPhase? PhaseNamed(string name) => name switch
    {
        "mobilized" => IncursionPhase.Mobilized,
        "established" => IncursionPhase.Established,
        "withdrawing" => IncursionPhase.Withdrawing,
        "ended" => IncursionPhase.Ended,
        _ => null,
    };
}
