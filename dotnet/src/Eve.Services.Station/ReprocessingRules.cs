namespace Eve.Services.Station;

/// <summary>
/// The reprocessing rules: the station's efficiency, the character's skills, the standing tax, and what a stack yields. The numbers are
/// the SDE's (station rows, skill mutators, materials, portion sizes); the wiki gives the rules (EVE University, "Refining": base yield
/// 30%–50% by station equipment, skill bonuses per level, tax 5% at zero standing and 0% at standing 6.67 with the owner).
/// </summary>
public static class ReprocessingRules
{
    /// <summary>The SDE's Reprocessing skill (its mutator is the percentage per level).</summary>
    public const int ReprocessingSkill = 3385;

    /// <summary>The SDE's Reprocessing Efficiency skill.</summary>
    public const int ReprocessingEfficiencySkill = 3389;

    /// <summary>The SDE's Scrapmetal Processing skill: the one that applies to items with no skill of their own.</summary>
    public const int ScrapmetalProcessingSkill = 12196;

    /// <summary>The dogma attribute of a skill that holds its yield bonus per level (<c>refiningYieldMutator</c>).</summary>
    public const int RefiningYieldMutatorAttribute = 379;

    /// <summary>The dogma attribute of an item that names the skill its yield depends on (<c>reprocessingSkillType</c>).</summary>
    public const int ReprocessingSkillTypeAttribute = 790;

    /// <summary>The efficiency of an NPC station whose static data gives none (the wiki: 50% with the best equipment).</summary>
    public const double DefaultStationEfficiency = 0.5;

    /// <summary>The standing at which the NPC station tax is zero (wiki: 5% at 0, 0% at 6.67 with the owner).</summary>
    public const double StandingTaxZeroPoint = 6.67;

    /// <summary>The materials of a type, for one portion (the SDE's <c>typeMaterials</c>).</summary>
    public sealed record Material(int TypeId, long Quantity);

    /// <summary>One material as the client's quote shows it: what the client receives, what is lost, and the tax the station takes on it.</summary>
    public sealed record Recoverable(int TypeId, long Client, long Unrecoverable, double IskCost);

    /// <summary>The quote of one stack: the part that is processed, the leftovers, the efficiency and the recoverables.</summary>
    public sealed record Quote(long ItemId, int TypeId, long QuantityToProcess, long LeftOvers, long Portions, double Efficiency,
        IReadOnlyList<Recoverable> Recoverables, double TotalIskCost);

    /// <summary>The tax a station takes: its base rate, reduced by the standing toward its owner, zero at the zero point.</summary>
    public static double StationTax(double baseTax, double standing)
    {
        if (standing <= 0) return baseTax;
        var scale = 1 - Math.Min(standing, StandingTaxZeroPoint) / StandingTaxZeroPoint;
        return Round2(baseTax * Math.Max(0, scale));
    }

    /// <summary>A skill's yield multiplier: its level times its percentage per level, on 100.</summary>
    public static double SkillMultiplier(int level, double percentagePerLevel) => 1 + level * percentagePerLevel / 100.0;

    /// <summary>The efficiency a stack is processed at: the station's, times the character's skills, never above 1.</summary>
    public static double Efficiency(double stationEfficiency, double characterMultiplier) => Math.Clamp(stationEfficiency * characterMultiplier, 0, 1);

    /// <summary>The largest quantity a client can hold in one stack (EVE.js <c>CLIENT_INVENTORY_STACK_LIMIT</c>, 2^31 - 1).</summary>
    public const long ClientInventoryStackLimit = 2147483647;

    /// <summary>A quote the client can show and hold: every recoverable and every unrecoverable quantity is within the stack limit.</summary>
    public static bool IsClientSafe(Quote quote) =>
        quote.Recoverables.All(r => r.Client <= ClientInventoryStackLimit && r.Unrecoverable <= ClientInventoryStackLimit);

    /// <summary>
    /// The stacks a stack too large for the client is split into (EVE.js <c>buildOversizedReprocessingSplitPlan</c>): the root keeps the largest
    /// safe quantity aligned to whole portions, and each further chunk is the largest aligned quantity that is safe. Null when no quantity is safe.
    /// </summary>
    public static IReadOnlyList<long>? SplitChunks(long total, int portionSize, Func<long, bool> isSafe)
    {
        var portion = Math.Max(1, portionSize);
        long low = 1, high = total, best = 0;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (isSafe(middle)) { best = middle; low = middle + 1; }
            else high = middle - 1;
        }
        if (best <= 0) return null;
        var leftovers = total % portion;
        var root = leftovers + (best - leftovers) / portion * portion;
        var chunk = best / portion * portion;
        if (root <= 0 || chunk <= 0 || root > best) return null;
        var chunks = new List<long> { root };
        var remaining = Math.Max(0, total - root);
        while (remaining > 0)
        {
            var piece = Math.Min(remaining, chunk);
            if (piece <= 0) return null;
            chunks.Add(piece);
            remaining -= piece;
        }
        return chunks;
    }

    /// <summary>The quote of several stacks taken as one (the sums of their quantities, yields and taxes); the stack's own efficiency.</summary>
    public static Quote Sum(long itemId, int typeId, IReadOnlyList<Quote> parts)
    {
        var yields = parts.SelectMany(p => p.Recoverables)
            .GroupBy(r => r.TypeId)
            .Select(g => new Recoverable(g.Key, g.Sum(r => r.Client), g.Sum(r => r.Unrecoverable), Round2(g.Sum(r => r.IskCost))))
            .ToList();
        return new Quote(itemId, typeId, parts.Sum(p => p.QuantityToProcess), parts.Sum(p => p.LeftOvers), parts.Sum(p => p.Portions),
            parts.Count > 0 ? parts[0].Efficiency : 0, yields, Round2(parts.Sum(p => p.TotalIskCost)));
    }

    /// <summary>Rounds down to whole units after removing the binary noise of a chain of multiplications (the reference does the same).</summary>
    public static long FloorUnits(double value) => (long)Math.Floor(value + 1e-9);

    public static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The quote of a stack: whole portions are processed, the rest is left over; each material yields its quantity times the portions at the
    /// efficiency, rounded down; the station's tax is charged on what is yielded, at the material's adjusted price.
    /// </summary>
    public static Quote Build(long itemId, int typeId, long stackQuantity, int portionSize, double efficiency, double taxRate,
        IReadOnlyList<Material> materials, IReadOnlyDictionary<int, double> prices)
    {
        var portion = Math.Max(1, portionSize);
        var portions = stackQuantity / portion;
        var leftOvers = stackQuantity % portion;
        var toProcess = portions * portion;
        var recoverables = new List<Recoverable>(materials.Count);
        var total = 0.0;
        if (portions > 0)
        {
            foreach (var material in materials)
            {
                var quantity = material.Quantity * portions;
                var client = Math.Max(0, FloorUnits(quantity * efficiency));
                var unrecoverable = Math.Max(0, quantity - client);
                var price = prices.TryGetValue(material.TypeId, out var value) ? value : 0;
                var iskCost = Round2(price * client * taxRate);
                total += iskCost;
                recoverables.Add(new Recoverable(material.TypeId, client, unrecoverable, iskCost));
            }
        }
        return new Quote(itemId, typeId, toProcess, leftOvers, portions, efficiency, recoverables, Round2(total));
    }
}
