using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Eve.Npc;

/// <summary>What a profile says of an NPC: the ship type it flies, whose it is, what is fitted to it and where it comes into space.</summary>
public sealed record NpcProfile(
    string Id, string Name, IReadOnlyList<string> Aliases, string EntityType, int ShipTypeId, long CorporationId, long AllianceId, int FactionId, string LoadoutId,
    string LootTableId, string ShipNameTemplate, double SecurityStatus, long Bounty, double SpawnDistanceMeters);

/// <summary>What is fitted to an NPC: modules (type, how many) and the charges they hold (type, how many to a module). Retail entity types have none of either.</summary>
public sealed record NpcLoadout(string Id, string Name, IReadOnlyList<(int TypeId, int Quantity)> Modules, IReadOnlyList<(int TypeId, int Quantity)> Charges);

/// <summary>A profile of a pool and how often it is rolled (its weight, at least one).</summary>
public sealed record NpcPoolEntry(string ProfileId, int Weight);

/// <summary>A set of profiles a spawn rolls from.</summary>
public sealed record NpcPool(string Id, string Name, IReadOnlyList<string> Aliases, string EntityType, IReadOnlyList<NpcPoolEntry> Entries, bool Derived = false);

/// <summary>A row a query found: how it matched (the whole of a name, or part of it).</summary>
public sealed record NpcMatch<T>(T Row, bool Exact);

/// <summary>The result of looking a row up by what a player typed: the row, or why not and what was close.</summary>
public sealed record NpcLookup<T>(NpcMatch<T>? Match, string? Error, IReadOnlyList<string> Suggestions)
{
    public bool Found => Match is not null;
}

public sealed partial class NpcAuthority
{
    /// <summary>Commander, officer and faction-grade rats never join the derived standard pools.</summary>
    [GeneratedRegex("commander|officer|domination|dread_guristas|dark_blood|true_sansha|shadow_serpentis|overseer", RegexOptions.IgnoreCase)]
    private static partial Regex StandardPoolExcluded();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    private sealed record StandardPool(string BasePoolId, string PoolId, string Name, string[] Aliases, bool IncludeAllBaseEntries, string[] AllowedProfilePrefixes);

    private static readonly StandardPool[] StandardPools =
    [
        new("blood", "blood_standard", "Blood Raider Standard Hostiles", ["blood standard", "blood anomaly", "blood raider standard"], true, ["parity_blood_raider_pulse_", "parity_blood_raider_beam_"]),
        new("sanshas", "sanshas_standard", "Sansha Standard Hostiles", ["sansha standard", "sanshas anomaly", "sansha anomaly"], true, ["parity_sansha_pulse_", "parity_sansha_beam_"]),
        new("serpentis", "serpentis_standard", "Serpentis Standard Hostiles", ["serpentis standard", "serpentis anomaly"], true, ["parity_serpentis_blaster_", "parity_serpentis_rail_"]),
        new("angels", "angels_standard", "Angel Standard Hostiles", ["angel standard", "angels anomaly", "angel anomaly"], true, ["parity_angel_autocannon_", "parity_angel_artillery_"]),
        new("guristas", "guristas_standard", "Guristas Standard Hostiles", ["guristas standard", "guristas anomaly"], true, ["parity_guristas_missile_", "parity_guristas_rail_"]),
    ];

    /// <summary>The loadout of a retail entity type: nothing fitted, the weapon is on the type.</summary>
    public const string RetailEmptyLoadoutId = "retail_empty_npc_entity_loadout";

    private readonly Index<NpcProfile> _profiles;
    private readonly Index<NpcPool> _pools;
    private readonly Dictionary<string, NpcLoadout> _loadouts;
    private readonly Dictionary<string, LootTable> _lootTables = [];
    private readonly Dictionary<int, string> _pirateRegions = [];

    public NpcAuthority(IEnumerable<NpcProfile> profiles, IEnumerable<NpcLoadout> loadouts, IEnumerable<NpcPool> pools, IEnumerable<LootTable>? lootTables = null)
    {
        foreach (var table in lootTables ?? [])
        {
            _lootTables[table.Id.Trim()] = table;
        }
        _profiles = new Index<NpcProfile>([.. profiles], p => p.Id, p => p.Name, p => p.Aliases);
        _loadouts = [];
        foreach (var loadout in loadouts)
        {
            _loadouts[loadout.Id] = loadout;
        }
        _loadouts.TryAdd(RetailEmptyLoadoutId, new NpcLoadout(RetailEmptyLoadoutId, "Retail NPC Entity Native Dogma", [], []));
        var authored = pools.ToList();
        _pools = new Index<NpcPool>([.. authored, .. DerivedPools(authored)], p => p.Id, p => p.Name, p => p.Aliases);
    }

    public IReadOnlyList<NpcProfile> Profiles => _profiles.Rows;

    public IReadOnlyList<NpcPool> Pools => _pools.Rows;

    public NpcProfile? Profile(string id) => _profiles.ById(id);

    public NpcLoadout? Loadout(string id) => _loadouts.GetValueOrDefault(id.Trim());

    public NpcPool? Pool(string id) => _pools.ById(id);

    /// <summary>What an NPC leaves, by the id its profile names (null for an id the authored data does not have: such an NPC leaves nothing).</summary>
    public LootTable? LootTable(string id) => _lootTables.GetValueOrDefault(id.Trim());

    public int LootTableCount => _lootTables.Count;

    /// <summary>The pirate faction that holds a region (<c>guristas</c>, <c>angels</c>, <c>blood</c>, <c>sanshas</c>, <c>serpentis</c> or <c>rogue_drones</c>), or null for a region none holds.</summary>
    public string? PirateOfRegion(int regionId) => _pirateRegions.GetValueOrDefault(regionId);

    public NpcAuthority WithPirateRegions(IEnumerable<KeyValuePair<int, string>> regions)
    {
        foreach (var (region, faction) in regions)
        {
            _pirateRegions[region] = faction;
        }
        return this;
    }

    public NpcLookup<NpcProfile> ResolveProfile(string query) => _profiles.Resolve(query);

    public NpcLookup<NpcPool> ResolvePool(string query) => _pools.Resolve(query);

    internal static string NormalizeQuery(string? value) => NonAlphanumeric().Replace((value ?? "").Trim().ToLowerInvariant(), " ");

    private static IEnumerable<NpcPool> DerivedPools(List<NpcPool> authored)
    {
        var byId = authored.GroupBy(p => p.Id.Trim()).ToDictionary(g => g.Key, g => g.Last());
        foreach (var spec in StandardPools)
        {
            if (!byId.TryGetValue(spec.BasePoolId, out var basePool))
            {
                continue;
            }
            var entries = spec.IncludeAllBaseEntries
                ? basePool.Entries.Where(entry => !StandardPoolExcluded().IsMatch(entry.ProfileId)).ToList()
                : basePool.Entries.Where(entry => spec.AllowedProfilePrefixes.Any(prefix => entry.ProfileId.Trim().StartsWith(prefix, StringComparison.Ordinal))).ToList();
            if (entries.Count == 0)
            {
                continue;
            }
            yield return basePool with { Id = spec.PoolId, Name = spec.Name, Aliases = spec.Aliases, Entries = entries, Derived = true };
        }
    }

    /// <summary>The rows of a table: by id, by each of the tokens a query can be (the first row of a token is the one it names) and for a part of one.</summary>
    private sealed class Index<T> where T : class
    {
        private readonly Func<T, string> _id;
        private readonly Func<T, string> _name;
        private readonly Dictionary<string, T> _byId = [];
        private readonly Dictionary<string, T> _byToken = [];
        private readonly List<(T Row, string[] Tokens)> _entries = [];

        public Index(List<T> rows, Func<T, string> id, Func<T, string> name, Func<T, IReadOnlyList<string>> aliases)
        {
            Rows = rows;
            _id = id;
            _name = name;
            foreach (var row in rows)
            {
                var key = id(row).Trim();
                if (key.Length > 0)
                {
                    _byId[key] = row;
                }
                var tokens = new[] { id(row), name(row) }.Concat(aliases(row)).Select(NormalizeQuery).Where(token => token.Length > 0).ToArray();
                foreach (var token in tokens)
                {
                    _byToken.TryAdd(token, row);
                }
                _entries.Add((row, tokens));
            }
        }

        public List<T> Rows { get; }

        public T? ById(string id) => _byId.GetValueOrDefault(id.Trim());

        public NpcLookup<T> Resolve(string query)
        {
            var normalized = NormalizeQuery(query);
            if (normalized.Length == 0)
            {
                return new NpcLookup<T>(null, "PROFILE_REQUIRED", [.. Rows.Take(8).Select(row => _name(row).Length > 0 ? _name(row) : _id(row))]);
            }
            if (_byToken.TryGetValue(normalized, out var exact))
            {
                return new NpcLookup<T>(new NpcMatch<T>(exact, true), null, []);
            }
            var partial = _entries.Where(entry => entry.Tokens.Any(token => token.Contains(normalized, StringComparison.Ordinal))).ToList();
            if (partial.Count == 1)
            {
                return new NpcLookup<T>(new NpcMatch<T>(partial[0].Row, false), null, []);
            }
            return new NpcLookup<T>(
                null, partial.Count > 1 ? "PROFILE_AMBIGUOUS" : "PROFILE_NOT_FOUND", [.. partial.Take(8).Select(entry => $"{_name(entry.Row)} ({_id(entry.Row)})")]);
        }
    }

    private static readonly Lazy<NpcAuthority> Embedded = new(() =>
    {
        using var stream = typeof(NpcAuthority).Assembly.GetManifestResourceStream("npc_authority.json")
            ?? throw new InvalidOperationException("npc_authority.json is not in the assembly");
        return FromJson(stream);
    });

    public static NpcAuthority Load() => Embedded.Value;

    public static NpcAuthority FromJson(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;
        double Real(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        string[] Strings(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? [.. v.EnumerateArray().Select(a => a.GetString() ?? "")] : [];
        (int, int)[] Pairs(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Select(pair => (pair[0].GetInt32(), pair[1].GetInt32()))]
                : [];

        var profiles = root.GetProperty("profiles").EnumerateArray().Select(p => new NpcProfile(
            Text(p, "id"), Text(p, "name"), Strings(p, "aliases"), Text(p, "entityType"), (int)Number(p, "shipTypeID"), Number(p, "corporationID"),
            Number(p, "allianceID"), (int)Number(p, "factionID"), Text(p, "loadoutID"), Text(p, "lootTableID"), Text(p, "shipNameTemplate"),
            Real(p, "securityStatus"), Number(p, "bounty"), p.TryGetProperty("spawnDistanceMeters", out var distance) ? distance.GetDouble() : 20_000));
        var loadouts = root.GetProperty("loadouts").EnumerateArray().Select(l => new NpcLoadout(Text(l, "id"), Text(l, "name"), Pairs(l, "modules"), Pairs(l, "charges")));
        var pools = root.GetProperty("pools").EnumerateArray().Select(p => new NpcPool(
            Text(p, "id"), Text(p, "name"), Strings(p, "aliases"), Text(p, "entityType"),
            [.. p.GetProperty("entries").EnumerateArray().Select(entry => new NpcPoolEntry(entry[0].GetString() ?? "", entry[1].GetInt32()))]));
        int? Whole(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : null;
        bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        var lootTables = !root.TryGetProperty("lootTables", out var tables) ? [] : tables.EnumerateArray().Select(t => new LootTable(
            Text(t, "id"), Text(t, "mode"), Whole(t, "minEntries"), Whole(t, "maxEntries"), Real(t, "emptyChance"), Flag(t, "allowDuplicates"), Flag(t, "disableRandomLoot"),
            Whole(t, "stackableMinQuantity"), Whole(t, "stackableMaxQuantity"),
            [.. t.GetProperty("selectors").EnumerateArray().Select(s => new LootSelector(
                Text(s, "kind"), Text(s, "sizeBand"), Text(s, "grade"), Strings(s, "nameIncludes"), Strings(s, "nameExcludes"), Strings(s, "groupNames"),
                s.TryGetProperty("categoryIDs", out var categories) ? [.. categories.EnumerateArray().Select(c => c.GetInt32())] : [], Whole(s, "weight"),
                Whole(s, "quantityMin"), Whole(s, "quantityMax")))],
            Flag(t, "hasSelectors"))).ToList();
        var authority = new NpcAuthority(profiles.ToList(), loadouts.ToList(), pools.ToList(), lootTables);
        if (root.TryGetProperty("pirateRegions", out var regions))
        {
            authority.WithPirateRegions(regions.EnumerateObject().Select(r => new KeyValuePair<int, string>(int.Parse(r.Name, System.Globalization.CultureInfo.InvariantCulture), r.Value.GetString() ?? "")));
        }
        return authority;
    }
}
