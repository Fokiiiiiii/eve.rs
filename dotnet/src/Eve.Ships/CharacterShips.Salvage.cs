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
        var difficultyId = statics.AttributeId("accessDifficulty");
        var advanced = identity.Contains("Tech II", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("T2", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Officer", StringComparison.OrdinalIgnoreCase)
            || identity.Contains("Faction Wreck", StringComparison.OrdinalIgnoreCase);
        if (difficultyId is { } attributeId
            && wreckType.Attributes.TryGetValue(attributeId, out var baseChance)
            && baseChance <= 0)
        {
            advanced = true;
        }

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

        var materialNames = SalvageMaterialNames(identity, advanced);
        IReadOnlyList<TypeDef> pool = materialNames is null
            ? products
            : products.Where(product => materialNames.Contains(product.Name, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (pool.Count == 0)
        {
            pool = products;
        }
        var ordered = pool.OrderBy(product => product.TypeId).ToArray();
        var rollCount = size switch { 3 => 4 + (int)(Roll() * 5), 2 => 1 + (int)(Roll() * 3), 1 => 1 + (int)(Roll() * 2), _ => 1 };
        var rewards = new Dictionary<int, long>();
        for (var index = 0; index < rollCount; index++)
        {
            var product = ordered[(int)(Roll() * ordered.Length)];
            var quantity = size switch { 3 => 2 + (long)(Roll() * 4), 2 => 1 + (long)(Roll() * 3), 1 => 1 + (long)(Roll() * 2), _ => 1 };
            rewards[product.TypeId] = rewards.GetValueOrDefault(product.TypeId) + quantity;
        }
        return rewards.Select(reward => (reward.Key, reward.Value)).ToArray();
    }

    private static string[]? SalvageMaterialNames(string identity, bool advanced)
    {
        static bool Has(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);

        if (advanced)
        {
            if (Has(identity, "Angel"))
            {
                return ["Artificial Neural Network", "Impetus Console", "Interface Circuit", "Logic Circuit",
                    "Micro Circuit", "Power Circuit", "Single-crystal Superalloy I-beam", "Trigger Unit"];
            }
            if (Has(identity, "Blood"))
            {
                return ["Artificial Neural Network", "Capacitor Console", "Interface Circuit", "Logic Circuit",
                    "Micro Circuit", "Power Circuit", "Power Conduit"];
            }
            if (Has(identity, "Guristas"))
            {
                return ["Artificial Neural Network", "Enhanced Ward Console", "Intact Shield Emitter",
                    "Interface Circuit", "Logic Circuit", "Micro Circuit", "Power Circuit", "Telemetry Processor"];
            }
            if (Has(identity, "Sansha"))
            {
                return ["Artificial Neural Network", "Current Pump", "Intact Armor Plates", "Interface Circuit",
                    "Logic Circuit", "Micro Circuit", "Nanite Compound", "Power Circuit"];
            }
            if (Has(identity, "Serpentis"))
            {
                return ["Conductive Thermoplastic", "Drone Transceiver", "Interface Circuit", "Logic Circuit",
                    "Lorentz Fluid", "Micro Circuit", "Power Circuit"];
            }
            return null;
        }

        if (Has(identity, "Rogue Drone"))
        {
            return ["Alloyed Tritanium Bar", "Armor Plates", "Broken Drone Transceiver", "Burned Logic Circuit",
                "Charred Micro Circuit", "Conductive Polymer", "Contaminated Lorentz Fluid", "Contaminated Nanite Compound",
                "Defective Current Pump", "Fried Interface Circuit", "Malfunctioning Shield Emitter", "Melted Capacitor Console",
                "Scorched Telemetry Processor", "Smashed Trigger Unit", "Tangled Power Conduit", "Thruster Console",
                "Tripped Power Circuit", "Ward Console"];
        }
        if (Has(identity, "Minmatar") || Has(identity, "Angel"))
        {
            return ["Alloyed Tritanium Bar", "Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit",
                "Contaminated Nanite Compound", "Fried Interface Circuit", "Malfunctioning Shield Emitter",
                "Smashed Trigger Unit", "Thruster Console", "Tripped Power Circuit"];
        }
        if (Has(identity, "Caldari") || Has(identity, "Guristas"))
        {
            return ["Burned Logic Circuit", "Charred Micro Circuit", "Conductive Polymer",
                "Damaged Artificial Neural Network", "Fried Interface Circuit", "Malfunctioning Shield Emitter",
                "Scorched Telemetry Processor", "Tripped Power Circuit", "Ward Console"];
        }
        if (Has(identity, "Gallente") || Has(identity, "Serpentis"))
        {
            return ["Armor Plates", "Broken Drone Transceiver", "Burned Logic Circuit", "Charred Micro Circuit",
                "Conductive Polymer", "Contaminated Lorentz Fluid", "Contaminated Nanite Compound",
                "Damaged Artificial Neural Network", "Fried Interface Circuit", "Tripped Power Circuit"];
        }
        if (Has(identity, "Amarr") || Has(identity, "Blood") || Has(identity, "Sansha"))
        {
            return ["Armor Plates", "Burned Logic Circuit", "Charred Micro Circuit",
                "Contaminated Nanite Compound", "Defective Current Pump", "Fried Interface Circuit",
                "Melted Capacitor Console", "Tangled Power Conduit", "Tripped Power Circuit"];
        }
        return null;
    }
}
