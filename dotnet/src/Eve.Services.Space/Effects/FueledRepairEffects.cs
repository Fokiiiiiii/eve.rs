using Eve.Space;

namespace Eve.Services.Space.Effects;

/// <summary>
/// An ancillary armor repairer (<c>fueledArmorRepair</c>): the armor repairer's package (<see cref="RepairEffect"/>). With a charge loaded the cycle mends
/// the charged amount and spends the charge; without one it mends the module's plain points, as an armor repairer does.
/// </summary>
internal sealed class FueledArmorRepairEffect : ShipEffect
{
    private const int ChargedArmorDamageMultiplier = 1886;

    public override IReadOnlyCollection<string> EffectNames { get; } = ["fueledArmorRepair"];

    protected override SimEffect Build(ModuleEffectContext module)
    {
        var repair = RepairEffect.RepairAction(module, "armorRepair");
        var charge = module.Snapshot.ChargeOf(module.Module);
        var fallback = repair with { Amount = Math.Max(0, module.Value(ModuleAttributes.ArmorDamageAmount, 0)) };
        var charged = repair with { Amount = ChargedAmount(module) };
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        SimAction action = charge is { Quantity: > 0 }
            ? new SimAction.Charged(charged, fallback, charge.TypeId, module.Module.Flag, (int)chargeRate, charge.Quantity)
            : fallback;
        return RepairEffect.Package(module, action);
    }

    /// <summary>The points the armor repairer mends with its charge loaded: its own points, raised by the charge's multiplier for the part of a charge the cycle uses.</summary>
    internal static double ChargedAmount(ModuleEffectContext module)
    {
        var baseAmount = Math.Max(0, module.Value(ModuleAttributes.ArmorDamageAmount, 0));
        var multiplier = Math.Max(1, module.Value(ChargedArmorDamageMultiplier, 1));
        var chargeRate = Math.Max(1L, (long)module.Value(ModuleAttributes.ChargeRate, 1));
        var charge = module.Snapshot.ChargeOf(module.Module);
        var chargedUnits = Math.Min(chargeRate, Math.Max(0, charge?.Quantity ?? 0));
        var chargedFraction = (double)chargedUnits / chargeRate;
        return Math.Max(0, ModuleAttributes.Six(baseAmount + (baseAmount * (multiplier - 1) * chargedFraction)));
    }
}

/// <summary>
/// An ancillary shield booster (<c>fueledShieldBoosting</c>): the shield booster's package (<see cref="RepairEffect"/>). The package does not spend a
/// charge loaded in the module, as it did not before this effect became a handler.
/// </summary>
internal sealed class FueledShieldBoostingEffect : ShipEffect
{
    public override IReadOnlyCollection<string> EffectNames { get; } = ["fueledShieldBoosting"];

    protected override SimEffect Build(ModuleEffectContext module) =>
        RepairEffect.Package(module, RepairEffect.RepairAction(module, "shieldBoosting"));
}
