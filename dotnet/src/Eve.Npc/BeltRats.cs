using System.Text.RegularExpressions;

namespace Eve.Npc;

/// <summary>What a solar system is to the rats of its asteroid belts: how safe it is, which region and which empire faction it belongs to.</summary>
public sealed record BeltSystem(int SolarSystemId, double SecurityStatus, int RegionId, int FactionId);

/// <summary>
/// How asteroid belt rats are rolled (<c>config/npc.json</c> of the reference: <c>asteroidBeltNpcRat*</c>): a pilot arriving at a belt makes one roll; a belt has no more than
/// <see cref="MaxActiveGroupsPerBelt"/> groups at once, is rolled again after <see cref="RollCooldownMs"/> and a new group comes after <see cref="RespawnCooldownMs"/> at the earliest.
/// </summary>
public sealed record BeltRatConfig(
    bool Enabled, double ChanceHighSec, double ChanceLowSec, double ChanceNullSec, long RollCooldownMs, long RespawnCooldownMs, int MaxActiveGroupsPerBelt, double LandingRadiusMeters,
    double SpawnDistanceMeters)
{
    /// <summary>The reference's own values.</summary>
    public static readonly BeltRatConfig Reference = new(true, 0.25, 0.35, 0.45, 120_000, 1_200_000, 1, 250_000, 30_000);

    /// <summary>
    /// The reference's values with what the environment changes: <c>EVE_BELT_RATS</c> (<c>off</c>, <c>0</c> or <c>false</c> switches the rats off), <c>EVE_BELT_RAT_CHANCE</c> (the chance of a
    /// group at an arrival, for every security band), <c>EVE_BELT_RAT_ROLL_COOLDOWN_MS</c> and <c>EVE_BELT_RAT_RESPAWN_COOLDOWN_MS</c>. A value that is not a number (or a chance outside 0 to 1)
    /// is not taken. The landing radius is the simulation's (it decides where a warp has arrived at a belt) and is not changed here.
    /// </summary>
    public static BeltRatConfig FromEnvironment(Func<string, string?> get)
    {
        var config = Reference;
        if (get("EVE_BELT_RATS") is { Length: > 0 } enabled)
        {
            config = config with { Enabled = enabled.Trim().ToLowerInvariant() is not ("off" or "0" or "false") };
        }
        if (Number(get("EVE_BELT_RAT_CHANCE")) is { } chance and >= 0 and <= 1)
        {
            config = config with { ChanceHighSec = chance, ChanceLowSec = chance, ChanceNullSec = chance };
        }
        if (Number(get("EVE_BELT_RAT_ROLL_COOLDOWN_MS")) is { } roll and >= 0)
        {
            config = config with { RollCooldownMs = (long)roll };
        }
        if (Number(get("EVE_BELT_RAT_RESPAWN_COOLDOWN_MS")) is { } respawn and >= 0)
        {
            config = config with { RespawnCooldownMs = (long)respawn };
        }
        return config;
    }

    private static double? Number(string? text) =>
        double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;
}

/// <summary>A group of rats chosen for a belt: what holds the system, how safe it is, and the profile of each rat.</summary>
public sealed record BeltRatPlan(string Faction, string Band, double SecurityStatus, IReadOnlyList<NpcProfile> Profiles);

/// <summary>What came of an arrival at a belt.</summary>
public enum BeltOutcome
{

    Spawn,
    Disabled,
    /// <summary>The belt has a group already (as many as the configuration allows).</summary>
    ActiveGroupPresent,
    /// <summary>A group was spawned here not long ago.</summary>
    RespawnCooldown,
    /// <summary>The belt was rolled not long ago.</summary>
    RollCooldown,
    /// <summary>Wormhole space has none.</summary>
    NotEligible,
    /// <summary>No rat could be made for the faction (its pool has no profile of the hulls this security takes).</summary>
    PlanFailed,
    ChanceMiss,
}

public sealed record BeltDecision(BeltOutcome Outcome, BeltRatPlan? Plan = null, string? Faction = null, string? Band = null);

/// <summary>
/// The rats of asteroid belts (<c>beltRatRuntime.js</c> and <c>pirateFactionPolicy.js</c> of the reference, ADR 0009): a pilot who arrives at a belt may find a group of pirates of the faction that holds
/// the region, as many and as big as the system's security allows. This is the ordinary group; the reference's rarer ones (haulers, commanders, officers, capitals) are not ported.
/// </summary>
public sealed partial class BeltRats
{
    private sealed record Band(double MinSecurity, string Name, int MinCount, int MaxCount, (string Hull, int Weight)[] Hulls);

    // `SECURITY_PROFILES`: the first whose limit the security reaches
    private static readonly Band[] Bands =
    [
        new(0.8, "highsec", 1, 1, [("frigate", 12)]),
        new(0.45, "highsec", 1, 2, [("frigate", 10), ("destroyer", 3)]),
        new(0.1, "lowsec", 2, 3, [("frigate", 8), ("destroyer", 5), ("cruiser", 3)]),
        new(0, "lowsec", 2, 4, [("destroyer", 5), ("cruiser", 6), ("battlecruiser", 2)]),
        new(-0.45, "nullsec", 3, 4, [("cruiser", 5), ("battlecruiser", 5), ("battleship", 2)]),
        new(double.NegativeInfinity, "nullsec", 3, 5, [("cruiser", 2), ("battlecruiser", 5), ("battleship", 5)]),
    ];

    // `PIRATE_FACTION_BY_FACTION_ID`: a system that belongs to a pirate faction; `EMPIRE_FALLBACK_PIRATE_BY_FACTION_ID`: an empire's system in a region no pirate holds
    private static readonly Dictionary<int, string> PirateOfFaction = new() { [500010] = "guristas", [500011] = "angels", [500012] = "blood", [500019] = "sanshas", [500020] = "serpentis", [500025] = "rogue_drones" };
    private static readonly Dictionary<int, string> PirateNextToEmpire = new() { [500001] = "guristas", [500002] = "angels", [500004] = "serpentis", [500007] = "sanshas", [500008] = "sanshas" };

    /// <summary>The factions a system that no faction owns draws from, <c>STANDARD_PIRATE_FACTIONS</c>.</summary>
    private static readonly string[] AnyPirate = ["guristas", "serpentis", "angels", "blood", "sanshas", "rogue_drones"];

    private readonly NpcAuthority _authority;
    private readonly Func<int, bool> _hasHull;
    private readonly Dictionary<(int System, long Belt), BeltState> _belts = [];

    private sealed class BeltState
    {
        public long LastRollMs = long.MinValue / 2;
        public long LastSpawnMs = long.MinValue / 2;
    }

    /// <param name="hasHull">Whether a ship type has hit points a weapon can work on (a profile whose hull has none is never chosen: nothing could kill it).</param>
    public BeltRats(NpcAuthority authority, BeltRatConfig? config = null, Func<int, bool>? hasHull = null)
    {
        _authority = authority;
        Config = config ?? BeltRatConfig.Reference;
        _hasHull = hasHull ?? (_ => true);
    }

    public BeltRatConfig Config { get; }

    /// <summary>The ship types of the profiles the rats are chosen from: their hulls are to be known (<c>hasHull</c>) before a group is rolled.</summary>
    public IReadOnlyList<int> PoolShipTypes() =>
    [
        .. AnyPirate.Select(_authority.Pool).OfType<NpcPool>().SelectMany(pool => pool.Entries)
            .Select(entry => _authority.Profile(entry.ProfileId)).OfType<NpcProfile>().Select(profile => profile.ShipTypeId).Distinct(),
    ];

    /// <summary>How a pilot's arrival at a belt comes out: the checks the reference makes, in its order, and then one roll of chance. A group that is to be spawned is told by <see cref="Spawned"/> when it was.</summary>
    /// <param name="activeGroups">How many groups of rats the belt has now.</param>
    public BeltDecision Decide(BeltSystem system, long beltId, long nowMs, int activeGroups, Func<double> random)
    {
        if (!Config.Enabled)
        {
            return new BeltDecision(BeltOutcome.Disabled);
        }
        var state = StateOf(system.SolarSystemId, beltId);
        if (activeGroups >= Config.MaxActiveGroupsPerBelt)
        {
            return new BeltDecision(BeltOutcome.ActiveGroupPresent);
        }
        if (Config.RespawnCooldownMs > 0 && nowMs - state.LastSpawnMs < Config.RespawnCooldownMs)
        {
            return new BeltDecision(BeltOutcome.RespawnCooldown);
        }
        if (Config.RollCooldownMs > 0 && nowMs - state.LastRollMs < Config.RollCooldownMs)
        {
            return new BeltDecision(BeltOutcome.RollCooldown);
        }
        if (IsWormholeSpace(system.SolarSystemId))
        {
            return new BeltDecision(BeltOutcome.NotEligible);
        }
        var band = BandOf(system.SecurityStatus);
        var faction = FactionOf(system, random);
        var plan = PlanOf(system, band, faction, random);
        if (plan is null)
        {
            return new BeltDecision(BeltOutcome.PlanFailed, Faction: faction, Band: band.Name);
        }
        state.LastRollMs = nowMs;
        var chance = band.Name switch { "highsec" => Config.ChanceHighSec, "lowsec" => Config.ChanceLowSec, "nullsec" => Config.ChanceNullSec, _ => 0 };
        return random() >= Math.Clamp(chance, 0, 1)
            ? new BeltDecision(BeltOutcome.ChanceMiss, Faction: faction, Band: band.Name)
            : new BeltDecision(BeltOutcome.Spawn, plan, faction, band.Name);
    }

    /// <summary>A group was spawned at the belt: no other comes before the respawn cooldown has passed.</summary>
    public void Spawned(int solarSystemId, long beltId, long nowMs) => StateOf(solarSystemId, beltId).LastSpawnMs = nowMs;

    private BeltState StateOf(int system, long belt)
    {
        lock (_belts)
        {
            if (!_belts.TryGetValue((system, belt), out var state))
            {
                _belts[(system, belt)] = state = new BeltState();
            }
            return state;
        }
    }

    private static bool IsWormholeSpace(int solarSystemId) => solarSystemId is >= 31_000_000 and <= 31_999_999;

    private static Band BandOf(double security) => Bands.First(band => security >= band.MinSecurity);

    /// <summary><c>resolvePirateFactionKeyForSystem</c>: the faction that owns the system, else the one that holds its region, else the one beside the empire it belongs to, else any of them.</summary>
    internal string FactionOf(BeltSystem system, Func<double> random)
    {
        if (PirateOfFaction.TryGetValue(system.FactionId, out var owner))
        {
            return owner;
        }
        if (_authority.PirateOfRegion(system.RegionId) is { } holder)
        {
            return holder;
        }
        if (PirateNextToEmpire.TryGetValue(system.FactionId, out var neighbour))
        {
            return neighbour;
        }
        return AnyPirate[Math.Min(AnyPirate.Length - 1, (int)Math.Floor(random() * AnyPirate.Length))];
    }

    private BeltRatPlan? PlanOf(BeltSystem system, Band band, string faction, Func<double> random)
    {
        var count = band.MinCount == band.MaxCount ? band.MinCount : band.MinCount + (int)Math.Floor(random() * (band.MaxCount - band.MinCount + 1));
        var profiles = new List<NpcProfile>();
        for (var i = 0; i < count; i++)
        {
            var hull = ChooseWeighted(band.Hulls, h => h.Weight, random).Hull;
            if (ProfileOf(faction, hull, random) is { } profile)
            {
                profiles.Add(profile);
            }
        }
        return profiles.Count == 0 ? null : new BeltRatPlan(faction, band.Name, system.SecurityStatus, profiles);
    }

    /// <summary><c>chooseStandardPoolProfileIDForFactionHull</c>: a profile of the faction's standard pool whose hull class is the one asked for, by its weight in the pool.</summary>
    private NpcProfile? ProfileOf(string faction, string hull, Func<double> random)
    {
        if (_authority.Pool(faction) is not { } pool)
        {
            return null;
        }
        var candidates = new List<(NpcProfile Profile, int Weight)>();
        foreach (var entry in pool.Entries)
        {
            if (_authority.Profile(entry.ProfileId) is { } profile && entry.Weight > 0 && MatchesHull(profile, hull) && _hasHull(profile.ShipTypeId))
            {
                candidates.Add((profile, entry.Weight));
            }
        }
        return candidates.Count == 0 ? null : ChooseWeighted(candidates, c => c.Weight, random).Profile;
    }

    [GeneratedRegex("[_-]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary><c>profileMatchesHullClass</c>: the name of the hull class stands as a word in what names the profile (its id, name, ship name and aliases; "battle cruiser" is a battlecruiser too).</summary>
    internal static bool MatchesHull(NpcProfile profile, string hullClass)
    {
        var hull = Regex.Replace(hullClass.Trim().ToLowerInvariant(), "[\\s_-]+", "");
        if (hull.Length == 0)
        {
            return false;
        }
        var haystack = Spaces().Replace(Separators().Replace(string.Join(' ', [profile.Id, profile.Name, profile.ShipNameTemplate, .. profile.Aliases]).ToLowerInvariant(), " "), " ");
        var pattern = hull == "battlecruiser" ? "(^|[^a-z])battle\\s*cruiser([^a-z]|$)" : $"(^|[^a-z]){Regex.Escape(hull)}([^a-z]|$)";
        return Regex.IsMatch(haystack, pattern, RegexOptions.ECMAScript);
    }

    private static T ChooseWeighted<T>(IReadOnlyList<T> entries, Func<T, int> weight, Func<double> random)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            total += Math.Max(0, weight(entry));
        }
        var roll = random() * total;
        foreach (var entry in entries)
        {
            roll -= Math.Max(0, weight(entry));
            if (roll < 0)
            {
                return entry;
            }
        }
        return entries[^1];
    }
}
