using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Eve.Sde;

namespace Eve.Npc;

public sealed record LootType(int TypeId, string Name, string GroupName, int CategoryId);

/// <summary>
/// One rule of a loot table: a kind of item (how big, how rare, what it is called) drawn at a weight, and how many of it. <see cref="Kind"/> is <c>ammo</c>, <c>module</c>, <c>utility</c>,
/// <c>trash</c> or anything else for no restriction on the kind; <see cref="Grade"/> lets the special grades (faction, deadspace, officer, ...) in.
/// </summary>
public sealed record LootSelector(
    string Kind, string SizeBand, string Grade, IReadOnlyList<string> NameIncludes, IReadOnlyList<string> NameExcludes, IReadOnlyList<string> GroupNames,
    IReadOnlyList<int> CategoryIds, int? Weight, int? QuantityMin, int? QuantityMax);

public sealed record LootTable(
    string Id, string Mode, int? MinEntries, int? MaxEntries, double EmptyChance, bool AllowDuplicates, bool DisableRandomLoot, int? StackableMin, int? StackableMax,
    IReadOnlyList<LootSelector> Selectors, bool HasSelectors);

/// <summary>An item an NPC leaves: what it is, how many (a stack of them) and whether it is a single unit item (a module or a ship: one each, not a stack).</summary>
public sealed record LootDrop(int TypeId, string Name, long Quantity, bool Singleton);

public sealed class NpcLoot
{
    private const int MunitionCategory = 8, ModuleCategory = 7, BlueprintCategory = 9;

    // JavaScript's \b and \w are ASCII, and the reference's patterns are JavaScript's
    private const RegexOptions Js = RegexOptions.ECMAScript | RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly HashSet<string> AmmoGroups =
    [
        "projectile ammo", "hybrid charge", "frequency crystal", "light missile", "heavy missile", "cruise missile", "rocket", "torpedo", "heavy assault missile",
    ];

    private static readonly HashSet<string> UtilityGroups =
    [
        "armor repair unit", "armor plate", "armor hardener", "armor coating", "energized armor membrane", "shield booster", "shield extender", "shield hardener",
        "shield resistance amplifier", "capacitor recharger", "capacitor battery", "capacitor booster", "capacitor power relay", "capacitor flux coil", "shield power relay",
        "shield flux coil", "propulsion module",
    ];

    private static readonly HashSet<string> ModuleGroups =
    [
        "energy weapon", "hybrid weapon", "projectile weapon", "missile launcher light", "missile launcher rapid light", "missile launcher heavy", "missile launcher heavy assault",
        "missile launcher cruise", "missile launcher rocket", "missile launcher torpedo", .. UtilityGroups,
    ];

    private static readonly Regex SpecialGrade = new(
        string.Join('|',
        [
            @"\bdread guristas\b", @"\bdomination\b", @"\bdark blood\b", @"\btrue sansha", @"\bshadow serpentis\b", @"\bofficer\b", @"\boverseer\b", @"\bdeadspace\b", @"\bcommander\b",
            @"\bblueprint\b", @"\bmutaplasmid\b", @"\babyssal\b", @"\barch angel\b", @"\bguardian\b", @"\bimperial navy\b", @"\bcaldari navy\b", @"\bfederation navy\b",
            @"\brepublic fleet\b", @"\bnavy\b", @"\bguristas\b", @"\bserpentis\b", @"\bblood\b", @"\bsansha", @"\bfleet\b", @"\bshadow\b", @"\bpolarized\b", @"\bconcord\b",
            @"\bthukker\b", @"\bsisters\b", @"\bmordu", @"\bammatar\b", @"\bkhanid\b", @"\baugmented\b", @"\bintegrated\b", @"\bmodified\b", @"\b[abcx]-type\b", @"\bcivilian\b",
            @"\bsyndicate\b", @"\bsmuggler", @"\bcosmos\b", @"\bfestival\b", @"\bevent\b",
        ]), Js);

    private static readonly Regex TechTwo = new(@"\bII\b", RegexOptions.ECMAScript | RegexOptions.Compiled);
    private static readonly Regex NotTrash = new("skill|skin|blueprint|plex|extractor|injector|biomass", Js);
    private static readonly Regex MetalScraps = new("^metal scraps$", Js);
    private static readonly Regex SmallName = new(@"\b(s|small)\b$", Js);
    private static readonly Regex MediumName = new(@"\b(m|medium)\b$", Js);
    private static readonly Regex LargeName = new(@"\b(l|large)\b$", Js);

    private static readonly Regex SmallModule = new(@"\bsmall\b|\blight\b|\brocket\b|\b125mm\b|\b150mm\b|\b200mm\b|\b1mn\b|\bmicro\b", Js);
    private static readonly Regex MediumModule = new(@"\bmedium\b|\bheavy\b|\bassault\b|\b250mm\b|\b350mm\b|\b425mm\b|\b10mn\b", Js);
    private static readonly Regex LargeModule = new(@"\blarge\b|\bcruise\b|\btorpedo\b|\b650mm\b|\b720mm\b|\b800mm\b|\b1200mm\b|\b1400mm\b|\bmega\b|\b100mn\b", Js);

    private readonly LootType[] _pool;
    private readonly ConcurrentDictionary<LootSelector, LootType[]> _candidates = new(ReferenceEqualityComparer.Instance);

    /// <summary>The types everything is drawn from; a type without a name, and a wreck, is never drawn (the caller leaves out what is not published).</summary>
    public NpcLoot(IEnumerable<LootType> types) =>
        _pool = [.. types.Where(t => t.TypeId > 0 && t.Name.Trim().Length > 0 && !t.GroupName.Trim().Equals("wreck", StringComparison.OrdinalIgnoreCase))];

    public int PoolSize => _pool.Length;

    /// <summary>The types of the static data a plain draw and the rules choose from: every published type with the name and the category of its group.</summary>
    public static async Task<NpcLoot> LoadAsync(SdeReader sde, CancellationToken cancellationToken = default)
    {
        var groups = (await sde.ReadAsync("groups", cancellationToken: cancellationToken))
            .ToDictionary(g => (int)g.Key, g => (Name: g.Doc.TryGetProperty("name", out var n) ? SdeReader.English(n) : "", Category: g.Doc.GetProperty("categoryID").GetInt32()));
        var types = await sde.ReadTypeSummariesAsync(cancellationToken);
        return new NpcLoot(types.Where(t => t.Published && groups.ContainsKey(t.GroupId)).Select(t => new LootType(t.TypeId, t.Name, groups[t.GroupId].Name, groups[t.GroupId].Category)));
    }

    /// <summary>What the table leaves this time; <paramref name="random"/> gives numbers from 0 up to (not including) 1.</summary>
    public IReadOnlyList<LootDrop> Roll(LootTable? table, Func<double> random)
    {
        if (table is null || _pool.Length == 0)
        {
            return [];
        }
        if (table.Mode.Trim().Equals("rule", StringComparison.OrdinalIgnoreCase) || table.HasSelectors)
        {
            return RollRules(table, random);
        }
        if (table.DisableRandomLoot || (NonNegative(table.MinEntries, 1) == 0 && NonNegative(table.MaxEntries, 1) == 0))
        {
            return [];
        }
        var min = Positive(table.MinEntries, 1);
        var max = Math.Max(min, Positive(table.MaxEntries, min));
        var count = min + (int)Math.Floor(random() * (max - min + 1));
        var stackMin = Positive(table.StackableMin, 1);
        var stackMax = Math.Max(stackMin, Positive(table.StackableMax, stackMin));
        var drops = new List<LootDrop>(count);
        for (var i = 0; i < count; i++)
        {
            var type = Choose(_pool, random);
            var stackable = IsStackable(type);
            drops.Add(new LootDrop(type.TypeId, type.Name, stackable ? stackMin + (long)Math.Floor(random() * (stackMax - stackMin + 1)) : 1, !stackable));
        }
        return drops;
    }

    private List<LootDrop> RollRules(LootTable table, Func<double> random)
    {
        var drops = new List<LootDrop>();
        var emptyChance = Math.Clamp(table.EmptyChance, 0, 1);
        if (emptyChance > 0 && random() < emptyChance)
        {
            return drops;
        }
        if (table.Selectors.Count == 0)
        {
            return drops;
        }
        var min = NonNegative(table.MinEntries, 0);
        var max = Math.Max(min, NonNegative(table.MaxEntries, min));
        var count = min + (int)Math.Floor(random() * (max - min + 1));
        var candidates = table.Selectors
            .Select(selector => (Selector: selector, Weight: Math.Max(1, Positive(selector.Weight, 1)), Pool: CandidatesOf(selector)))
            .Where(c => c.Pool.Length > 0)
            .ToList();
        var used = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            if (candidates.Count == 0)
            {
                break;
            }
            var chosen = ChooseWeighted(candidates, c => c.Weight, random);
            var available = table.AllowDuplicates ? chosen.Pool : [.. chosen.Pool.Where(t => !used.Contains(t.TypeId))];
            if (available.Length == 0)
            {
                // nothing left for this rule: it is not asked again, and the draw is made again
                candidates.Remove(chosen);
                i--;
                continue;
            }
            var type = Choose(available, random);
            var stackable = IsStackable(type);
            used.Add(type.TypeId);
            drops.Add(new LootDrop(
                type.TypeId, type.Name,
                stackable ? RollQuantity(chosen.Selector.QuantityMin ?? table.StackableMin, chosen.Selector.QuantityMax ?? table.StackableMax, random) : 1, !stackable));
        }
        return drops;
    }

    private LootType[] CandidatesOf(LootSelector selector) => _candidates.GetOrAdd(selector, s => [.. _pool.Where(type => Matches(type, s))]);

    private static bool Matches(LootType type, LootSelector selector)
    {
        if (!IsEligible(type, selector) || !MatchesText(type, selector))
        {
            return false;
        }
        var kind = Lower(selector.Kind.Length == 0 ? "any" : selector.Kind);
        var group = Lower(type.GroupName);
        return kind switch
        {
            "ammo" or "charge" => type.CategoryId == MunitionCategory && AmmoGroups.Contains(group) && AmmoMatchesSize(type, selector.SizeBand),
            "module" => type.CategoryId == ModuleCategory && ModuleGroups.Contains(group) && ModuleMatchesSize(type, selector.SizeBand),
            "utility" or "tank" => type.CategoryId == ModuleCategory && UtilityGroups.Contains(group) && ModuleMatchesSize(type, selector.SizeBand),
            "trash" or "salvage" => (group == "salvaged materials" || MetalScraps.IsMatch(type.Name)) && !NotTrash.IsMatch(type.Name),
            _ => true,
        };
    }

    /// <summary>A type a rule may give: not a blueprint, not tech two, and not of a special grade unless the rule lets those in.</summary>
    private static bool IsEligible(LootType type, LootSelector selector) =>
        !IsBlueprint(type) && (AllowsSpecialGrade(selector) || !SpecialGrade.IsMatch($"{type.Name} {type.GroupName}")) && !TechTwo.IsMatch(type.Name);

    private static bool IsBlueprint(LootType type) =>
        type.CategoryId == BlueprintCategory || Lower(type.GroupName).Contains("blueprint", StringComparison.Ordinal) || Lower(type.Name).Contains("blueprint", StringComparison.Ordinal);

    private static bool AllowsSpecialGrade(LootSelector selector) => Lower(selector.Grade) is "faction" or "commander" or "deadspace" or "officer" or "special";

    private static bool MatchesText(LootType type, LootSelector selector)
    {
        var name = Lower(type.Name);
        var group = Lower(type.GroupName);
        var text = $"{name} {group}";
        var includes = selector.NameIncludes.Select(Lower).Where(s => s.Length > 0).ToArray();
        if (includes.Length > 0 && !includes.Any(needle => text.Contains(needle, StringComparison.Ordinal)))
        {
            return false;
        }
        if (selector.NameExcludes.Select(Lower).Where(s => s.Length > 0).Any(needle => text.Contains(needle, StringComparison.Ordinal)))
        {
            return false;
        }
        var groups = selector.GroupNames.Select(Lower).Where(s => s.Length > 0).ToArray();
        if (groups.Length > 0 && !groups.Contains(group))
        {
            return false;
        }
        return selector.CategoryIds.Count == 0 || selector.CategoryIds.Contains(type.CategoryId);
    }

    private static bool AmmoMatchesSize(LootType type, string sizeBand)
    {
        var name = Lower(type.Name);
        var group = Lower(type.GroupName);
        return Lower(sizeBand) switch
        {
            "small" => SmallName.IsMatch(name) || group.Contains("light missile", StringComparison.Ordinal) || group.Contains("rocket", StringComparison.Ordinal),
            "medium" => MediumName.IsMatch(name) || group.Contains("heavy missile", StringComparison.Ordinal) || group.Contains("heavy assault missile", StringComparison.Ordinal),
            "large" => LargeName.IsMatch(name) || group.Contains("cruise missile", StringComparison.Ordinal) || group.Contains("torpedo", StringComparison.Ordinal),
            _ => true,
        };
    }

    private static bool ModuleMatchesSize(LootType type, string sizeBand)
    {
        var text = $"{type.Name} {type.GroupName}";
        return Lower(sizeBand) switch
        {
            "small" => SmallModule.IsMatch(text),
            "medium" => MediumModule.IsMatch(text),
            "large" => LargeModule.IsMatch(text),
            _ => true,
        };
    }

    /// <summary>Charges, ammunition, minerals and the like are stacks of many; anything else is one unit of its own.</summary>
    private static bool IsStackable(LootType type) => type.CategoryId is 4 or 5 or 8 or 17 or 25;

    private static T Choose<T>(IReadOnlyList<T> entries, Func<double> random) => entries[Math.Min(entries.Count - 1, (int)Math.Floor(random() * entries.Count))];

    private static T ChooseWeighted<T>(IReadOnlyList<T> entries, Func<T, int> weight, Func<double> random)
    {
        long total = 0;
        foreach (var entry in entries)
        {
            total += weight(entry);
        }
        var roll = random() * total;
        foreach (var entry in entries)
        {
            roll -= weight(entry);
            if (roll < 0)
            {
                return entry;
            }
        }
        return entries[^1];
    }

    private static long RollQuantity(int? minimum, int? maximum, Func<double> random)
    {
        var min = Math.Max(1, Positive(minimum, 1));
        var max = Math.Max(min, Positive(maximum, min));
        return min + (long)Math.Floor(random() * (max - min + 1));
    }

    private static string Lower(string? value) => (value ?? "").Trim().ToLowerInvariant();

    /// <summary><c>toPositiveInt</c>: the number cut to a whole one when it is above nothing, else the fallback.</summary>
    private static int Positive(int? value, int fallback) => value is > 0 ? value.Value : fallback;

    /// <summary><c>toNonNegativeInt</c>: a number that is not there is nothing, not the fallback (which only a number below nothing gets).</summary>
    private static int NonNegative(int? value, int fallback) => value is < 0 ? fallback : value ?? 0;
}
