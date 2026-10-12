namespace Eve.Services.Station;

/// <summary>
/// The repair rules of the station repair shop (EVE.js <c>repairRuntime.js</c>): the health of an item in HP, the cost of one unit of damage,
/// the payment, and what a payment repairs. Damage is repaired in the order structure, armor, shield.
/// </summary>
public static class RepairRules
{
    /// <summary>The station's share of an item's base price that one full repair costs (EVE.js <c>STATION_REPAIR_COST_MULTIPLIER</c>).</summary>
    public const double StationCostMultiplier = 0.1;

    /// <summary>
    /// An item's health: the HP of its shield, armor and structure (dogma attributes), and what it has taken as fractions of each
    /// (the shield's charge, the armor and structure damage) — the condition the module state store keeps.
    /// </summary>
    public sealed record Health(double ShieldCapacity, double ArmorHp, double StructureHp, double ShieldCharge, double ArmorDamage, double StructureDamage)
    {
        public double MaxHealth => ShieldCapacity + ArmorHp + StructureHp;
        public double ShieldDamage => ShieldCapacity * (1 - Clamp01(ShieldCharge));
        public double ArmorDamageHp => ArmorHp * Clamp01(ArmorDamage);
        public double StructureDamageHp => StructureHp * Clamp01(StructureDamage);

        /// <summary>The HP the item has lost: shield, armor and structure together.</summary>
        public double Damage => ShieldDamage + ArmorDamageHp + StructureDamageHp;
    }

    /// <summary>What the state is after a repair: the new fractions of the structure damage, the armor damage and the shield charge.</summary>
    public sealed record Repaired(double StructureDamage, double ArmorDamage, double ShieldCharge);

    /// <summary>One item of a repair: its damage, the cost of one unit of it, and the cost of all of it (rounded to cents).</summary>
    public sealed record Target(long ItemId, bool IsModule, double Damage, double UnitCost)
    {
        public double FullCost => Round2(Math.Ceiling(Damage) * UnitCost);
    }

    /// <summary>What a payment repairs of one target, and what it is charged.</summary>
    public sealed record Step(long ItemId, double RepairAmount, double Charge);

    /// <summary>The cost of one unit of damage: a tenth of the base price, spread over the HP of the item (rounded to six decimals).</summary>
    public static double UnitCost(double basePrice, double maxHealth) =>
        basePrice > 0 && maxHealth > 0 ? Round6(basePrice * StationCostMultiplier / maxHealth) : 0;

    /// <summary>
    /// What a repair of <paramref name="health"/> leaves when <paramref name="amount"/> HP are repaired: the structure first, then the armor,
    /// then the shield (the reference's <c>applyRepairToHealthSnapshot</c>).
    /// </summary>
    public static Repaired Apply(Health health, double amount)
    {
        var remaining = Math.Max(0, amount);
        var repairStructure = Math.Min(health.StructureDamageHp, remaining);
        remaining -= repairStructure;
        var repairArmor = Math.Min(health.ArmorDamageHp, remaining);
        remaining -= repairArmor;
        var repairShield = Math.Min(health.ShieldDamage, remaining);

        var nextStructure = health.StructureDamageHp - repairStructure;
        var nextArmor = health.ArmorDamageHp - repairArmor;
        var nextShield = health.ShieldDamage - repairShield;
        return new Repaired(
            health.StructureHp > 0 ? Clamp01(nextStructure / health.StructureHp) : 0,
            health.ArmorHp > 0 ? Clamp01(nextArmor / health.ArmorHp) : 0,
            health.ShieldCapacity > 0 ? Clamp01(1 - nextShield / health.ShieldCapacity) : 1);
    }

    /// <summary>
    /// The steps of a repair of <paramref name="targets"/> with a payment, in the order given: each target is charged the part of the payment it
    /// can take, up to its full cost, and repairs what that buys. A repair with a fitted module is paid in full (<see cref="InvalidOperationException"/>
    /// with the reference's <c>MODULE_PARTIAL_REPAIR</c> when the payment is short).
    /// </summary>
    public static IReadOnlyList<Step> Plan(IReadOnlyList<Target> targets, double payment)
    {
        var totalFull = Round2(targets.Sum(t => t.FullCost));
        if (targets.Any(t => t.IsModule) && payment + 0.0001 < totalFull)
            throw new InvalidOperationException("MODULE_PARTIAL_REPAIR");

        var remaining = Math.Min(Math.Max(0, payment), totalFull);
        var steps = new List<Step>();
        foreach (var target in targets)
        {
            if (!(target.Damage > 0)) continue;
            var full = target.FullCost;
            var charge = Math.Min(remaining, full);
            if (!(charge > 0)) continue;
            var amount = charge >= full ? target.Damage : Math.Min(target.Damage, charge / target.UnitCost);
            remaining = Round2(Math.Max(0, remaining - charge));
            if (!(amount > 0)) continue;
            steps.Add(new Step(target.ItemId, amount, Round2(charge)));
        }
        return steps;
    }

    public static double Round2(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static double Round6(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static double Clamp01(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}
