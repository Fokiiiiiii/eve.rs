namespace Eve.Npc;

/// <summary>What a spawn is asked for besides how many: the pool rolled when nothing is named, the profile when there is no such pool, and what kind of NPC.</summary>
public sealed record NpcSelectionOptions(string DefaultPoolId = "npc_hostiles", string FallbackProfileId = "generic_hostile", string EntityType = "npc", bool PreferPools = true);

/// <summary>A profile or a pool a query named, before anything is rolled (what a roll will need to know of hulls can be loaded from <see cref="ShipTypes"/>).</summary>
public sealed record NpcSelection(string Kind, string Id, string Name, IReadOnlyList<NpcProfile> Candidates, IReadOnlyList<NpcPoolEntry> Entries)
{
    public const string ProfileKind = "profile";
    public const string PoolKind = "pool";

    /// <summary>The ship types of what may be rolled.</summary>
    public IEnumerable<int> ShipTypes => Candidates.Select(profile => profile.ShipTypeId).Where(type => type > 0).Distinct();
}

/// <summary>The NPCs a spawn makes (one profile for each), or why none: the reference's error codes and what was close to what was typed.</summary>
public sealed record NpcPlan(NpcSelection? Selection, IReadOnlyList<NpcProfile> Profiles, string? Error, IReadOnlyList<string> Suggestions)
{
    public bool Succeeded => Error is null;
}

/// <summary>
/// Which NPCs a spawn makes: a port, as it is, of <c>resolveNpcSpawnPlan</c> and what it calls in <c>server/src/space/npc/npcSelection.js</c> of EVE.js (ADR 0009).
/// Naming a profile gives that profile as many times as asked; naming a pool rolls it, by weight, for each; naming nothing rolls the default pool. Only
/// hulls that can be hurt are made (a type with no shield, armor or hull points could never be killed).
/// </summary>
public sealed class NpcSelector(NpcAuthority authority)
{
    public NpcAuthority Authority => authority;

    // the codes of the reference
    public const string ProfileNotFound = "PROFILE_NOT_FOUND";
    public const string ProfileAmbiguous = "PROFILE_AMBIGUOUS";
    public const string PoolEmpty = "POOL_EMPTY";
    public const string ProfileHullNotDamageable = "PROFILE_HULL_NOT_DAMAGEABLE";
    public const string DefinitionIncomplete = "NPC_DEFINITION_INCOMPLETE";

    private static NpcSelection OfProfile(NpcProfile profile) =>
        new(NpcSelection.ProfileKind, profile.Id, profile.Name.Length > 0 ? profile.Name : profile.Id, [profile], []);

    private NpcSelection OfPool(NpcPool pool)
    {
        // an entry whose profile is not authored cannot be rolled
        var known = pool.Entries.Where(entry => authority.Profile(entry.ProfileId) is not null).ToList();
        var candidates = known.Select(entry => authority.Profile(entry.ProfileId)!).DistinctBy(profile => profile.Id).ToList();
        return new NpcSelection(NpcSelection.PoolKind, pool.Id, pool.Name.Length > 0 ? pool.Name : pool.Id, candidates, known);
    }

    /// <summary><c>resolveNpcSpawnPlan</c>'s choice of what a query names: the profile, the pool, or neither (with what was close).</summary>
    public (NpcSelection? Selection, string? Error, IReadOnlyList<string> Suggestions) Select(string? query, NpcSelectionOptions? options = null)
    {
        options ??= new NpcSelectionOptions();
        var trimmed = (query ?? "").Trim();
        if (trimmed.Length == 0)
        {
            if (options.DefaultPoolId.Length > 0 && authority.Pool(options.DefaultPoolId) is { } defaultPool)
            {
                return (OfPool(defaultPool), null, []);
            }
            if (options.FallbackProfileId.Length > 0)
            {
                return authority.Profile(options.FallbackProfileId) is { } fallback ? (OfProfile(fallback), null, []) : (null, DefinitionIncomplete, []);
            }
        }

        var profile = authority.ResolveProfile(trimmed);
        var pool = authority.ResolvePool(trimmed);
        if (profile.Found && !pool.Found)
        {
            return (OfProfile(profile.Match!.Row), null, []);
        }
        if (pool.Found && !profile.Found)
        {
            return (OfPool(pool.Match!.Row), null, []);
        }
        if (profile.Found && pool.Found)
        {
            // a name that one of them has whole wins; else the option says
            if (profile.Match!.Exact && !pool.Match!.Exact)
            {
                return (OfProfile(profile.Match.Row), null, []);
            }
            if (pool.Match!.Exact && !profile.Match.Exact)
            {
                return (OfPool(pool.Match.Row), null, []);
            }
            return options.PreferPools ? (OfPool(pool.Match.Row), null, []) : (OfProfile(profile.Match.Row), null, []);
        }
        var error = profile.Error == ProfileAmbiguous || pool.Error == ProfileAmbiguous ? ProfileAmbiguous : ProfileNotFound;
        return (null, error, DedupeSuggestions([.. profile.Suggestions, .. pool.Suggestions]));
    }

    /// <summary><c>dedupeSuggestions</c>: each once, in order, at most eight.</summary>
    internal static IReadOnlyList<string> DedupeSuggestions(IEnumerable<string> values) =>
        [.. values.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).Take(8)];

    /// <summary>
    /// The NPCs for a selection: the profile <paramref name="amount"/> times, or a pool's entries rolled that many times by weight. <paramref name="damageable"/>
    /// says whether a ship type has hit points to lose; <paramref name="random"/> gives numbers from 0 up to 1.
    /// </summary>
    public NpcPlan Roll(NpcSelection selection, int amount, Func<int, bool> damageable, Func<double> random, string entityType = "npc")
    {
        amount = Math.Max(1, amount);
        bool Allowed(NpcProfile profile) => entityType.Length == 0 || string.Equals(profile.EntityType.Trim(), entityType.Trim(), StringComparison.OrdinalIgnoreCase);
        if (selection.Kind == NpcSelection.ProfileKind)
        {
            var profile = selection.Candidates[0];
            if (!Allowed(profile))
            {
                return new NpcPlan(selection, [], ProfileNotFound, []);
            }
            if (!damageable(profile.ShipTypeId))
            {
                return new NpcPlan(selection, [], ProfileHullNotDamageable, []);
            }
            return new NpcPlan(selection, [.. Enumerable.Repeat(profile, amount)], null, []);
        }

        var eligible = selection.Entries
            .Where(entry => authority.Profile(entry.ProfileId) is { } profile && Allowed(profile) && damageable(profile.ShipTypeId))
            .ToList();
        var chosen = new List<NpcProfile>();
        for (var index = 0; index < amount; index++)
        {
            var entry = ChooseWeighted(eligible, random);
            if (entry is null)
            {
                return new NpcPlan(selection, [], PoolEmpty, []);
            }
            chosen.Add(authority.Profile(entry.ProfileId)!);
        }
        return new NpcPlan(selection, chosen, null, []);
    }

    /// <summary><c>chooseWeightedEntry</c>: each entry has a weight of at least one; the roll takes the first whose share it falls in, the last when rounding leaves none.</summary>
    private static NpcPoolEntry? ChooseWeighted(List<NpcPoolEntry> entries, Func<double> random)
    {
        var named = entries.Where(entry => entry.ProfileId.Trim().Length > 0).ToList();
        if (named.Count == 0)
        {
            return null;
        }
        var total = named.Sum(entry => Math.Max(1, entry.Weight));
        var roll = random() * total;
        foreach (var entry in named)
        {
            roll -= Math.Max(1, entry.Weight);
            if (roll < 0)
            {
                return entry;
            }
        }
        return named[^1];
    }
}
