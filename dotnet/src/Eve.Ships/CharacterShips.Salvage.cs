using Eve.Dogma;

namespace Eve.Ships;

public sealed partial class CharacterShips
{
    private readonly PgWreckSalvageStore _wreckSalvage = new(source, spaceSchema);

    /// <summary>Draws the bundle once, when the wreck is created.</summary>
    public Func<double> SalvageRewardRoll { get; set; } = Random.Shared.NextDouble;

    private async Task<IReadOnlyList<(int TypeId, long Quantity)>> RollWreckSalvageAsync(
        int wreckTypeId, long warFactionId, CancellationToken cancellationToken)
    {
        await statics.EnsureTypesAsync([wreckTypeId], cancellationToken);
        var wreckType = statics.Type(wreckTypeId)
            ?? throw new InvalidDataException($"Wreck type {wreckTypeId} is absent from the SDE.");
        var products = await statics.TypesInGroupsAsync(["Salvaged Materials"], cancellationToken);
        if (products.Count == 0)
        {
            throw new InvalidDataException("The SDE contains no salvage products.");
        }
        return SelectSalvageRewards(wreckType, warFactionId, products);
    }

    private IReadOnlyList<(int TypeId, long Quantity)> SelectSalvageRewards(
        TypeDef wreckType, long warFactionId, IReadOnlyList<TypeDef> products)
    {
        var factionName = warFactionId > 0 && warFactionId <= int.MaxValue
            ? statics.FactionName((int)warFactionId)
            : "";
        var identity = $"{factionName} {wreckType.Name}";
        var size = identity.Contains("Capital", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Carrier", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Dreadnought", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Titan", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Freighter", StringComparison.OrdinalIgnoreCase)
            ? 3
            : identity.Contains("Battleship", StringComparison.OrdinalIgnoreCase)
                ? 2
                : identity.Contains("Cruiser", StringComparison.OrdinalIgnoreCase)
                    || identity.Contains("Battlecruiser", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
        var emptyChance = size switch { 0 => 0.22, 1 => 0.14, 2 => 0.08, _ => 0.02 };
        double Roll()
        {
            var value = SalvageRewardRoll();
            return Math.Clamp(double.IsFinite(value) ? value : 0, 0, Math.BitDecrement(1d));
        }
        if (Roll() < emptyChance)
        {
            return [];
        }

        var pool = BuildSalvagePool(
            ClassifySalvageFaction(identity),
            IsLargeSalvageWreck(identity),
            products.Select(product => (product.TypeId, product.Name)).ToArray());
        var rollCount = size switch { 3 => 4 + (int)(Roll() * 5), 2 => 1 + (int)(Roll() * 3), 1 => 1 + (int)(Roll() * 2), _ => 1 };
        var rewards = new Dictionary<int, long>();
        for (var index = 0; index < rollCount; index++)
        {
            var typeId = pool.Count == 0 ? null : SelectWeightedSalvageProductTypeId(pool, Roll());
            if (typeId is not { } selectedTypeId)
            {
                continue;
            }
            var quantity = size switch { 3 => 2 + (long)(Roll() * 4), 2 => 1 + (long)(Roll() * 3), 1 => 1 + (long)(Roll() * 2), _ => 1 };
            rewards[selectedTypeId] = rewards.GetValueOrDefault(selectedTypeId) + quantity;
        }
        return rewards.Select(reward => (reward.Key, reward.Value)).ToArray();
    }

    private readonly record struct SalvagePoolEntry(string Name, int Weight, bool LargeOnly = false);

    private static readonly SalvagePoolEntry[] CommonT1SalvagePool =
    [
        new("Tripped Power Circuit", 16),
        new("Burned Logic Circuit", 16),
        new("Charred Micro Circuit", 13),
        new("Fried Interface Circuit", 13),
        new("Contaminated Nanite Compound", 10),
        new("Damaged Artificial Neural Network", 8),
        new("Malfunctioning Shield Emitter", 8),
    ];

    private static readonly IReadOnlyDictionary<string, SalvagePoolEntry[]> SalvagePoolsByFaction =
        new Dictionary<string, SalvagePoolEntry[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["angel"] =
            [
                new("Alloyed Tritanium Bar", 18),
                new("Armor Plates", 12),
                new("Smashed Trigger Unit", 12),
                new("Thruster Console", 10),
                new("Malfunctioning Shield Emitter", 8),
                new("Single-crystal Superalloy I-beam", 2, LargeOnly: true),
            ],
            ["blood"] =
            [
                new("Armor Plates", 18),
                new("Melted Capacitor Console", 16),
                new("Current Pump", 10),
                new("Power Circuit", 8),
                new("Capacitor Console", 3, LargeOnly: true),
                new("Intact Armor Plates", 2, LargeOnly: true),
            ],
            ["sansha"] =
            [
                new("Armor Plates", 17),
                new("Melted Capacitor Console", 16),
                new("Current Pump", 10),
                new("Power Circuit", 8),
                new("Capacitor Console", 3, LargeOnly: true),
                new("Intact Armor Plates", 2, LargeOnly: true),
            ],
            ["guristas"] =
            [
                new("Malfunctioning Shield Emitter", 18),
                new("Scorched Telemetry Processor", 14),
                new("Ward Console", 8),
                new("Tripped Power Circuit", 8),
                new("Enhanced Ward Console", 2, LargeOnly: true),
                new("Intact Shield Emitter", 2, LargeOnly: true),
            ],
            ["serpentis"] =
            [
                new("Burned Logic Circuit", 16),
                new("Fried Interface Circuit", 16),
                new("Tripped Power Circuit", 12),
                new("Broken Drone Transceiver", 10),
                new("Damaged Artificial Neural Network", 8),
                new("Armor Plates", 4, LargeOnly: true),
            ],
            ["drone"] =
            [
                new("Broken Drone Transceiver", 18),
                new("Damaged Artificial Neural Network", 15),
                new("Contaminated Nanite Compound", 14),
                new("Conductive Polymer", 10),
                new("Drone Transceiver", 3, LargeOnly: true),
                new("Artificial Neural Network", 2, LargeOnly: true),
            ],
            ["mercenary"] =
            [
                new("Tripped Power Circuit", 14),
                new("Burned Logic Circuit", 14),
                new("Armor Plates", 10),
                new("Malfunctioning Shield Emitter", 10),
                new("Melted Capacitor Console", 8),
            ],
        };

    internal static string ClassifySalvageFaction(string source)
    {
        if (SalvageSourceMatches(source, @"\b(angel|gist|domination)\b")) return "angel";
        if (SalvageSourceMatches(source, @"\b(blood|corpus|corpii|corpum|dark blood)\b")) return "blood";
        if (SalvageSourceMatches(source, @"\b(sansha|centii|centum|centus|true sansha)\b")) return "sansha";
        if (SalvageSourceMatches(source, @"\b(guristas|pith|dread guristas|dire pithi|dire pithum|dire pithatis)\b")) return "guristas";
        if (SalvageSourceMatches(source, @"\b(serpentis|coreli|corelum|core|shadow serpentis)\b")) return "serpentis";
        if (SalvageSourceMatches(source, @"\b(rogue drone|drone|alvi|alvum|alvus|strain|infested)\b")) return "drone";
        if (SalvageSourceMatches(source, @"\b(mercenary|mordus|mordu|state navy|federation navy|imperial navy|republic fleet)\b")) return "mercenary";
        return "generic";
    }

    internal static bool IsLargeSalvageWreck(string source) =>
        SalvageSourceMatches(source, @"\b(titan|supercarrier|carrier|dreadnought|freighter|capital|force auxiliary|fax|large|battleship|battlecruiser|industrial command|command ship|marauder|black ops)\b");

    private static bool SalvageSourceMatches(string source, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(source, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static IReadOnlyList<(int TypeId, double Weight)> BuildSalvagePool(
        string faction, bool isLarge, IReadOnlyList<(int TypeId, string Name)> products)
    {
        var pool = new List<(int TypeId, double Weight)>();
        if (SalvagePoolsByFaction.TryGetValue(faction, out var factionPool))
        {
            Add(factionPool);
        }
        Add(CommonT1SalvagePool);
        return pool;

        void Add(IReadOnlyList<SalvagePoolEntry> entries)
        {
            foreach (var entry in entries)
            {
                if (entry.Weight <= 0 || (entry.LargeOnly && !isLarge))
                {
                    continue;
                }
                var typeId = products.FirstOrDefault(product =>
                    product.TypeId > 0 && string.Equals(product.Name, entry.Name, StringComparison.OrdinalIgnoreCase)).TypeId;
                if (typeId > 0)
                {
                    pool.Add((typeId, entry.Weight));
                }
            }
        }
    }

    internal static int? SelectWeightedSalvageProductTypeId(
        IReadOnlyList<(int TypeId, double Weight)> pool, double roll)
    {
        var totalWeight = pool
            .Where(candidate => candidate.TypeId > 0 && double.IsFinite(candidate.Weight) && candidate.Weight > 0)
            .Sum(candidate => candidate.Weight);
        if (!double.IsFinite(totalWeight) || totalWeight <= 0 || !double.IsFinite(roll))
        {
            return null;
        }

        var remaining = Math.Clamp(roll, 0, Math.BitDecrement(1d)) * totalWeight;
        var lastTypeId = 0;
        foreach (var candidate in pool)
        {
            if (candidate.TypeId <= 0 || !double.IsFinite(candidate.Weight) || candidate.Weight <= 0)
            {
                continue;
            }
            lastTypeId = candidate.TypeId;
            remaining -= candidate.Weight;
            if (remaining <= 0)
            {
                return candidate.TypeId;
            }
        }
        return lastTypeId > 0 ? lastTypeId : null;
    }
}
