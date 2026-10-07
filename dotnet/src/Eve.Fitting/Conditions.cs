namespace Eve.Fitting;

/// <summary>What a ship has taken and what it has left: the fractions dogma reports next to its attributes.</summary>
public sealed record ShipCondition(double Damage, double Charge, double ShieldCharge, double ArmorDamage, bool Incapacitated)
{
    /// <summary>A ship as it leaves the hangar: nothing damaged, capacitor and shield full.</summary>
    public static readonly ShipCondition Full = new(0, 1, 1, 0, false);

    /// <summary>
    /// Keeps the absolute shield, armor and hull damage when a pilot or module modifier changes maximum HP.
    /// Capacitor charge remains a fraction of its new capacity.
    /// </summary>
    public (ShipCondition Condition, bool HullCollapsed) RebaseHitpoints(
        double oldHull, double oldArmor, double oldShield,
        double newHull, double newArmor, double newShield)
    {
        if (newHull <= 0 || newArmor <= 0 || newShield <= 0
            || oldHull <= 0 || oldArmor <= 0 || oldShield <= 0
            || !double.IsFinite(oldHull) || !double.IsFinite(oldArmor) || !double.IsFinite(oldShield)
            || !double.IsFinite(newHull) || !double.IsFinite(newArmor) || !double.IsFinite(newShield))
        {
            throw new ArgumentOutOfRangeException(nameof(newHull), "ship hitpoint capacities must be positive and finite");
        }

        var hullDamage = Damage * oldHull;
        var armorDamage = ArmorDamage * oldArmor;
        var shieldDamage = (1 - ShieldCharge) * oldShield;
        var rebased = this with
        {
            Damage = hullDamage / newHull,
            ArmorDamage = armorDamage / newArmor,
            ShieldCharge = 1 - shieldDamage / newShield,
        };
        return (rebased, hullDamage >= newHull);
    }
}

/// <summary>What a fitted module has taken, as fractions. A module nobody has damaged or mended has none of the shield a mended one has.</summary>
public sealed record ModuleCondition(double Damage, double ArmorDamage, double ShieldCharge, bool Incapacitated)
{
    /// <summary>A module nobody has touched.</summary>
    public static readonly ModuleCondition Untouched = new(0, 0, 0, false);

    /// <summary>A module that has been mended: nothing damaged, its shield full.</summary>
    public static readonly ModuleCondition Mended = new(0, 0, 1, false);
}
