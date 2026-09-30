namespace Eve.Fitting;

/// <summary>What a ship has taken and what it has left: the fractions dogma reports next to its attributes.</summary>
public sealed record ShipCondition(double Damage, double Charge, double ShieldCharge, double ArmorDamage, bool Incapacitated)
{
    /// <summary>A ship as it leaves the hangar: nothing damaged, capacitor and shield full.</summary>
    public static readonly ShipCondition Full = new(0, 1, 1, 0, false);
}

/// <summary>What a fitted module has taken, as fractions. A module nobody has damaged or mended has none of the shield a mended one has.</summary>
public sealed record ModuleCondition(double Damage, double ArmorDamage, double ShieldCharge, bool Incapacitated)
{
    /// <summary>A module nobody has touched.</summary>
    public static readonly ModuleCondition Untouched = new(0, 0, 0, false);

    /// <summary>A module that has been mended: nothing damaged, its shield full.</summary>
    public static readonly ModuleCondition Mended = new(0, 0, 1, false);
}
