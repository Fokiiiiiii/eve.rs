namespace Eve.Wormholes;

/// <summary>One planned signature of a J-space system: its category and, for a combat site, the name of its template (EVE University, "Wormhole Space").</summary>
public sealed record PlannedSignature(string Category, string? TemplateName);

/// <summary>
/// The sites of J-space (slice 9): the signatures of each system and what they lead to. The rules are the project's, where nothing is published
/// (written in DEVIATIONS): a count of 4 to 9 signatures per system drawn from its seed; a category by share (combat 40%, relic 15%, data 15%,
/// gas 20%, ore 10%); a combat site named from the wiki's list for the system's class (C13 uses the list of class 6); a Drifter system has
/// exactly five combat sites (the list of class 6). The wiki names only combat sites, so a relic, data, gas or ore signature has no template
/// name in the sources and is planned with no name (its template is not chosen; DEVIATIONS).
/// </summary>
public static class WormholeSites
{
    /// <summary>The scan strength of every wormhole signature: the SDE dogma default of <c>scanWormholeStrength</c> (attribute 1908), 0.0 (project rule).</summary>
    public const double ScanStrength = 0.0;

    public const int MinSignatures = 4;
    public const int MaxSignatures = 9;
    public const int DrifterCombatSites = 5;

    /// <summary>The share of each category among the signatures (the project rule).</summary>
    public static readonly (string Category, double Share)[] Shares =
    [
        ("combat", 0.40), ("relic", 0.15), ("data", 0.15), ("gas", 0.20), ("ore", 0.10),
    ];

    /// <summary>The combat site names of each class (EVE University, "Wormhole Space"; each name exists in the SDE's dungeon data, archetype 24).</summary>
    public static readonly IReadOnlyDictionary<int, string[]> CombatNames = new Dictionary<int, string[]>
    {
        [1] = ["Perimeter Ambush Point", "Perimeter Camp", "Phase Catalyst Node", "The Line"],
        [2] = ["Perimeter Checkpoint", "Perimeter Hangar", "The Ruins of Enclave Cohort 27", "Sleeper Data Sanctuary"],
        [3] = ["Fortification Frontier Stronghold", "Outpost Frontier Stronghold", "Solar Cell", "The Oruze Construct"],
        [4] = ["Frontier Barracks", "Frontier Command Post", "Integrated Terminus", "Sleeper Information Sanctum"],
        [5] = ["Core Garrison", "Core Stronghold", "Oruze Osobnyk", "Quarantine Area"],
        [6] = ["Core Citadel", "Core Bastion", "Strange Energy Readings", "The Mirror"],
    };

    /// <summary>The signatures of a system: its count from the seed, then each one's category and, for combat, its name from the class's list.</summary>
    public static IReadOnlyList<PlannedSignature> Plan(int wormholeClass, bool drifter, ulong seed)
    {
        var random = new SeededRandom(seed);
        var listClass = wormholeClass == 13 ? 6 : wormholeClass;
        if (drifter)
        {
            return Enumerable.Range(0, DrifterCombatSites)
                .Select(_ => new PlannedSignature("combat", Name(6, random))).ToArray();
        }
        if (!CombatNames.ContainsKey(listClass))
        {
            return [];
        }
        var count = MinSignatures + random.Next(MaxSignatures - MinSignatures + 1);
        var planned = new List<PlannedSignature>(count);
        for (var i = 0; i < count; i++)
        {
            var category = Category(random);
            planned.Add(new PlannedSignature(category, category == "combat" ? Name(listClass, random) : null));
        }
        return planned;
    }

    private static string Category(SeededRandom random)
    {
        var draw = random.NextDouble();
        var cumulative = 0.0;
        foreach (var (category, share) in Shares)
        {
            cumulative += share;
            if (draw < cumulative)
            {
                return category;
            }
        }
        return Shares[^1].Category;
    }

    private static string Name(int listClass, SeededRandom random)
    {
        var names = CombatNames[listClass];
        return names[random.Next(names.Length)];
    }
}
