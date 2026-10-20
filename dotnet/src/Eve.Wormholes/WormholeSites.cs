namespace Eve.Wormholes;

/// <summary>One planned signature of a J-space system: its category and the site it leads to (its name and client dungeon key).</summary>
public sealed record PlannedSignature(string Category, string SiteName, int DungeonKey);

public sealed record Site(string Name, int DungeonKey);

/// <summary>
/// The sites of J-space (slices 9 and 10): the signatures of each system and the site each one leads to. The counts, the shares and the Drifter rule
/// are the project's, where nothing is published (DEVIATIONS): 4 to 9 signatures per system from the seed; a category by share (combat 40%, relic 15%,
/// lists plus Shattered Debris Field; a Drifter system has exactly five combat sites (the class 6 anomalies).
/// </summary>
public static class WormholeSites
{
    /// <summary>The scan strength of every wormhole signature: the SDE dogma default of <c>scanWormholeStrength</c> (attribute 1908), 0.0 (project rule).</summary>
    public const double ScanStrength = 0.0;

    public const int MinSignatures = 4;
    public const int MaxSignatures = 9;
    public const int DrifterCombatSites = 5;
    public const int ShatteredClass = 13;

    /// <summary>The first wormhole class of a Drifter system (classes 14 to 18).</summary>
    public const int DrifterFirstClass = 14;

    /// <summary>The share of each category among the signatures (the project rule).</summary>
    public static readonly (string Category, double Share)[] Shares =
    [
        ("combat", 0.40), ("relic", 0.15), ("data", 0.15), ("gas", 0.20), ("ore", 0.10),
    ];

    /// <summary>The cosmic anomalies of each class (combat sites).</summary>
    public static readonly IReadOnlyDictionary<int, Site[]> CombatSites = new Dictionary<int, Site[]>
    {
        [1] = [new("Perimeter Ambush Point", 3707), new("Perimeter Camp", 3708), new("Phase Catalyst Node", 3801), new("The Line", 3797)],
        [2] = [new("Perimeter Checkpoint", 3714), new("Perimeter Hangar", 3715), new("The Ruins of Enclave Cohort 27", 3795), new("Sleeper Data Sanctuary", 3796)],
        [3] = [new("Fortification Frontier Stronghold", 3710), new("Outpost Frontier Stronghold", 3713), new("Solar Cell", 3809), new("The Oruze Construct", 3800)],
        [4] = [new("Frontier Barracks", 3709), new("Frontier Command Post", 3718), new("Integrated Terminus", 3799), new("Sleeper Information Sanctum", 3808)],
        [5] = [new("Core Garrison", 3716), new("Core Stronghold", 3717), new("Oruze Osobnyk", 3803), new("Quarantine Area", 3812)],
        [6] = [new("Core Citadel", 3704), new("Core Bastion", 3706), new("Strange Energy Readings", 3819), new("The Mirror", 3802)],
    };

    /// <summary>The data sites of each class.</summary>
    public static readonly IReadOnlyDictionary<int, Site[]> DataSites = new Dictionary<int, Site[]>
    {
        [1] = [new("Unsecured Perimeter Amplifier", 3753), new("Unsecured Perimeter Information Center", 3754)],
        [2] = [new("Unsecured Perimeter Comms Relay", 3759), new("Unsecured Perimeter Transponder Farm", 3752)],
        [3] = [new("Unsecured Frontier Database", 3735), new("Unsecured Frontier Receiver", 3746)],
        [4] = [new("Unsecured Frontier Digital Nexus", 3748), new("Unsecured Frontier Trinary Hub", 3744)],
        [5] = [new("Unsecured Frontier Enclave Relay", 3729), new("Unsecured Frontier Server Bank", 3732)],
        [6] = [new("Unsecured Core Backup Array", 3747), new("Unsecured Core Emergence", 3741)],
    };

    /// <summary>The relic sites of each class.</summary>
    public static readonly IReadOnlyDictionary<int, Site[]> RelicSites = new Dictionary<int, Site[]>
    {
        [1] = [new("Forgotten Perimeter Coronation Platform", 3737), new("Forgotten Perimeter Power Array", 3730)],
        [2] = [new("Forgotten Perimeter Gateway", 3749), new("Forgotten Perimeter Habitation Coils", 3739)],
        [3] = [new("Forgotten Frontier Quarantine Outpost", 3726), new("Forgotten Frontier Recursive Depot", 3725)],
        [4] = [new("Forgotten Frontier Conversion Module", 3736), new("Forgotten Frontier Evacuation Center", 3731)],
        [5] = [new("Forgotten Core Data Field", 3722), new("Forgotten Core Information Pen", 3728)],
        [6] = [new("Forgotten Core Assembly Hall", 3727), new("Forgotten Core Circuitry Disassembler", 3723)],
    };

    /// <summary>The gas sites (reservoirs) by zone: perimeter C1 to C4, frontier C3 to C6, core C5 and C6 (and Shattered).</summary>
    private static readonly Site[] PerimeterGas =
    [
        new("Barren Perimeter Reservoir", 3555), new("Token Perimeter Reservoir", 3556), new("Minor Perimeter Reservoir", 3557),
        new("Ordinary Perimeter Reservoir", 3558), new("Sizeable Perimeter Reservoir", 3559),
    ];

    private static readonly Site[] FrontierGas = [new("Bountiful Frontier Reservoir", 3560), new("Vast Frontier Reservoir", 3561)];

    private static readonly Site[] CoreGas = [new("Instrumental Core Reservoir", 3562), new("Vital Core Reservoir", 3563)];

    /// <summary>The ore sites (deposits) by zone: perimeter and frontier in every class, core in C5 and C6 (and Shattered).</summary>
    private static readonly Site[] PerimeterOre = [new("Common Perimeter Deposit", 3811), new("Ordinary Perimeter Deposit", 3815)];

    private static readonly Site[] FrontierOre = [new("Average Frontier Deposit", 3807), new("Unexceptional Frontier Deposit", 3814)];

    private static readonly Site[] CoreOre =
    [
        new("Exceptional Core Deposit", 3822), new("Infrequent Core Deposit", 3818), new("Isolated Core Deposit", 3813),
        new("Rarified Core Deposit", 3820), new("Uncommon Core Deposit", 3823), new("Unusual Core Deposit", 3817),
    ];

    /// <summary>The ore site of Shattered systems only (C13).</summary>
    private static readonly Site ShatteredDebrisField = new("Shattered Debris Field", 5605);

    /// <summary>The signatures of a system: its count from the seed, then each one's category and the site drawn from that category's pool.</summary>
    public static IReadOnlyList<PlannedSignature> Plan(int wormholeClass, bool drifter, ulong seed)
    {
        var random = new SeededRandom(seed);
        if (drifter)
        {
            return Enumerable.Range(0, DrifterCombatSites).Select(_ => Draw("combat", 6, random)).ToArray();
        }
        if (!IsPlanned(wormholeClass))
        {
            return [];
        }
        var count = MinSignatures + random.Next(MaxSignatures - MinSignatures + 1);
        var planned = new List<PlannedSignature>(count);
        for (var i = 0; i < count; i++)
        {
            planned.Add(Draw(Category(random), wormholeClass, random));
        }
        return planned;
    }

    /// <summary>The sites a signature of this category (combat, data, relic, gas or ore) can lead to in this class.</summary>
    public static IReadOnlyList<Site> PoolOf(string category, int wormholeClass) => Pool(category, wormholeClass);

    private static bool IsPlanned(int wormholeClass) => wormholeClass is >= 1 and <= 6 || wormholeClass == ShatteredClass;

    private static PlannedSignature Draw(string category, int wormholeClass, SeededRandom random)
    {
        var pool = Pool(category, wormholeClass);
        var site = pool[random.Next(pool.Length)];
        return new PlannedSignature(category, site.Name, site.DungeonKey);
    }

    /// <summary>The sites a signature of this category can lead to in this class (C13 uses the class 6 lists).</summary>
    private static Site[] Pool(string category, int wormholeClass)
    {
        var listClass = wormholeClass == ShatteredClass ? 6 : wormholeClass;
        return category switch
        {
            "combat" => CombatSites[listClass],
            "data" => DataSites[listClass],
            "relic" => RelicSites[listClass],
            "gas" => Zones(listClass, (1, 4, PerimeterGas), (3, 6, FrontierGas), (5, 6, CoreGas)),
            _ when wormholeClass == ShatteredClass =>
                Zones(listClass, (1, 6, PerimeterOre), (1, 6, FrontierOre), (5, 6, CoreOre)).Append(ShatteredDebrisField).ToArray(),
            _ => Zones(listClass, (1, 6, PerimeterOre), (1, 6, FrontierOre), (5, 6, CoreOre)),
        };
    }

    private static Site[] Zones(int listClass, params (int From, int To, Site[] Sites)[] zones) =>
        zones.Where(zone => listClass >= zone.From && listClass <= zone.To).SelectMany(zone => zone.Sites).ToArray();

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
}
